using Godot;

/// <summary>Shared, data-driven contract for every selectable match mode.</summary>
public abstract partial class GameModeBase : RefCounted
{
    public abstract MatchManager.Mode Id { get; }
    public abstract string DisplayName { get; }
    public abstract string LobbyDescription { get; }
    public virtual string PlayerFormat => UsesTeams ? $"2 teams · {RecommendedTeamSize} players per squad" : "Free for all";
    public abstract int BaseKillPoints { get; }
    public virtual bool UsesTeams => false;
    public virtual bool UsesVehicles => true;
    public virtual bool UsesCursedEvents => false;
    public virtual bool HasTimedResolution => false;
    public virtual int RecommendedTeamSize => 2;
    public virtual int MinimumPlayers => 2;
    public virtual int MaximumPlayers => 16;

    public static GameModeBase For(MatchManager.Mode mode) => mode switch
    {
        MatchManager.Mode.Default => new DefaultMode(),
        MatchManager.Mode.TeamDeathmatch => new TeamDeathmatchMode(),
        MatchManager.Mode.PureChase => new PureChaseMode(),
        MatchManager.Mode.StormRespawn => new StormRespawnMode(),
        MatchManager.Mode.ClanChase => new ClanChaseMode(),
        MatchManager.Mode.CursedDeathmatch => new CursedDeathmatchMode(),
        MatchManager.Mode.ConvoyAssault => new ConvoyAssaultMode(),
        MatchManager.Mode.DuoBuggy => new DuoBuggyMode(),
        _ => new DefaultMode(),
    };
}

public partial class DefaultMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.Default;
    public override string DisplayName => "Default";
    public override string LobbyDescription => "Two-player crews fight for points across the wasteland. Cursed land events and the garage are active. The host ends the open match when ready; the team with the most points wins.";
    public override string PlayerFormat => "Two-player crews · driver + shooter";
    public override int BaseKillPoints => 100;
    public override bool UsesTeams => true;
    public override bool UsesCursedEvents => true;
}

public partial class PureChaseMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.PureChase;
    public override string DisplayName => "Pure Chase";
    public override string LobbyDescription => "Each team hunts an assigned rival team. There are no cursed events or garage. Be the first team to destroy five rival vehicles; reaching the checkpoint ahead of your target earns bonus points.";
    public override string PlayerFormat => "Two-player crews · assigned target teams";
    public override int BaseKillPoints => 100;
    public override bool UsesTeams => true;
}

/// <summary>Plain Alpha vs Bravo on foot: the mode the hub's two warehouses were designed for. Team
/// scoring and the time limit already live in MatchManager (they check GameMode == TeamDeathmatch);
/// this class only gives the mode its own name and rules so it can be picked in the lobby.</summary>
public partial class TeamDeathmatchMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.TeamDeathmatch;
    public override string DisplayName => "Team Deathmatch";
    public override string LobbyDescription => "Alpha against Bravo on foot. Each team preps in its own warehouse; when the host starts, both squads drop into the map. The team with the most kills when time runs out wins.";
    public override string PlayerFormat => "2 teams · Alpha vs Bravo";
    public override int BaseKillPoints => 100;
    public override bool UsesTeams => true;
    public override bool UsesVehicles => false;
    public override bool HasTimedResolution => true;
    public override int RecommendedTeamSize => 4;
}

public partial class StormRespawnMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.StormRespawn;
    public override string DisplayName => "Storm Respawn";
    public override string LobbyDescription => "Free-for-all, timed combat. Players return at a safe map-edge zone after each death. The storm is visual only; the player with the most kills when time expires wins.";
    public override int BaseKillPoints => 100;
    public override bool HasTimedResolution => true;
    public override bool UsesTeams => false;
}

public partial class ClanChaseMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.ClanChase;
    public override string DisplayName => "Clan Chase";
    public override string LobbyDescription => "Two larger teams fight as convoys, with multiple vehicles and a shared team garage. Teams have 3–5 players; the team with the most points wins when the host ends the match.";
    public override string PlayerFormat => "2 teams · 3–5 players each";
    public override int BaseKillPoints => 100;
    public override bool UsesTeams => true;
    public override int RecommendedTeamSize => 4;
    public override int MinimumPlayers => 6;
    public override int MaximumPlayers => 12;
}

public partial class CursedDeathmatchMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.CursedDeathmatch;
    public override string DisplayName => "Cursed Deathmatch";
    public override string LobbyDescription => "Timed on-foot free-for-all with no vehicles. All six cursed land events run at maximum intensity. The player with the most kills when time expires wins.";
    public override int BaseKillPoints => 100;
    public override bool UsesVehicles => false;
    public override bool UsesCursedEvents => true;
    public override bool HasTimedResolution => true;
}

public partial class ConvoyAssaultMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.ConvoyAssault;
    public override string DisplayName => "Convoy Assault";
    public override string LobbyDescription => "Asymmetric attack and defense. Attackers escort the STV to the checkpoint and hold it; defenders can disable the STV or destroy the checkpoint to remove the hold win path. At 3v3 the hold is 25 seconds with a 10-minute match; at 8v8 it is 60 seconds with a 20-minute match. If time expires first, team score, kills and individual score break ties.";
    public override string PlayerFormat => "2 teams · 3–8 players each";
    public override int BaseKillPoints => 100;
    public override bool UsesTeams => true;
    public override int RecommendedTeamSize => 3;
    public override int MinimumPlayers => 6;
    public override bool HasTimedResolution => true;
}

public partial class DuoBuggyMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.DuoBuggy;
    public override string DisplayName => "2v2 Duo Buggy";
    public override string LobbyDescription => "Exactly four players form two two-person buggy crews in a timed, open-wasteland skirmish. Swap driver and shooter roles while moving. The higher-scoring team wins; ties break by kills, then individual score.";
    public override string PlayerFormat => "2 teams · 2 players each · exactly 4 total";
    public override int BaseKillPoints => 100;
    public override bool UsesTeams => true;
    public override int RecommendedTeamSize => 2;
    public override int MinimumPlayers => 4;
    public override int MaximumPlayers => 4;
    public override bool HasTimedResolution => true;
}
