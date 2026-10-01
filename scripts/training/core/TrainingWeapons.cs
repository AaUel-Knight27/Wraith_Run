using Godot;

/// <summary>Looks up WeaponData by Loadout name (bots hold real weapons, with real fire sounds/flash).</summary>
public static class TrainingWeapons
{
    private static WeaponRegistry? _registry;

    public static WeaponData? Find(string weaponName)
    {
        _registry ??= ResourceLoader.Load<WeaponRegistry>("res://scripts/data/WeaponRegistry.tres");
        if (_registry == null) return null;
        foreach (WeaponData weapon in _registry.Weapons)
            if (weapon != null && weapon.WeaponName == weaponName) return weapon;
        return null;
    }
}
