using Godot;
using System;

[GlobalClass]
public partial class WeaponRegistry : Resource
{
    [Export] public WeaponData[] Weapons { get; set; } = Array.Empty<WeaponData>();
    [Export] public string ActiveWeaponName { get; set; } = "AK-47";

    public WeaponData GetActiveWeapon()
    {
        foreach (var weapon in Weapons)
        {
            if (weapon.WeaponName == ActiveWeaponName)
                return weapon;
        }
        return null!;
    }
}
