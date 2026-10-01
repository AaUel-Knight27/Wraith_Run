using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>How one match ended for the local player.</summary>
public enum MatchResult { Win, Tie, Loss }

/// <summary>Kills and deaths with one weapon. "Deaths" = times you died while it was in your hands.</summary>
public sealed class WeaponRecord
{
    public long Kills;
    public long Deaths;

    /// <summary>With zero deaths it is simply the kill count (a 5-0 weapon reads 5.00), the same
    /// convention GameUiState.KdRatio uses for the scoreboard.</summary>
    public double KdRatio => Deaths <= 0 ? Kills : Kills / (double)Deaths;
}

/// <summary>
/// A player's lifetime record: the "Main stats", "Best weapons" and "Medals / achievements" of the
/// stat card. Pure C# (no Godot types) so the counting rules can be reasoned about and unit-tested
/// without running the engine, like KillStyleBonus.
///
/// Invariants kept by construction rather than by trust:
///   MatchesPlayed = Wins + Ties + Losses        (never stored, so it can never disagree)
///   Head shots    = the Head shot medal count   (never stored twice)
///   K/D, Head shot %, win rate                  (always derived)
/// </summary>
public sealed class CareerStats
{
    /// <summary>Hard ceiling for any counter. Real play never gets near it; it only exists so a
    /// damaged save file or a hostile peer cannot push absurd numbers into the UI.</summary>
    public const long MaxCounter = 999_999_999_999L;

    public const int MaxWeapons = 32;
    public const int MaxWeaponNameLength = 32;

    /// <summary>A weapon needs this many kills before it may top the BEST WEAPONS list, so a single
    /// lucky kill with a pistol does not outrank a thousand with a rifle.</summary>
    public const long MinKillsForRanking = 10;

    public long Wins;
    public long Ties;
    public long Losses;
    public long Kills;
    public long Deaths;
    public double PlaytimeSeconds;

    public readonly Dictionary<string, WeaponRecord> Weapons = new(StringComparer.Ordinal);
    private readonly long[] _medals = new long[MedalCatalog.Count];

    // ---- derived ----------------------------------------------------------------------------

    public long MatchesPlayed => Wins + Ties + Losses;
    public long HeadShots => _medals[(int)Medal.HeadShot];
    public double KdRatio => Deaths <= 0 ? Kills : Kills / (double)Deaths;
    public double HeadShotPercent => Kills <= 0 ? 0.0 : 100.0 * HeadShots / Kills;
    public double WinRatePercent => MatchesPlayed <= 0 ? 0.0 : 100.0 * Wins / MatchesPlayed;

    public long MedalCount(Medal medal) => _medals[(int)medal];

    public void SetMedalCount(Medal medal, long count) => _medals[(int)medal] = Clamp(count);

    public void AddMedal(Medal medal, long amount = 1) =>
        _medals[(int)medal] = Clamp(_medals[(int)medal] + amount);

    // ---- recording --------------------------------------------------------------------------

    /// <summary>A confirmed kill by this player: one kill, one more kill for the weapon, and every
    /// medal the kill earned.</summary>
    public void RecordKill(string weapon, in KillContext context, List<Medal>? earned = null)
    {
        Kills = Clamp(Kills + 1);
        Weapon(weapon).Kills = Clamp(Weapon(weapon).Kills + 1);
        MedalRules.Award(this, in context, earned);
    }

    /// <summary>This player died (to anyone, or to themselves) holding <paramref name="weapon"/>.</summary>
    public void RecordDeath(string weapon)
    {
        Deaths = Clamp(Deaths + 1);
        Weapon(weapon).Deaths = Clamp(Weapon(weapon).Deaths + 1);
    }

    public void RecordAssist() => AddMedal(Medal.Assist);

    public void RecordMatch(MatchResult result)
    {
        switch (result)
        {
            case MatchResult.Win: Wins = Clamp(Wins + 1); break;
            case MatchResult.Tie: Ties = Clamp(Ties + 1); break;
            default: Losses = Clamp(Losses + 1); break;
        }
    }

    public void AddPlaytime(double seconds)
    {
        if (seconds <= 0.0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return;
        PlaytimeSeconds = Math.Min(PlaytimeSeconds + seconds, 3.0e9);
    }

    /// <summary>The weapon's record, created on first use. Once MaxWeapons distinct names exist, any
    /// further name is folded into a shared "Other" slot instead of growing the table.</summary>
    public WeaponRecord Weapon(string? name)
    {
        string key = string.IsNullOrWhiteSpace(name) ? "Unknown" : name!.Trim();
        if (key.Length > MaxWeaponNameLength) key = key.Substring(0, MaxWeaponNameLength);
        if (Weapons.TryGetValue(key, out WeaponRecord? existing)) return existing;
        if (Weapons.Count >= MaxWeapons && !Weapons.ContainsKey("Other")) key = "Other";
        else if (Weapons.Count >= MaxWeapons) return Weapons["Other"];

        var record = new WeaponRecord();
        Weapons[key] = record;
        return record;
    }

    // ---- best weapons -----------------------------------------------------------------------

    /// <summary>Weapons you have killed with, best K/D first. Weapons with at least
    /// MinKillsForRanking kills always rank above the ones with fewer; ties break on kills, then name.</summary>
    public List<KeyValuePair<string, WeaponRecord>> BestWeapons(int count)
    {
        var list = new List<KeyValuePair<string, WeaponRecord>>();
        foreach (KeyValuePair<string, WeaponRecord> entry in Weapons)
            if (entry.Value.Kills > 0) list.Add(entry);

        list.Sort((a, b) =>
        {
            bool qa = a.Value.Kills >= MinKillsForRanking;
            bool qb = b.Value.Kills >= MinKillsForRanking;
            if (qa != qb) return qa ? -1 : 1;
            int byKd = b.Value.KdRatio.CompareTo(a.Value.KdRatio);
            if (byKd != 0) return byKd;
            int byKills = b.Value.Kills.CompareTo(a.Value.Kills);
            return byKills != 0 ? byKills : string.CompareOrdinal(a.Key, b.Key);
        });

        if (list.Count > count) list.RemoveRange(count, list.Count - count);
        return list;
    }

    // ---- formatting (shared by the card and the lists so numbers always read the same) --------

    /// <summary>"14,673". Invariant culture, so a phone set to another locale still shows commas.</summary>
    public static string FormatCount(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"2.19".</summary>
    public static string FormatKd(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>"14%". Whole percent, like the sample card.</summary>
    public static string FormatPercent(double value) =>
        Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>"64.9 h" from one hour up, "42 min" below it.</summary>
    public static string FormatPlaytime(double seconds)
    {
        if (seconds < 3600.0) return $"{Math.Max(0, (int)(seconds / 60.0))} min";
        return (seconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture) + " h";
    }

    public static long Clamp(long value) => value < 0 ? 0 : (value > MaxCounter ? MaxCounter : value);
}
