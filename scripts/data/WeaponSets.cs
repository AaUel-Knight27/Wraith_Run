using System;
using System.Collections.Generic;
using Godot;

/// <summary>The three places a carried weapon can sit, in number-key order.</summary>
public enum WeaponSlot
{
    Primary1 = 0,
    Primary2 = 1,
    Pistol = 2,
}

/// <summary>The weapon families the picker groups by. Shotgun is its own family so it can sit in a
/// primary slot next to the rifles and SMGs.</summary>
public enum WeaponCategory
{
    AssaultRifle,
    Smg,
    Shotgun,
    Sniper,
    Pistol,
}

/// <summary>
/// Everything that decides what a player may carry. Pure data and rules - no UI, no nodes - so the
/// lobby (what you may pick), the profile (what is valid to save) and the match (what you spawn
/// with) all ask the same place.
///
/// The carry rule: two primary guns and one pistol. A slot only ever accepts its own kind of weapon, so a set can be changed but
/// never grown or shrunk, and a weapon cannot sit in two slots of the same set.
/// </summary>
public static class WeaponRules
{
    public const int SlotCount = 3;
    public const int SetCount = 3;
    public const int MaxSetNameLength = 16;

    /// <summary>The set that is active until the player picks another. MID RANGE holds the AK-47,
    /// which is what every player has always started with.</summary>
    public const int DefaultActiveSet = 1;

    public static readonly string[] DefaultSetNames = { "SHORT RANGE", "MID RANGE", "LONG RANGE" };

    /// <summary>[set][slot] -> weapon name. Order of slots: Primary1, Primary2, Pistol.</summary>
    public static readonly string[][] DefaultWeapons =
    {
        new[] { "Shotgun", "Kriss Vector", "Glock 19" },
        new[] { "AK-47", "P90", "Glock 19" },
        new[] { "M4", "AK-47", "Desert Eagle" },
    };

    // ---- slots ------------------------------------------------------------------------------

    public static bool IsPrimary(WeaponSlot slot) => slot == WeaponSlot.Primary1 || slot == WeaponSlot.Primary2;

    public static string SlotName(WeaponSlot slot) => slot switch
    {
        WeaponSlot.Primary1 => "PRIMARY 1",
        WeaponSlot.Primary2 => "PRIMARY 2",
        WeaponSlot.Pistol => "PISTOL",
        _ => string.Empty,
    };

    public static WeaponCategory[] CategoriesFor(WeaponSlot slot) => slot switch
    {
        WeaponSlot.Primary1 or WeaponSlot.Primary2 =>
            new[] { WeaponCategory.AssaultRifle, WeaponCategory.Smg, WeaponCategory.Shotgun, WeaponCategory.Sniper },
        WeaponSlot.Pistol => new[] { WeaponCategory.Pistol },
        _ => Array.Empty<WeaponCategory>(),
    };

    // ---- categories -------------------------------------------------------------------------

    public static string CategoryName(WeaponCategory category) => category switch
    {
        WeaponCategory.AssaultRifle => "ASSAULT RIFLES",
        WeaponCategory.Smg => "SMGS",
        WeaponCategory.Shotgun => "SHOTGUNS",
        WeaponCategory.Sniper => "SNIPER RIFLES",
        WeaponCategory.Pistol => "PISTOLS",
        _ => string.Empty,
    };

    public static string CategoryIcon(WeaponCategory category) => category switch
    {
        WeaponCategory.AssaultRifle => "rifle",
        WeaponCategory.Smg => "wpn-smg",
        WeaponCategory.Shotgun => "wpn-shotgun",
        WeaponCategory.Sniper => "rifle",
        WeaponCategory.Pistol => "wpn-pistol",
        _ => "rifle",
    };

    /// <summary>Which family a weapon belongs to, from its WeaponClass string in the registry.</summary>
    public static WeaponCategory? CategoryOf(WeaponData weapon)
    {
        switch (weapon.WeaponClass.Replace(" ", string.Empty).ToLowerInvariant())
        {
            case "assaultrifle": return WeaponCategory.AssaultRifle;
            case "smg": return WeaponCategory.Smg;
            case "shotgun": return WeaponCategory.Shotgun;
            case "sniper":
            case "sniperrifle":
            case "marksmanrifle": return WeaponCategory.Sniper;
            case "pistol":
            case "sidearm": return WeaponCategory.Pistol;
            default: return null;
        }
    }

    // ---- who may carry what -----------------------------------------------------------------

    public static bool Fits(WeaponData weapon, WeaponSlot slot)
    {
        WeaponCategory? category = CategoryOf(weapon);
        return category != null && Array.IndexOf(CategoriesFor(slot), category.Value) >= 0;
    }

    /// <summary>Every weapon a slot could hold, in registry order. Weapons the match cannot equip
    /// (not in WeaponSwitcher.Loadout) are left out, so the picker never offers a dead choice.</summary>
    public static List<WeaponData> WeaponsFor(WeaponSlot slot)
    {
        var result = new List<WeaponData>();
        foreach (WeaponData weapon in OperatorInfo.Weapons)
            if (Fits(weapon, slot) && Array.IndexOf(WeaponSwitcher.Loadout, weapon.WeaponName) >= 0)
                result.Add(weapon);
        return result;
    }

    /// <summary>True when <paramref name="weaponName"/> may go in <paramref name="slot"/> given what the
    /// set already holds: the weapon exists, the match can equip it, it is the right kind for the
    /// slot, and (for a primary) it is not already in the other primary slot.</summary>
    public static bool IsValidFor(WeaponSlot slot, string? weaponName, string?[] setWeapons)
    {
        if (string.IsNullOrEmpty(weaponName)) return false;
        WeaponData? weapon = OperatorInfo.FindWeapon(weaponName);
        if (weapon == null || !Fits(weapon, slot)) return false;
        if (Array.IndexOf(WeaponSwitcher.Loadout, weaponName) < 0) return false;
        if (IsPrimary(slot))
        {
            for (int other = 0; other < setWeapons.Length; other++)
            {
                if (other == (int)slot || !IsPrimary((WeaponSlot)other)) continue;
                if (setWeapons[other] == weaponName) return false;
            }
        }
        return true;
    }

    /// <summary>Which other slot of the set already holds this weapon, if any (for an "IN USE" tag).</summary>
    public static WeaponSlot? SlotHolding(string weaponName, WeaponSlot except, string?[] setWeapons)
    {
        for (int i = 0; i < setWeapons.Length; i++)
            if (i != (int)except && setWeapons[i] == weaponName) return (WeaponSlot)i;
        return null;
    }

    /// <summary>Trims, flattens line breaks and caps the length of a typed set name.</summary>
    public static string CleanSetName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string text = raw.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
        if (text.Length > MaxSetNameLength) text = text.Substring(0, MaxSetNameLength).TrimEnd();
        return text;
    }
}
