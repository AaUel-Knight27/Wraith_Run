using System.Collections.Generic;

/// <summary>
/// Everything the medal rules need to know about ONE confirmed kill by the local player. The tracker
/// (CareerTracker) fills this in from what it observed; MedalRules turns it into medals. Keeping the
/// two apart means the rules are plain data-in / counters-out and need no engine to check.
/// </summary>
public struct KillContext
{
    /// <summary>The style flags the shot was fired with (headshot, hip-fire, spin, verge of death,
    /// melee, explosive) - the same flags that decide the kill's score.</summary>
    public KillStyle Styles;

    /// <summary>Metres from the shooter to the victim when the killing shot landed; negative when it
    /// is not known, in which case no distance medal is awarded.</summary>
    public float Distance;

    /// <summary>The killing shot emptied the magazine.</summary>
    public bool LastBullet;

    /// <summary>The victim was at full health and died to a single shot of yours.</summary>
    public bool OneShotOneKill;

    /// <summary>A second (or later) kill from the same trigger pull. The tracker sets it once per
    /// pull, so one shotgun blast through three players is one medal, not two.</summary>
    public bool OneShotMultiKill;

    /// <summary>The bullet passed through a wall. Nothing in the game sets this yet (shots stop at
    /// the first surface); the medal exists so it lights up the day penetration is added.</summary>
    public bool WallBang;

    /// <summary>The victim is the player who last killed you.</summary>
    public bool Revenge;

    /// <summary>The victim was on a kill streak of StopStreakMinimum or more.</summary>
    public bool StoppedStreak;

    /// <summary>This kill brought your own streak to a multiple of MajorStreakInterval.</summary>
    public bool ReachedMajorStreak;

    /// <summary>The victim was leading the match.</summary>
    public bool KingSlayer;

    /// <summary>The first kill of the match, by anyone.</summary>
    public bool FirstBlood;

    /// <summary>The victim was the last living member of the enemy team.</summary>
    public bool TeamWipe;

    /// <summary>1 for a lone kill, 2 when it landed within the multi-kill window of your previous
    /// one, 3 for a third in a row, and so on.</summary>
    public int MultiKillCount;
}

/// <summary>
/// The medal rules: which medals one kill earns. Every threshold lives here as a named constant so
/// tuning a medal is a one-line change (and the descriptions on the stat card follow automatically).
/// </summary>
public static class MedalRules
{
    public const float CloseCombatMeters = 4.0f;
    public const float LongShotMeters = 30.0f;

    /// <summary>Kill a player who has at least this many kills in a row.</summary>
    public const int StopStreakMinimum = 3;

    /// <summary>Major Streak is awarded at 5 kills in a row, and again at 10, 15, ...</summary>
    public const int MajorStreakInterval = 5;

    /// <summary>The match leader must have at least this many kills to be worth a King Slayer.</summary>
    public const int KingSlayerMinimumKills = 3;

    /// <summary>Same window PlayerScore uses for its multi-kill bonus, so "double kill" and the
    /// MULTI-KILL feed line agree.</summary>
    public const float MultiKillWindowSeconds = KillStyleBonus.MultiKillWindowSeconds;

    /// <summary>Adds one count of every medal this kill earned (and lists them in <paramref name="earned"/>
    /// if given). The kill itself, and its weapon, is counted by CareerStats.RecordKill before this runs.</summary>
    public static void Award(CareerStats stats, in KillContext c, List<Medal>? earned = null)
    {
        KillStyle s = c.Styles;
        bool headshot = Has(s, KillStyle.Headshot);
        bool melee = Has(s, KillStyle.Melee);
        bool explosive = Has(s, KillStyle.Explosive);
        bool spin = Has(s, KillStyle.NoScope360);

        // A "clear" kill is the plain one: none of the special ways to kill.
        if (!headshot && !melee && !explosive && !spin) Give(stats, earned, Medal.ClearKill);

        if (headshot) Give(stats, earned, Medal.HeadShot);
        if (melee) Give(stats, earned, Medal.Knife);
        if (explosive) Give(stats, earned, Medal.Explosive);
        if (spin) Give(stats, earned, Medal.NoScope360);
        if (Has(s, KillStyle.HipFire)) Give(stats, earned, Medal.HipFire);
        if (Has(s, KillStyle.VergeOfDeath)) Give(stats, earned, Medal.OnVergeOfDeath);

        if (c.Distance >= 0.0f)
        {
            if (c.Distance <= CloseCombatMeters) Give(stats, earned, Medal.CloseCombat);
            if (c.Distance >= LongShotMeters) Give(stats, earned, Medal.LongShot);
        }

        if (c.LastBullet) Give(stats, earned, Medal.LastBullet);
        if (c.OneShotOneKill) Give(stats, earned, Medal.OneShotOneKill);
        if (c.OneShotMultiKill) Give(stats, earned, Medal.OneShotMultiKill);
        if (c.WallBang) Give(stats, earned, Medal.WallBang);
        if (c.Revenge) Give(stats, earned, Medal.Revenge);
        if (c.StoppedStreak) Give(stats, earned, Medal.StopStreak);
        if (c.ReachedMajorStreak) Give(stats, earned, Medal.MajorStreak);
        if (c.KingSlayer) Give(stats, earned, Medal.KingSlayer);
        if (c.FirstBlood) Give(stats, earned, Medal.FirstBlood);
        if (c.TeamWipe) Give(stats, earned, Medal.TeamWipe);

        // Double and Triple are the 2nd and 3rd kill of a chain; a 4th or later kill adds neither.
        if (c.MultiKillCount == 2) Give(stats, earned, Medal.DoubleKill);
        else if (c.MultiKillCount == 3) Give(stats, earned, Medal.TripleKill);
    }

    private static void Give(CareerStats stats, List<Medal>? earned, Medal medal)
    {
        stats.AddMedal(medal);
        earned?.Add(medal);
    }

    private static bool Has(KillStyle styles, KillStyle flag) => (styles & flag) == flag;
}
