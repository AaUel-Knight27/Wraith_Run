using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// What the player picked in the lobby: which operator, the three weapon sets (their names and
/// weapons) and which set is active, and the colours they dressed each operator in. Saved to
/// user://profile.cfg (separate from settings.cfg so a settings reset never wipes a loadout). A
/// plain static class rather than an autoload: nothing here needs _Process or signals, and it keeps
/// project.godot untouched.
///
/// An empty operator id means "Standard Issue" - no perks and no drawbacks, exactly how the game
/// played before the operator roster existed.
///
/// Weapon sets: three sets, each holding one weapon per WeaponSlot. Whatever is read from disk is
/// checked against WeaponRules (right kind of weapon for the slot, no weapon twice in one set) and
/// repaired from the defaults if it does not pass, so a hand-edited or out-of-date file can never
/// put an illegal loadout into a match.
/// </summary>
public static class PlayerProfile
{
    private const string FilePath = "user://profile.cfg";
    private const string SetsSection = "weapon_sets";

    private static bool _loaded;
    private static string _operatorId = string.Empty;
    private static int _activeSet = WeaponRules.DefaultActiveSet;
    private static readonly string[] _setNames = NewNames();
    private static readonly string[][] _setWeapons = NewWeapons();
    private static readonly Dictionary<string, (Color Jacket, Color Pants, Color Accent)> _colors = new();

    private static string[] NewNames()
    {
        var names = new string[WeaponRules.SetCount];
        Array.Fill(names, string.Empty); // empty = use the default name
        return names;
    }

    private static string[][] NewWeapons()
    {
        var sets = new string[WeaponRules.SetCount][];
        for (int i = 0; i < sets.Length; i++) sets[i] = (string[])WeaponRules.DefaultWeapons[i].Clone();
        return sets;
    }

    public static string OperatorId
    {
        get { EnsureLoaded(); return _operatorId; }
        set { EnsureLoaded(); _operatorId = value ?? string.Empty; Save(); }
    }

    // ---- weapon sets ------------------------------------------------------------------------

    public static int ActiveWeaponSet
    {
        get { EnsureLoaded(); return _activeSet; }
        set
        {
            EnsureLoaded();
            int clamped = Math.Clamp(value, 0, WeaponRules.SetCount - 1);
            if (clamped == _activeSet) return;
            _activeSet = clamped;
            Save();
        }
    }

    /// <summary>The name shown for a set: the player's own if they typed one, else SHORT / MID / LONG RANGE.</summary>
    public static string GetSetName(int set)
    {
        EnsureLoaded();
        set = Math.Clamp(set, 0, WeaponRules.SetCount - 1);
        return string.IsNullOrEmpty(_setNames[set]) ? WeaponRules.DefaultSetNames[set] : _setNames[set];
    }

    /// <summary>Renames a set. A blank name puts the default back.</summary>
    public static void SetSetName(int set, string? name)
    {
        EnsureLoaded();
        if (set < 0 || set >= WeaponRules.SetCount) return;
        string clean = WeaponRules.CleanSetName(name);
        if (clean == WeaponRules.DefaultSetNames[set]) clean = string.Empty;
        if (clean == _setNames[set]) return;
        _setNames[set] = clean;
        Save();
    }

    public static string GetSetWeapon(int set, WeaponSlot slot)
    {
        EnsureLoaded();
        set = Math.Clamp(set, 0, WeaponRules.SetCount - 1);
        return _setWeapons[set][(int)slot];
    }

    /// <summary>A copy of a set's five weapon names, for the rule checks that need the whole set.</summary>
    public static string[] GetSetWeapons(int set)
    {
        EnsureLoaded();
        set = Math.Clamp(set, 0, WeaponRules.SetCount - 1);
        return (string[])_setWeapons[set].Clone();
    }

    /// <summary>Puts a weapon in a slot of a set. Returns false (and changes nothing) if the rules
    /// refuse it: wrong kind of weapon for the slot, or already in the other primary slot.</summary>
    public static bool SetSetWeapon(int set, WeaponSlot slot, string weaponName)
    {
        EnsureLoaded();
        if (set < 0 || set >= WeaponRules.SetCount) return false;
        if (!WeaponRules.IsValidFor(slot, weaponName, _setWeapons[set])) return false;
        if (_setWeapons[set][(int)slot] == weaponName) return true;
        _setWeapons[set][(int)slot] = weaponName;
        Save();
        return true;
    }

    /// <summary>The weapon a match starts you holding: Primary 1 of the active set.</summary>
    public static string StartingWeaponName
    {
        get { EnsureLoaded(); return _setWeapons[_activeSet][(int)WeaponSlot.Primary1]; }
    }

    // ---- colours ----------------------------------------------------------------------------

    private static string Key(string operatorId) => string.IsNullOrEmpty(operatorId) ? "standard" : operatorId;

    /// <summary>The player's own colours for this operator, if they ever changed them.</summary>
    public static bool TryGetColors(string operatorId, out Color jacket, out Color pants, out Color accent)
    {
        EnsureLoaded();
        if (_colors.TryGetValue(Key(operatorId), out var set))
        {
            jacket = set.Jacket; pants = set.Pants; accent = set.Accent;
            return true;
        }
        jacket = pants = accent = Godot.Colors.White;
        return false;
    }

    public static void SetColors(string operatorId, Color jacket, Color pants, Color accent)
    {
        EnsureLoaded();
        _colors[Key(operatorId)] = (jacket, pants, accent);
        Save();
    }

    public static void ClearColors(string operatorId)
    {
        EnsureLoaded();
        if (_colors.Remove(Key(operatorId))) Save();
    }

    // ---- load / save ------------------------------------------------------------------------

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        var file = new ConfigFile();
        if (file.Load(FilePath) != Error.Ok)
        {
            // First run: defaults, already in place. Still run them through the rules once so a
            // registry change can never leave an illegal default behind.
            RepairSets();
            return;
        }

        _operatorId = file.GetValue("profile", "operator", string.Empty).AsString();

        bool hasSets = file.HasSection(SetsSection);
        if (hasSets)
        {
            _activeSet = Math.Clamp(file.GetValue(SetsSection, "active", WeaponRules.DefaultActiveSet).AsInt32(),
                0, WeaponRules.SetCount - 1);
            for (int set = 0; set < WeaponRules.SetCount; set++)
            {
                _setNames[set] = WeaponRules.CleanSetName(file.GetValue(SetsSection, $"name_{set}", string.Empty).AsString());
                if (_setNames[set] == WeaponRules.DefaultSetNames[set]) _setNames[set] = string.Empty;
                for (int slot = 0; slot < WeaponRules.SlotCount; slot++)
                    _setWeapons[set][slot] = file.GetValue(SetsSection, $"set{set}_slot{slot}",
                        WeaponRules.DefaultWeapons[set][slot]).AsString();
            }
        }

        RepairSets();

        // A profile from before weapon sets stored one starting weapon. Keep it: it becomes
        // Primary 1 of the active set (if it is a primary gun).
        if (!hasSets)
        {
            string legacy = file.GetValue("profile", "weapon", string.Empty).AsString();
            AdoptLegacyWeapon(legacy);
        }

        if (!file.HasSection("colors")) return;
        foreach (string key in file.GetSectionKeys("colors"))
        {
            string[] parts = file.GetValue("colors", key, string.Empty).AsString().Split(',');
            if (parts.Length != 3) continue;
            _colors[key] = (Color.FromHtml(parts[0]), Color.FromHtml(parts[1]), Color.FromHtml(parts[2]));
        }
    }

    private static void AdoptLegacyWeapon(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        WeaponData? weapon = OperatorInfo.FindWeapon(name);
        if (weapon == null || !WeaponRules.Fits(weapon, WeaponSlot.Primary1)) return;
        if (Array.IndexOf(WeaponSwitcher.Loadout, name) < 0) return;

        string[] set = _setWeapons[_activeSet];
        if (set[(int)WeaponSlot.Primary1] == name) return;
        // Already the second primary: swap, so the set never holds it twice.
        if (set[(int)WeaponSlot.Primary2] == name) set[(int)WeaponSlot.Primary2] = set[(int)WeaponSlot.Primary1];
        set[(int)WeaponSlot.Primary1] = name;
    }

    /// <summary>Makes every set legal: each slot keeps its saved weapon if the rules allow it, else
    /// takes the default for that slot, else the first weapon that fits.</summary>
    private static void RepairSets()
    {
        for (int set = 0; set < WeaponRules.SetCount; set++)
        {
            var chosen = new string?[WeaponRules.SlotCount];
            for (int slot = 0; slot < WeaponRules.SlotCount; slot++)
                chosen[slot] = ResolveSlot(set, (WeaponSlot)slot, _setWeapons[set][slot], chosen);
            for (int slot = 0; slot < WeaponRules.SlotCount; slot++)
                _setWeapons[set][slot] = chosen[slot] ?? WeaponRules.DefaultWeapons[set][slot];
        }
    }

    private static string ResolveSlot(int set, WeaponSlot slot, string? stored, string?[] taken)
    {
        if (WeaponRules.IsValidFor(slot, stored, taken)) return stored!;
        string fallback = WeaponRules.DefaultWeapons[set][(int)slot];
        if (WeaponRules.IsValidFor(slot, fallback, taken)) return fallback;
        foreach (WeaponData weapon in WeaponRules.WeaponsFor(slot))
            if (WeaponRules.IsValidFor(slot, weapon.WeaponName, taken)) return weapon.WeaponName;
        return fallback;
    }

    private static void Save()
    {
        var file = new ConfigFile();
        file.SetValue("profile", "operator", _operatorId);

        file.SetValue(SetsSection, "active", _activeSet);
        for (int set = 0; set < WeaponRules.SetCount; set++)
        {
            file.SetValue(SetsSection, $"name_{set}", _setNames[set]);
            for (int slot = 0; slot < WeaponRules.SlotCount; slot++)
                file.SetValue(SetsSection, $"set{set}_slot{slot}", _setWeapons[set][slot]);
        }

        foreach (var pair in _colors)
        {
            string value = $"{pair.Value.Jacket.ToHtml(false)},{pair.Value.Pants.ToHtml(false)},{pair.Value.Accent.ToHtml(false)}";
            file.SetValue("colors", pair.Key, value);
        }
        Error error = file.Save(FilePath);
        if (error != Error.Ok) GD.PushWarning($"PlayerProfile: could not save {FilePath}: {error}");
    }
}
