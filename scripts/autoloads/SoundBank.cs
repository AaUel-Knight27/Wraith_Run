using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GameAudio;

/// <summary>
/// How a family of sounds behaves, chosen by the FIRST folder of its id:
/// "weapons/kar98k/fire" -> category "weapons". Add a line to <see cref="Table"/> for a new top-level folder.
///
/// Buses are this project's own (default_bus_layout.tres), not the pack's original generic
/// "SFX"/"Footsteps" pair:
///   - surfaces (footsteps, landing) and player (slide, shoulder push, low hop, self-revive) both
///     go to "Movement" - the bus SoundManager's header comment already explains has a native
///     sidechain compressor ducking it from "Weapons", so a nearby gunshot masks a footstep for
///     free. A second, unducked "Footsteps" bus would have quietly defeated that.
///   - casings/impacts/explosions/bomb/grenade/pickups are all combat feedback and share
///     "Weapons" with the weapons category itself.
///   - gameplay (the kill stinger) joins ui on "UI" - same non-positional feedback treatment the
///     existing hit marker already gets.
/// </summary>
public sealed record SoundCategory(
    string Bus,
    float PitchVar = 0.04f,      // random pitch +-  (0.04 = +-4 %)
    float VolVarDb = 1f,         // random volume +- in dB
    int MaxVoices = 4,           // copies of the SAME event allowed at once
    float MaxDistance = 40f,     // metres in 3D (multiplied by Audio.PixelsPerUnit in 2D)
    float UnitSize = 8f,         // 3D attenuation: bigger = carries further before fading
    float TrimDb = 0f)           // category-wide gain fix (keep <= 0: your files are peak-normalised to 0 dBFS)
{
    static readonly Dictionary<string, SoundCategory> Table = new()
    {
        ["weapons"]    = new("Weapons",  0.03f, 1.0f, 8, 150f, 15f),
        ["surfaces"]   = new("Movement", 0.08f, 1.5f, 6,  25f,  5f),
        ["casings"]    = new("Weapons",  0.10f, 2.0f, 4,  15f,  3f),
        ["voice"]      = new("Voice",    0.03f, 0.5f, 2,  50f, 10f),
        ["ui"]         = new("UI",       0.00f, 0.0f, 2,   0f,  1f),
        ["gameplay"]   = new("UI",       0.00f, 0.0f, 2,   0f,  1f),
        ["explosions"] = new("Weapons",  0.05f, 1.0f, 3, 250f, 40f),
        ["impacts"]    = new("Weapons",  0.06f, 1.5f, 6,  40f,  6f),
        ["bomb"]       = new("Weapons",  0.00f, 0.0f, 2,  60f, 10f),
        ["grenade"]    = new("Weapons",  0.04f, 1.0f, 3,  60f, 10f),
        ["player"]     = new("Movement", 0.05f, 1.0f, 3,  25f,  5f),
        ["pickups"]    = new("Weapons",  0.03f, 1.0f, 3,  20f,  4f),
    };

    public static readonly SoundCategory Default = new("Weapons");
    public static SoundCategory For(string category) => Table.TryGetValue(category, out var c) ? c : Default;
}

/// <summary>One logical sound = a folder-of-variants (or several files sharing a name + _NN suffix).</summary>
public sealed class SoundEvent
{
    public string Id { get; }
    public string Category { get; }
    public int VariantCount => _paths.Count;

    /// <summary>Per-event gain fix in dB (see Audio.EventTrimDb). Keep it &lt;= 0.</summary>
    public float TrimDb;

    readonly List<string> _paths = new();
    AudioStream[] _streams;     // loaded on first use
    int _last = -1;

    public SoundEvent(string id)
    {
        Id = id;
        int slash = id.IndexOf('/');
        Category = slash < 0 ? id : id.Substring(0, slash);
    }

    internal void AddPath(string path)
    {
        if (!_paths.Contains(path)) _paths.Add(path);
    }

    /// <summary>Random variant that is never the same one twice in a row.</summary>
    public AudioStream Pick()
    {
        _streams ??= LoadAll();
        if (_streams.Length == 0) return null;
        if (_streams.Length == 1) return _streams[0];

        int i;
        do { i = (int)(GD.Randi() % (uint)_streams.Length); } while (i == _last);
        _last = i;
        return _streams[i];
    }

    /// <summary>Load now instead of on first play (call before a level starts to avoid a first-use hitch).</summary>
    public void Preload() => _streams ??= LoadAll();

    AudioStream[] LoadAll() =>
        _paths.Select(p => ResourceLoader.Load<AudioStream>(p)).Where(s => s != null).ToArray();
}

/// <summary>
/// Finds every sound under a folder and groups it into events by name:
///   res://audio/SFX/weapons/kar98k/fire_01.wav, fire_02.wav, fire_03.wav  ->  event "weapons/kar98k/fire" (3 variants)
///   res://audio/SFX/ui/hitmark.wav                                          ->  event "ui/hitmark"        (1 variant)
/// Adding a sound = drop the file in the right folder. No code or resource editing.
/// </summary>
public sealed class SoundBank
{
    static readonly Regex VariantSuffix = new(@"_\d+$", RegexOptions.Compiled);
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".wav", ".ogg", ".mp3" };

    readonly Dictionary<string, SoundEvent> _events = new();

    public int Count => _events.Count;
    public IEnumerable<SoundEvent> Events => _events.Values.OrderBy(e => e.Id, StringComparer.Ordinal);
    public bool Has(string id) => id != null && _events.ContainsKey(id);
    public bool TryGet(string id, out SoundEvent ev)
    {
        if (id == null) { ev = null; return false; }
        return _events.TryGetValue(id, out ev);
    }

    public void Scan(string root)
    {
        root = root.TrimEnd('/');
        ScanDirectory(root, root);
    }

    void ScanDirectory(string dir, string root)
    {
        // ResourceLoader.ListDirectory (Godot 4.4+) also works in exported builds,
        // unlike DirAccess on res:// where you would see ".import"/".remap" names.
        var entries = ResourceLoader.ListDirectory(dir);
        Array.Sort(entries, StringComparer.Ordinal);   // stable variant order

        foreach (var entry in entries)
        {
            if (entry.EndsWith('/'))
            {
                ScanDirectory($"{dir}/{entry.TrimEnd('/')}", root);
                continue;
            }

            var ext = Path.GetExtension(entry);
            if (!Extensions.Contains(ext)) continue;

            var full = $"{dir}/{entry}";
            var relative = full.Substring(root.Length + 1);                              // weapons/kar98k/fire_01.wav
            var noExt = relative.Substring(0, relative.Length - ext.Length);             // weapons/kar98k/fire_01
            var id = VariantSuffix.Replace(noExt, "");                                   // weapons/kar98k/fire

            if (!_events.TryGetValue(id, out var ev))
                _events[id] = ev = new SoundEvent(id);
            ev.AddPath(full);
        }
    }
}
