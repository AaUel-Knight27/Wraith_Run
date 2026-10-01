using System.Collections.Generic;

/// <summary>
/// Every medal / achievement a player can collect. Declared in the order the stat card lists them,
/// and MedalCatalog.All is indexed by the enum value, so keep the two in step.
///
/// Medals are SAVED by their string Key (see MedalCatalog), never by the enum number, so reordering
/// or inserting a medal here can never scramble anybody's saved career.
/// </summary>
public enum Medal
{
    ClearKill,
    HeadShot,
    Revenge,
    MajorStreak,
    StopStreak,
    Explosive,
    Knife,
    Assist,
    DoubleKill,
    TripleKill,
    KingSlayer,
    OneShotMultiKill,
    WallBang,
    NoScope360,
    LastBullet,
    OnVergeOfDeath,
    TeamWipe,
    FirstBlood,
    OneShotOneKill,
    CloseCombat,
    LongShot,
    HipFire,
}

/// <summary>Display name, save key and "how do I earn this" text for every medal. Pure C#.</summary>
public static class MedalCatalog
{
    public sealed class Info
    {
        public Medal Medal;
        /// <summary>Stable identifier used in the save file and on the wire. Never change one.</summary>
        public string Key = string.Empty;
        public string Label = string.Empty;
        public string Description = string.Empty;
    }

    private static Info M(Medal medal, string key, string label, string description) =>
        new Info { Medal = medal, Key = key, Label = label, Description = description };

    /// <summary>Index = (int)Medal. Order = the order the stat card shows them.</summary>
    public static readonly IReadOnlyList<Info> All = new[]
    {
        M(Medal.ClearKill, "clear_kill", "Clear Kill",
            "A kill with a plain shot - not a headshot, blade or explosive."),
        M(Medal.HeadShot, "head_shot", "Head shot",
            "Kill a player with a headshot."),
        M(Medal.Revenge, "revenge", "Revenge",
            "Kill the player who last killed you."),
        M(Medal.MajorStreak, "major_streak", "Major Streak",
            $"Reach a {MedalRules.MajorStreakInterval}-kill streak (and every {MedalRules.MajorStreakInterval} after) without dying."),
        M(Medal.StopStreak, "stop_streak", "Stop Streak",
            $"Kill a player who is on a streak of {MedalRules.StopStreakMinimum} or more."),
        M(Medal.Explosive, "explosive", "Explosive",
            "Kill a player with an explosive."),
        M(Medal.Knife, "knife", "Knife",
            "Kill a player with a blade."),
        M(Medal.Assist, "assist", "Assist",
            "Help kill a player you did not finish off."),
        M(Medal.DoubleKill, "double_kill", "Double Kill",
            $"Two kills within {MedalRules.MultiKillWindowSeconds:0} seconds."),
        M(Medal.TripleKill, "triple_kill", "Triple Kill",
            $"Three kills within {MedalRules.MultiKillWindowSeconds:0} seconds."),
        M(Medal.KingSlayer, "king_slayer", "King Slayer",
            $"Kill the player who is leading the match (at least {MedalRules.KingSlayerMinimumKills} kills)."),
        M(Medal.OneShotMultiKill, "one_shot_multi_kill", "One Shot Multi Kill",
            "Kill two or more players with a single shot."),
        M(Medal.WallBang, "wall_bang", "Wall Bang",
            "Kill a player through a wall. Needs bullet penetration, which the game does not have yet."),
        M(Medal.NoScope360, "no_scope_360", "360 No Scope",
            "Kill a player after a full spin without aiming down sights."),
        M(Medal.LastBullet, "last_bullet", "Last Bullet",
            "Kill a player with the last bullet in your magazine."),
        M(Medal.OnVergeOfDeath, "on_verge_of_death", "On Verge Of Death",
            "Kill a player while you are under 20% health."),
        M(Medal.TeamWipe, "team_wipe", "Team Wipe",
            "Kill the last living member of the enemy team."),
        M(Medal.FirstBlood, "first_blood", "First Blood",
            "Score the first kill of the match."),
        M(Medal.OneShotOneKill, "one_shot_one_kill", "One Shot, One Kill",
            "Kill a player from full health with a single shot."),
        M(Medal.CloseCombat, "close_combat", "Close Combat",
            $"Kill a player from {MedalRules.CloseCombatMeters:0} m or closer."),
        M(Medal.LongShot, "long_shot", "Long Shot",
            $"Kill a player from {MedalRules.LongShotMeters:0} m or farther."),
        M(Medal.HipFire, "hip_fire", "Hip Fire",
            "Kill a player without aiming down sights."),
    };

    public static int Count => All.Count;

    private static readonly Dictionary<string, Medal> ByKey = BuildKeyIndex();

    private static Dictionary<string, Medal> BuildKeyIndex()
    {
        var index = new Dictionary<string, Medal>(All.Count);
        foreach (Info info in All) index[info.Key] = info.Medal;
        return index;
    }

    public static Info Get(Medal medal) => All[(int)medal];

    /// <summary>Medals worth a line on the kill feed when earned. The everyday ones (Clear Kill,
    /// Head shot, Hip Fire, Knife...) come with almost every kill and would drown the feed; they
    /// still count on the card.</summary>
    public static bool IsNotable(Medal medal)
    {
        switch (medal)
        {
            case Medal.Revenge:
            case Medal.MajorStreak:
            case Medal.StopStreak:
            case Medal.DoubleKill:
            case Medal.TripleKill:
            case Medal.KingSlayer:
            case Medal.OneShotMultiKill:
            case Medal.WallBang:
            case Medal.NoScope360:
            case Medal.LastBullet:
            case Medal.OnVergeOfDeath:
            case Medal.TeamWipe:
            case Medal.FirstBlood:
            case Medal.LongShot:
                return true;
            default:
                return false;
        }
    }

    /// <summary>False for a key this build does not know (a medal from a newer version, or junk).</summary>
    public static bool TryParseKey(string? key, out Medal medal)
    {
        medal = default;
        return key != null && ByKey.TryGetValue(key, out medal);
    }
}
