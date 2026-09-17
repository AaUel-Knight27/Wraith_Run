using Godot;

/// <summary>
/// Per-player match score. Present on every peer's copy of the Player node, but only the owning
/// device ever awards points to itself - the same authority rule Health uses for damage, and the
/// rule the design doc states outright ("never write another player's state from a non-
/// authoritative device"). TotalPoints is the one replicated field, so every peer can display the
/// same number for a given player.
///
/// Kills/Deaths are display counters local to the owning device for now. Promoting them to host
/// authority belongs with the match/GameModeBase work, not this scoring increment.
/// </summary>
public partial class PlayerScore : Node
{
    /// <summary>Raised on the owning device when a follow-up kill landed inside the multi-kill
    /// window, carrying the bonus that was added on top of the kill's own value.</summary>
    [Signal] public delegate void MultiKillAwardedEventHandler(int bonusPoints);

    [Signal] public delegate void ScoreChangedEventHandler(int totalPoints);

    private const ulong MultiKillWindowMsec = (ulong)(KillStyleBonus.MultiKillWindowSeconds * 1000.0f);

    [Export] public int TotalPoints { get; set; }

    public int Kills { get; private set; }
    public int Deaths { get; private set; }

    private KillFeed? _killFeed;
    private ulong _lastKillMsec;
    private bool _hasPriorKill;

    public override void _Ready()
    {
        _killFeed = KillFeed.From(this);
        if (_killFeed == null)
        {
            GD.PushError("PlayerScore could not reach the KillFeed autoload; no points will be awarded.");
            return;
        }
        _killFeed.KillAnnounced += OnKillAnnounced;
    }

    public override void _ExitTree()
    {
        if (_killFeed != null && IsInstanceValid(_killFeed)) _killFeed.KillAnnounced -= OnKillAnnounced;
    }

    private void OnKillAnnounced(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
        int totalPoints, long assistPeerId, int assistPoints)
    {
        int myPeerId = GetMultiplayerAuthority();
        if (victimPeerId == myPeerId) Deaths++;
        if (!IsMultiplayerAuthority()) return;

        if (killerPeerId == myPeerId) AwardKill(totalPoints);
        else if (assistPeerId == myPeerId) AwardPoints(assistPoints, "assist");
    }

    /// <summary>Awards the killer's total plus the multi-kill bonus, which is a killer-side event:
    /// only this device knows how recently this player last killed someone.</summary>
    private void AwardKill(int totalPoints)
    {
        ulong now = Time.GetTicksMsec();
        bool multiKill = _hasPriorKill && now - _lastKillMsec <= MultiKillWindowMsec;
        _lastKillMsec = now;
        _hasPriorKill = true;
        Kills++;

        if (multiKill) EmitSignal(SignalName.MultiKillAwarded, KillStyleBonus.MultiKillBonus);
        AwardPoints(totalPoints + (multiKill ? KillStyleBonus.MultiKillBonus : 0), "kill");
    }

    private void AwardPoints(int points, string source)
    {
        if (points <= 0)
        {
            GD.PushWarning($"PlayerScore ignored a zero-point {source} award.");
            return;
        }
        TotalPoints += points;
        EmitSignal(SignalName.ScoreChanged, TotalPoints);
    }
}