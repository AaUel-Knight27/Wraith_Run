using Godot;
using System.Collections.Generic;

namespace GameAudio;

/// <summary>
/// Autoload (see project.godot's [autoload] section). General-purpose sound bank for everything
/// that is NOT a weapon's own fire sound: footsteps/landing, ADS in/out, casings, voice lines,
/// UI stingers, impacts, and whatever else lives under res://audio/SFX. Drop a file in the right
/// folder and it is playable - no resource or code editing.
///
///   Audio.Play("ui/hitmark");                                    // not positional (UI, your own voice, ...)
///   Audio.PlayAt("surfaces/concrete/step", feetPosition);        // 3D positional
///   Audio.PlayAt(Audio.WeaponId("ak", "bolt_close"), muzzlePos); // falls back to shared/generic
///
/// Deliberately separate from SoundManager (scripts/autoloads/SoundManager.cs), which owns a
/// weapon's actual gunshot report - inverse-square falloff, occlusion, the crack/boom delay past
/// range. That is bespoke physics for one sound per shot; this is a drop-in bank for everything
/// else, which does not need any of that.
///
/// Ported from a generic pack (see the project's audio import notes): the only project-specific
/// changes are SfxRoot's casing (res://audio/SFX, lowercase "audio" - the pack shipped
/// "res://Audio/SFX", which matches on Windows' case-insensitive filesystem but silently finds
/// nothing on Linux/exported builds, where res:// paths are case-sensitive - this project already
/// uses a lowercase "audio/" folder for its own clips) and EnsureBuses below, which now points at
/// this project's existing bus names instead of creating redundant new ones.
/// </summary>
public partial class Audio : Node
{
    // ----- settings you will probably touch -----
    public const string SfxRoot = "res://audio/SFX";
    public static float PixelsPerUnit = 100f;                 // 2D only: how many pixels count as "one metre"
    const int FlatVoiceCount = 16, WorldVoiceCount = 32;

    /// <summary>Per-event gain fixes for files that are too hot, in dB (negative only). Example: ["ui/hitmark"] = -6f</summary>
    public static readonly Dictionary<string, float> EventTrimDb = new()
    {
        // ["ui/hitmark"] = -6f,
    };

    // ----- internals -----
    static Audio _i;
    readonly SoundBank _bank = new();
    readonly List<Voice> _flat = new(), _3d = new(), _2d = new();
    readonly HashSet<string> _warned = new();
    AudioStreamPlayer _musicA, _musicB, _music;

    public override void _Ready()
    {
        _i = this;
        ProcessMode = ProcessModeEnum.Always;      // UI sounds and music keep working while the game is paused

        EnsureBuses();
        _bank.Scan(SfxRoot);
        foreach (var ev in _bank.Events)
            if (EventTrimDb.TryGetValue(ev.Id, out var trim)) ev.TrimDb = trim;
        GD.Print($"[Audio] {_bank.Count} sound events loaded from {SfxRoot}");

        for (int i = 0; i < FlatVoiceCount; i++) _flat.Add(new FlatVoice(this));
        for (int i = 0; i < WorldVoiceCount; i++) { _3d.Add(new Voice3D(this)); _2d.Add(new Voice2D(this)); }

        _musicA = NewMusicPlayer();
        _musicB = NewMusicPlayer();
        _music = _musicA;
    }

    public override void _ExitTree()
    {
        if (_i == this) _i = null;
    }

    // =====================================================================================
    // Public API (static, so gameplay code just says Audio.Something)
    // =====================================================================================

    /// <summary>Play a non-positional sound.</summary>
    public static void Play(string id, float volumeDb = 0f, float pitch = 1f) => _i?.PlayFlat(id, volumeDb, pitch);

    /// <summary>Play a sound at a 3D position.</summary>
    public static void PlayAt(string id, Vector3 position, float volumeDb = 0f, float pitch = 1f) =>
        _i?.PlayWorld3D(id, position, volumeDb, pitch);

    /// <summary>Play a sound at a 2D position.</summary>
    public static void PlayAt(string id, Vector2 position, float volumeDb = 0f, float pitch = 1f) =>
        _i?.PlayWorld2D(id, position, volumeDb, pitch);

    public static bool Has(string id) => _i != null && _i._bank.Has(id);

    /// <summary>
    /// Picks the best existing sound for a weapon action, or null if there is none:
    /// weapons/{weapon}/{action}  ->  weapons/shared/{action}  ->  weapons/generic/{action}.
    /// Actions: fire, fire_suppressed, mag_in, mag_out, bolt_open, bolt_close, shell_in, equip, drop.
    /// </summary>
    public static string WeaponId(string weapon, string action)
    {
        if (_i == null) return null;
        foreach (var candidate in new[] { $"weapons/{weapon}/{action}", $"weapons/shared/{action}", $"weapons/generic/{action}" })
            if (_i._bank.Has(candidate)) return candidate;
        return null;
    }

    /// <summary>Load every variant of every event whose id starts with <paramref name="prefix"/> (e.g. "weapons/kar98k/").</summary>
    public static void Preload(string prefix)
    {
        if (_i == null) return;
        foreach (var ev in _i._bank.Events)
            if (ev.Id.StartsWith(prefix)) ev.Preload();
    }

    /// <summary>Print every event id and how many variants it has (handy to check for typos).</summary>
    public static void DumpEvents()
    {
        if (_i == null) return;
        foreach (var ev in _i._bank.Events) GD.Print($"{ev.Id}  x{ev.VariantCount}");
    }

    /// <summary>Settings slider helper. busName: Master, Music, Weapons, Movement, Voice, UI.</summary>
    public static void SetBusVolume(string busName, float linear)
    {
        int idx = AudioServer.GetBusIndex(busName);
        if (idx < 0) return;
        AudioServer.SetBusVolumeDb(idx, Mathf.LinearToDb(linear));
        AudioServer.SetBusMute(idx, linear <= 0.001f);
    }

    /// <summary>Crossfade to another music track.</summary>
    public static void PlayMusic(AudioStream stream, float fadeSeconds = 1.5f) => _i?.CrossfadeMusic(stream, fadeSeconds);

    // =====================================================================================
    // Playing
    // =====================================================================================

    void PlayFlat(string id, float volumeDb, float pitch)
    {
        if (!Resolve(id, out var ev, out var cat, out var stream)) return;
        var voice = Acquire(_flat, ev, cat.MaxVoices);
        Begin(voice, ev, cat, stream, volumeDb, pitch);
    }

    void PlayWorld3D(string id, Vector3 position, float volumeDb, float pitch)
    {
        if (!Resolve(id, out var ev, out var cat, out var stream)) return;
        var voice = (Voice3D)Acquire(_3d, ev, cat.MaxVoices);
        voice.SetPosition(position);
        Begin(voice, ev, cat, stream, volumeDb, pitch);
    }

    void PlayWorld2D(string id, Vector2 position, float volumeDb, float pitch)
    {
        if (!Resolve(id, out var ev, out var cat, out var stream)) return;
        var voice = (Voice2D)Acquire(_2d, ev, cat.MaxVoices);
        voice.SetPosition(position);
        Begin(voice, ev, cat, stream, volumeDb, pitch);
    }

    bool Resolve(string id, out SoundEvent ev, out SoundCategory cat, out AudioStream stream)
    {
        ev = null; cat = null; stream = null;
        if (id == null) return false;                       // WeaponId() found nothing: stay silent on purpose
        if (!_bank.TryGet(id, out ev))
        {
            if (_warned.Add(id)) GD.PushWarning($"[Audio] unknown sound id '{id}'");
            return false;
        }
        cat = SoundCategory.For(ev.Category);
        stream = ev.Pick();
        return stream != null;
    }

    static void Begin(Voice voice, SoundEvent ev, SoundCategory cat, AudioStream stream, float volumeDb, float pitch)
    {
        float db = volumeDb + cat.TrimDb + ev.TrimDb + (float)GD.RandRange(-cat.VolVarDb, cat.VolVarDb);
        float p = pitch * (1f + (float)GD.RandRange(-cat.PitchVar, cat.PitchVar));

        voice.Event = ev;
        voice.Started = Time.GetTicksMsec();
        voice.Busy = true;
        voice.Start(stream, cat, db, p);
    }

    /// <summary>
    /// Free voice if there is one. If this event already has its max copies playing, restart its oldest copy.
    /// If everything is busy, steal the oldest voice.
    /// </summary>
    static Voice Acquire(List<Voice> pool, SoundEvent ev, int maxPerEvent)
    {
        Voice free = null, oldestSame = null, oldestAny = null;
        int same = 0;

        foreach (var v in pool)
        {
            if (!v.Busy) { free ??= v; continue; }
            if (v.Event == ev)
            {
                same++;
                if (oldestSame == null || v.Started < oldestSame.Started) oldestSame = v;
            }
            if (oldestAny == null || v.Started < oldestAny.Started) oldestAny = v;
        }

        if (same >= maxPerEvent && oldestSame != null) return oldestSame;
        return free ?? oldestAny;
    }

    // =====================================================================================
    // Buses
    // =====================================================================================

    /// <summary>
    /// This project already defines its full mixer layout in default_bus_layout.tres (Weapons,
    /// Movement, Voice, UI, Music, plus the reverb sends) - see SoundCategory.Table below, which
    /// routes every event at one of those existing buses rather than the generic "SFX"/"Footsteps"
    /// pair the pack shipped with, so footsteps keep landing on "Movement" (the bus with the
    /// gunfire-sidechained ducking compressor) instead of a second, unducked bus. This is a safety
    /// net only, for whichever of these happens to be missing; it adds nothing today.
    /// </summary>
    static void EnsureBuses()
    {
        (string Name, string Send)[] layout =
        {
            ("Music", "Master"), ("Weapons", "Master"), ("Movement", "Master"), ("Voice", "Master"), ("UI", "Master"),
        };

        foreach (var (name, send) in layout)
        {
            if (AudioServer.GetBusIndex(name) >= 0) continue;
            int idx = AudioServer.BusCount;
            AudioServer.AddBus();
            AudioServer.SetBusName(idx, name);
            AudioServer.SetBusSend(idx, send);
        }
    }

    // =====================================================================================
    // Music
    // =====================================================================================

    AudioStreamPlayer NewMusicPlayer()
    {
        var p = new AudioStreamPlayer { Bus = "Music", VolumeDb = -80f };
        AddChild(p);
        return p;
    }

    void CrossfadeMusic(AudioStream stream, float fade)
    {
        var old = _music;
        var next = old == _musicA ? _musicB : _musicA;

        next.Stream = stream;
        next.VolumeDb = -80f;
        next.Play();

        var tween = CreateTween().SetParallel(true);
        tween.TweenProperty(next, "volume_db", 0f, fade);
        tween.TweenProperty(old, "volume_db", -80f, fade);
        tween.Chain().TweenCallback(Callable.From(old.Stop));
        _music = next;
    }

    // =====================================================================================
    // Voices: one player that can be reused. Three flavours, same interface.
    // =====================================================================================

    abstract class Voice
    {
        public SoundEvent Event;
        public ulong Started;
        public bool Busy;
        public abstract void Start(AudioStream stream, SoundCategory cat, float volumeDb, float pitch);
    }

    sealed class FlatVoice : Voice
    {
        readonly AudioStreamPlayer _p = new();

        public FlatVoice(Node parent)
        {
            parent.AddChild(_p);
            _p.Finished += () => Busy = false;
        }

        public override void Start(AudioStream stream, SoundCategory cat, float volumeDb, float pitch)
        {
            _p.Stop();                            // makes reusing a voice that is still playing safe
            _p.Stream = stream;
            _p.Bus = cat.Bus;
            _p.VolumeDb = volumeDb;
            _p.PitchScale = pitch;
            _p.Play();
        }
    }

    sealed class Voice3D : Voice
    {
        readonly AudioStreamPlayer3D _p = new();

        public Voice3D(Node parent)
        {
            parent.AddChild(_p);
            _p.Finished += () => Busy = false;
        }

        public void SetPosition(Vector3 position) => _p.GlobalPosition = position;

        public override void Start(AudioStream stream, SoundCategory cat, float volumeDb, float pitch)
        {
            _p.Stop();                            // makes reusing a voice that is still playing safe
            _p.Stream = stream;
            _p.Bus = cat.Bus;
            _p.VolumeDb = volumeDb;
            _p.PitchScale = pitch;
            _p.MaxDistance = cat.MaxDistance;     // 0 = unlimited
            _p.UnitSize = cat.UnitSize;
            _p.Play();
        }
    }

    sealed class Voice2D : Voice
    {
        readonly AudioStreamPlayer2D _p = new();

        public Voice2D(Node parent)
        {
            parent.AddChild(_p);
            _p.Finished += () => Busy = false;
        }

        public void SetPosition(Vector2 position) => _p.GlobalPosition = position;

        public override void Start(AudioStream stream, SoundCategory cat, float volumeDb, float pitch)
        {
            _p.Stop();                            // makes reusing a voice that is still playing safe
            _p.Stream = stream;
            _p.Bus = cat.Bus;
            _p.VolumeDb = volumeDb;
            _p.PitchScale = pitch;
            _p.MaxDistance = cat.MaxDistance > 0f ? cat.MaxDistance * PixelsPerUnit : 100000f;
            _p.Play();
        }
    }
}
