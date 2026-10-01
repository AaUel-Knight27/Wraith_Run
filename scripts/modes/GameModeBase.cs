using Godot;

/// <summary>Shared, data-driven contract for every selectable match mode.</summary>
public abstract partial class GameModeBase : RefCounted
{
    public abstract MatchManager.Mode Id { get; }
    public abstract string DisplayName { get; }
    public abstract int BaseKillPoints { get; }
    public virtual bool UsesTeams => false;
    public virtual bool UsesVehicles => true;
    public virtual bool UsesCursedEvents => false;
    public virtual bool HasTimedResolution => false;
    public virtual int RecommendedTeamSize => 2;
    public virtual int MinimumPlayers => 2;
    public virtual int MaximumPlayers => 12;

    public static GameModeBase For(MatchManager.Mode mode) => mode switch
    {
        MatchManager.Mode.Default => new DefaultMode(),
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
    public override int BaseKillPoints => 100;
    public override bool UsesTeams => true;
    public override bool UsesCursedEvents => true;
}

public partial class PureChaseMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.PureChase;
    public override string DisplayName => "Pure Chase";
    public override int BaseKillPoints => 100;
    public override bool UsesTeams => true;
}

public partial class StormRespawnMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.StormRespawn;
    public override string DisplayName => "Storm Respawn";
    public override int BaseKillPoints => 100;
    public override bool HasTimedResolution => true;
    public override bool UsesTeams => true;
}

public partial class ClanChaseMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.ClanChase;
    public override string DisplayName => "Clan Chase";
    public override int BaseKillPoints => 100;
    public override bool UsesTeams => true;
    public override int RecommendedTeamSize => 4;
}

public partial class CursedDeathmatchMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.CursedDeathmatch;
    public override string DisplayName => "Cursed Deathmatch";
    public override int BaseKillPoints => 100;
    public override bool UsesVehicles => false;
    public override bool UsesCursedEvents => true;
    public override bool HasTimedResolution => true;
}

public partial class ConvoyAssaultMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.ConvoyAssault;
    public override string DisplayName => "Convoy Assault";
    public override int BaseKillPoints => 100;
    public override bool UsesTeams => true;
    public override int RecommendedTeamSize => 3;
    public override bool HasTimedResolution => true;
}

public partial class DuoBuggyMode : GameModeBase
{
    public override MatchManager.Mode Id => MatchManager.Mode.DuoBuggy;
    public override string DisplayName => "2v2 Duo Buggy";
    public override int BaseKillPoints => 100;
    public override bool UsesTeams => true;
    public override int RecommendedTeamSize => 2;
    public override int MinimumPlayers => 4;
    public override int MaximumPlayers => 4;
    public override bool HasTimedResolution => true;
}
