using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Read-only helpers the lobby screens share: the sorted operator roster, how to display a
/// callsign, which kit lines a system actually reads today, and each operator's default colours.
/// Plain static class - no scene node needed.
/// </summary>
public static class OperatorInfo
{
    private const string OperatorRegistryPath = "res://data/characters/CharacterRegistry.tres";
    private const string WeaponRegistryPath = "res://scripts/data/WeaponRegistry.tres";

    private static List<CharacterData>? _operators;
    private static List<WeaponData>? _weapons;

    /// <summary>Kit lines whose effect id is read by gameplay code right now. Everything else on
    /// the roster is real data waiting on a system (vehicles, garage, abilities) - the lobby lists
    /// it anyway and tags it PLANNED. Keep in step with CharacterEffectIds.cs and the
    /// GetModifier call sites (Health, PlayerMovement, WeaponSwitcher).</summary>
    private static readonly HashSet<string> WiredEffects = new()
    {
        CharacterEffectIds.OnFootMaxHealthMult,
        CharacterEffectIds.MoveSpeedMult,
        CharacterEffectIds.CrouchWalkSpeedMult,
        CharacterEffectIds.WeaponRecoilMult,
    };

    public static bool IsWired(string effectId) => WiredEffects.Contains(effectId);

    // ---- operators --------------------------------------------------------------------------

    public static IReadOnlyList<CharacterData> Operators
    {
        get
        {
            if (_operators != null) return _operators;
            _operators = new List<CharacterData>();
            var registry = ResourceLoader.Load<CharacterRegistry>(OperatorRegistryPath);
            if (registry == null)
            {
                GD.PushWarning($"OperatorInfo: could not load {OperatorRegistryPath}.");
                return _operators;
            }
            foreach (CharacterData character in registry.Characters)
                if (character != null) _operators.Add(character);
            _operators.Sort((a, b) =>
            {
                int byClass = a.Class.CompareTo(b.Class);
                return byClass != 0 ? byClass : string.Compare(Callsign(a), Callsign(b), StringComparison.OrdinalIgnoreCase);
            });
            return _operators;
        }
    }

    public static CharacterData? Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (CharacterData character in Operators)
            if (character.CharacterId == id) return character;
        return null;
    }

    /// <summary>The nickname between the quotes: Cade "Ironsight" Kessler -> IRONSIGHT.</summary>
    public static string Callsign(CharacterData? data)
    {
        if (data == null) return "STANDARD ISSUE";
        string name = data.DisplayName;
        int open = name.IndexOf('"');
        int close = open >= 0 ? name.IndexOf('"', open + 1) : -1;
        return (open >= 0 && close > open ? name.Substring(open + 1, close - open - 1) : name).ToUpperInvariant();
    }

    /// <summary>Cade "Ironsight" Kessler -> Cade Kessler.</summary>
    public static string RealName(CharacterData? data)
    {
        if (data == null) return "No operator";
        string name = data.DisplayName;
        int open = name.IndexOf('"');
        int close = open >= 0 ? name.IndexOf('"', open + 1) : -1;
        if (open < 0 || close <= open) return name;
        return $"{name.Substring(0, open).Trim()} {name.Substring(close + 1).Trim()}".Trim();
    }

    public static string ClassName(CharacterData.CharacterClass value) => value switch
    {
        CharacterData.CharacterClass.Driver => "DRIVER",
        CharacterData.CharacterClass.Fixer => "FIXER",
        CharacterData.CharacterClass.Operator => "OPERATOR",
        CharacterData.CharacterClass.Medic => "MEDIC",
        CharacterData.CharacterClass.Demolisher => "DEMOLISHER",
        _ => "OPERATOR",
    };

    public static string ClassIcon(CharacterData.CharacterClass value) => value switch
    {
        CharacterData.CharacterClass.Driver => "class-driver",
        CharacterData.CharacterClass.Fixer => "class-fixer",
        CharacterData.CharacterClass.Operator => "class-operator",
        CharacterData.CharacterClass.Medic => "class-medic",
        CharacterData.CharacterClass.Demolisher => "class-demolisher",
        _ => "user",
    };

    public static string ClassBlurb(CharacterData.CharacterClass value) => value switch
    {
        CharacterData.CharacterClass.Driver => "Keeps the vehicle fast, fed and alive.",
        CharacterData.CharacterClass.Fixer => "Repairs and armours the vehicle on the move.",
        CharacterData.CharacterClass.Operator => "On-foot fighter and objective specialist.",
        CharacterData.CharacterClass.Medic => "Heals the crew and lays cover smoke.",
        CharacterData.CharacterClass.Demolisher => "Charges, blasts and road denial.",
        _ => string.Empty,
    };

    // ---- colours ----------------------------------------------------------------------------

    private static bool IsWhite(Color c) => c.R > 0.98f && c.G > 0.98f && c.B > 0.98f;

    /// <summary>Colours an operator wears before the player touches anything. The roster data
    /// has none yet (every .tres is white), so each class gets a gritty base palette, nudged per
    /// operator so twenty-five people do not look like five.</summary>
    public static void DefaultColors(CharacterData? data, out Color jacket, out Color pants, out Color accent)
    {
        if (data != null && !(IsWhite(data.JacketColor) && IsWhite(data.PantsColor) && IsWhite(data.AccentColor)))
        {
            jacket = data.JacketColor; pants = data.PantsColor; accent = data.AccentColor;
            return;
        }

        (uint j, uint p, uint a) = data?.Class switch
        {
            CharacterData.CharacterClass.Driver => (0x7a5a2eu, 0x2e2a26u, 0xc8582au),
            CharacterData.CharacterClass.Fixer => (0x3d5a63u, 0x2b2f31u, 0xb59a3cu),
            CharacterData.CharacterClass.Operator => (0x4b5238u, 0x33352cu, 0x1b1b1du),
            CharacterData.CharacterClass.Medic => (0x8a8578u, 0x3a3d35u, 0xa3231du),
            CharacterData.CharacterClass.Demolisher => (0x5a2a22u, 0x232323u, 0xb59a3cu),
            _ => (0x55583fu, 0x2f312au, 0x1b1b1du),
        };

        int step = 0;
        if (data != null)
        {
            int sum = 0;
            foreach (char ch in data.CharacterId) sum += ch;
            step = (sum % 5) - 2; // -2..2, stable for a given id
        }
        jacket = Shift(Hex(j), step);
        pants = Shift(Hex(p), -step / 2);
        accent = Shift(Hex(a), step);
    }

    /// <summary>The colours to actually show: the player's own if they set any, else the defaults.</summary>
    public static void ResolveColors(CharacterData? data, out Color jacket, out Color pants, out Color accent)
    {
        string id = data?.CharacterId ?? string.Empty;
        if (PlayerProfile.TryGetColors(id, out jacket, out pants, out accent)) return;
        DefaultColors(data, out jacket, out pants, out accent);
    }

    private static Color Hex(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f);

    private static Color Shift(Color c, int step)
    {
        if (step == 0) return c;
        c.H = Mathf.PosMod(c.H + step * 0.02f, 1.0f);
        c.V = Mathf.Clamp(c.V * (1.0f + step * 0.09f), 0.05f, 1.0f);
        return c;
    }

    // ---- weapons ----------------------------------------------------------------------------

    public static IReadOnlyList<WeaponData> Weapons
    {
        get
        {
            if (_weapons != null) return _weapons;
            _weapons = new List<WeaponData>();
            var registry = ResourceLoader.Load<WeaponRegistry>(WeaponRegistryPath);
            if (registry == null)
            {
                GD.PushWarning($"OperatorInfo: could not load {WeaponRegistryPath}.");
                return _weapons;
            }
            foreach (WeaponData weapon in registry.Weapons)
                if (weapon != null) _weapons.Add(weapon);
            return _weapons;
        }
    }

    public static WeaponData? FindWeapon(string name)
    {
        foreach (WeaponData weapon in Weapons)
            if (weapon.WeaponName == name) return weapon;
        return null;
    }

    /// <summary>True when the in-match weapon list (WeaponSwitcher.Loadout) contains this weapon.
    /// A weapon that is not in it can be browsed and picked in the lobby, but a match starts you
    /// on the default weapon instead.</summary>
    public static bool IsInMatchLoadout(WeaponData weapon) =>
        Array.IndexOf(WeaponSwitcher.Loadout, weapon.WeaponName) >= 0;

    public static string WeaponIcon(WeaponData weapon)
    {
        string cls = weapon.WeaponClass.Replace(" ", string.Empty).ToLowerInvariant();
        return cls switch
        {
            "assaultrifle" => "rifle",
            "smg" => "wpn-smg",
            "pistol" or "sidearm" => "wpn-pistol",
            "shotgun" => "wpn-shotgun",
            "melee" => "wpn-melee",
            "launcher" or "heavy" => "wpn-launcher",
            _ => "rifle",
        };
    }

    public static string FireModeName(WeaponData weapon) => weapon.FireMode switch
    {
        WeaponData.FireModeType.FullAuto => "FULL AUTO",
        WeaponData.FireModeType.SemiAuto => "SEMI AUTO",
        WeaponData.FireModeType.Melee => "MELEE",
        _ => string.Empty,
    };
}
