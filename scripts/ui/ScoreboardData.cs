using System.Collections.Generic;
using Godot;

/// <summary>
/// One place that turns "who is in the match" into scoreboard rows, shared by the Tab scoreboard
/// and the end-of-match results screen so the two can never disagree.
///
/// Identity comes from PlayerIdentity.IdOf, not GetMultiplayerAuthority: bots are simulated by
/// the host and so all share the host's network authority. Keying on authority made every bot
/// collapse into the host's row ("Player 1 (YOU)" repeated, the enemy team empty) - keying on
/// the bot's own id gives each one its own name, team and kills / deaths / assists.
/// </summary>
public static class ScoreboardData
{
    public sealed class Row
    {
        public long PeerId;
        public int Team;
        public bool Dead;
        public int Points;
        public int Kills;
        public int Deaths;
        public int Assists;
    }

    /// <summary>Every player and bot currently in the match, best first.</summary>
    public static List<Row> Collect(SceneTree tree)
    {
        var rows = new List<Row>();
        var seen = new HashSet<long>();
        MatchManager match = MatchManager.Instance;
        bool teamMode = match != null && match.IsTeamMode;

        foreach (Node node in tree.GetNodesInGroup("players"))
        {
            if (node is not CharacterBody3D body || !GodotObject.IsInstanceValid(body) || body.IsQueuedForDeletion()) continue;
            long id = PlayerIdentity.IdOf(body);
            if (!seen.Add(id)) continue;

            MatchStats.Stats stats = MatchStats.Instance?.Get(id) ?? new MatchStats.Stats();
            Health? health = body.GetNodeOrNull<Health>("Health");
            PlayerScore? score = body.GetNodeOrNull<PlayerScore>("Score");
            rows.Add(new Row
            {
                PeerId = id,
                Team = teamMode ? match!.GetTeam(id) : -1,
                Dead = health != null && health.IsDead,
                Points = score?.TotalPoints ?? 0,
                Kills = stats.Kills,
                Deaths = stats.Deaths,
                Assists = stats.Assists,
            });
        }

        Sort(rows);
        return rows;
    }

    /// <summary>Best first: score, then kills, then fewest deaths, then id so ties never shuffle
    /// from one refresh to the next.</summary>
    public static void Sort(List<Row> rows) => rows.Sort((a, b) =>
    {
        int byPoints = b.Points.CompareTo(a.Points);
        if (byPoints != 0) return byPoints;
        int byKills = b.Kills.CompareTo(a.Kills);
        if (byKills != 0) return byKills;
        int byDeaths = a.Deaths.CompareTo(b.Deaths);
        return byDeaths != 0 ? byDeaths : a.PeerId.CompareTo(b.PeerId);
    });

    /// <summary>"Vex [N]", "Player 1 (YOU)", "Player 1 (HOST)"...</summary>
    public static string DisplayName(Row row, long me)
    {
        string name = BotRoster.DisplayName(row.PeerId);
        if (row.PeerId == me) name += "  (YOU)";
        else if (row.PeerId == 1 && !BotRoster.IsBot(row.PeerId)) name += "  (HOST)";
        return name;
    }
}
