using System.Collections.Generic;
using Godot;

/// <summary>
/// What the player picked in the lobby: which operator, which starting weapon, and the colours
/// they dressed each operator in. Saved to user://profile.cfg (separate from settings.cfg so a
/// settings reset never wipes a loadout). A plain static class rather than an autoload: nothing
/// here needs _Process or signals, and it keeps project.godot untouched.
///
/// An empty operator id means "Standard Issue" - no perks and no drawbacks, exactly how the game
/// played before the operator roster existed.
/// </summary>
public static class PlayerProfile
{
    private const string FilePath = "user://profile.cfg";
    public const string DefaultWeaponName = "AK-47";

    private static bool _loaded;
    private static string _operatorId = string.Empty;
    private static string _weaponName = DefaultWeaponName;
    private static readonly Dictionary<string, (Color Jacket, Color Pants, Color Accent)> _colors = new();

    public static string OperatorId
    {
        get { EnsureLoaded(); return _operatorId; }
        set { EnsureLoaded(); _operatorId = value ?? string.Empty; Save(); }
    }

    public static string WeaponName
    {
        get { EnsureLoaded(); return _weaponName; }
        set { EnsureLoaded(); _weaponName = string.IsNullOrEmpty(value) ? DefaultWeaponName : value; Save(); }
    }

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

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        var file = new ConfigFile();
        if (file.Load(FilePath) != Error.Ok) return;

        _operatorId = file.GetValue("profile", "operator", string.Empty).AsString();
        string weapon = file.GetValue("profile", "weapon", DefaultWeaponName).AsString();
        _weaponName = string.IsNullOrEmpty(weapon) ? DefaultWeaponName : weapon;

        if (!file.HasSection("colors")) return;
        foreach (string key in file.GetSectionKeys("colors"))
        {
            string[] parts = file.GetValue("colors", key, string.Empty).AsString().Split(',');
            if (parts.Length != 3) continue;
            _colors[key] = (Color.FromHtml(parts[0]), Color.FromHtml(parts[1]), Color.FromHtml(parts[2]));
        }
    }

    private static void Save()
    {
        var file = new ConfigFile();
        file.SetValue("profile", "operator", _operatorId);
        file.SetValue("profile", "weapon", _weaponName);
        foreach (var pair in _colors)
        {
            string value = $"{pair.Value.Jacket.ToHtml(false)},{pair.Value.Pants.ToHtml(false)},{pair.Value.Accent.ToHtml(false)}";
            file.SetValue("colors", pair.Key, value);
        }
        Error error = file.Save(FilePath);
        if (error != Error.Ok) GD.PushWarning($"PlayerProfile: could not save {FilePath}: {error}");
    }
}
