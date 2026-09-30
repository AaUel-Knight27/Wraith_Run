using Godot;
using System.Collections.Generic;

/// <summary>
/// Autoload (see project.godot, same pattern as KillFeed/SettingsManager) that owns everything a
/// Team Deathmatch match needs beyond what a single player already tracks for itself: which of the
/// two teams each peer is on, the running team score, the match clock, the Friendly Fire and Team
/// Indicators toggles, and the score/time limit the host picked on the "Play Locally" lobby screen.
///
/// Free-For-All (the existing "Deathmatch" mode) is untouched by any of this: GameMode defaults to
/// FreeForAll and every method here is a no-op (or returns a neutral value) while it stays that
/// way, so the mode that shipped before this file existed behaves exactly as it did before.
///
/// Team assignment follows the same authority model as everything else in this codebase: only the
/// server decides (see AssignTeamAndBroadcast, called from GameWorld), then broadcasts the decision
/// to every peer - including itself - through one RPC, so every peer's local PeerTeams dictionary
/// ends up identical without any client ever computing the assignment itself.
///
/// Team score works the other way around: it is never sent over the network directly. Every peer
/// already receives the same KillFeed.KillAnnounced broadcast (see KillFeed.cs) and, thanks to the
/// paragraph above, already agrees on every player's team, so every peer's own tally lands on the
/// same number independently - one less thing to keep in sync by hand, and one less RPC per kill.
/// </summary>
public partial class MatchManager : Node
{
    public enum Mode { FreeForAll, TeamDeathmatch }

    private const int TeamCount = 2;
    public static readonly string[] TeamNames = { "ALPHA", "BRAVO" };
    public static readonly Color[] TeamColors =
    {
        new(0.35f, 0.85f, 0.40f), // Alpha - green
        new(0.30f, 0.55f, 0.95f), // Bravo - blue
    };

    [Signal] public delegate void MatchEndedEventHandler(int winningTeam, string reason);

    public static MatchManager Instance { get; private set; } = null!;

    // ---- Host-configured, host-authoritative match rules --------------------------------------
    // Written directly by LanMenu's lobby screen while the host is still picking settings - the
    // same "the screen writes straight into the autoload's fields" pattern SettingsManager's own
    // screen already uses - then carried to every joining client by ReceiveHostConfig below.
    public Mode GameMode = Mode.FreeForAll;
    public bool FriendlyFire;
    public bool TeamIndicators = true;
    public int ScoreLimit = 50;
    public int TimeLimitMinutes = 10;

    // ---- Bots (host-only: bots are simulated by the host and reach everyone else as ordinary players) ----
    /// <summary>How many bots GameWorld spawns when the match starts. 0 = none. Set by LanMenu.</summary>
    public int BotCount;
    public BotDifficulty BotLevel = BotDifficulty.Normal;
    /// <summary>Lets bots ease off a little if a person is being crushed (see BotDirector).</summary>
    public bool AdaptiveBots = true;

    private readonly Dictionary<long, int> _peerTeams = new();
    private readonly int[] _teamScores = new int[TeamCount];
    private float _elapsedSeconds;
    private bool _matchEnded;
    // True from PrepareHostMatch/PrepareClientMatch (the lobby -> match handoff) until EndSession
    // (leaving to the main menu). The clock only runs while this is true, so it never counts up
    // while the player is sitting in a menu.
    private bool _sessionActive;

    public bool IsMatchEnded => _matchEnded;
    public int WinningTeam { get; private set; } = -1;

    /// <summary>Seconds since this match began, in every game mode (Free-For-All used to have no clock
    /// at all). On a joining client it starts from the host's clock (see ReceiveHostConfig), so both
    /// sides show roughly the same time. Read by the Tab scoreboard and the objectives screen.</summary>
    public float ElapsedSeconds => _elapsedSeconds;

    /// <summary>Only Team Deathmatch has a time limit today; Free-For-All runs until someone leaves.</summary>
    public bool HasTimeLimit => GameMode == Mode.TeamDeathmatch && TimeLimitMinutes > 0;

    /// <summary>Seconds left on the clock, or -1 when this mode has no time limit.</summary>
    public float TimeRemainingSeconds =>
        HasTimeLimit ? Mathf.Max(0.0f, TimeLimitMinutes * 60.0f - _elapsedSeconds) : -1.0f;

    public override void _Ready()
    {
        Instance = this;
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        KillFeed? killFeed = GetNodeOrNull<KillFeed>("/root/KillFeed");
        if (killFeed != null) killFeed.KillAnnounced += OnKillAnnounced;
    }

    public override void _ExitTree()
    {
        Multiplayer.PeerConnected -= OnPeerConnected;
        Multiplayer.PeerDisconnected -= OnPeerDisconnected;
    }

    public override void _Process(double delta)
    {
        if (!_sessionActive || _matchEnded) return;
        _elapsedSeconds += (float)delta;
        // Everything below is the Team Deathmatch time limit, unchanged; Free-For-All just keeps
        // the clock running for the scoreboard and never ends by time.
        if (!HasTimeLimit) return;
        if (_elapsedSeconds < TimeLimitMinutes * 60.0f) return;

        int winner = _teamScores[0] == _teamScores[1] ? -1 : (_teamScores[0] > _teamScores[1] ? 0 : 1);
        EndMatch(winner, "time");
    }

    // -------------------------------------------------------------------------------------------
    // Lobby -> match handoff
    // -------------------------------------------------------------------------------------------

    /// <summary>Called by LanMenu right before LanSession.StartHost. GameMode/FriendlyFire/
    /// TeamIndicators/ScoreLimit/TimeLimitMinutes are whatever the host just picked on the lobby
    /// screen and are left alone here; everything that belongs to one match run is reset so a
    /// second match started later in the same app session (once a return-to-lobby flow exists)
    /// never inherits the previous match's teams or score.</summary>
    public void PrepareHostMatch()
    {
        _peerTeams.Clear();
        _teamScores[0] = 0;
        _teamScores[1] = 0;
        _elapsedSeconds = 0.0f;
        _matchEnded = false;
        WinningTeam = -1;
        _sessionActive = true;
        MatchStats.Instance?.Reset();
    }

    /// <summary>GameWorld calls this when it loads. Through the normal lobby flow the clock is already
    /// running (PrepareHostMatch/PrepareClientMatch started it), so this changes nothing there; it only
    /// matters when GameWorld.tscn is run directly from the editor for single-instance testing, where
    /// no lobby ran and the scoreboard clock would otherwise sit at 00:00 forever.</summary>
    public void EnsureSessionActive() => _sessionActive = true;

    /// <summary>Called when the player leaves a running match for the main menu (see GameSession).
    /// Stops the clock; the next PrepareHostMatch/PrepareClientMatch starts everything fresh.</summary>
    public void EndSession()
    {
        _sessionActive = false;
        _peerTeams.Clear();
    }

    /// <summary>Called by LanMenu right before LanSession.Join. The real rules arrive moments later
    /// via ReceiveHostConfig once the connection is up; this only guarantees a clean slate rather
    /// than stale state left over from a previous match in the same app session.</summary>
    public void PrepareClientMatch()
    {
        GameMode = Mode.FreeForAll;
        FriendlyFire = false;
        TeamIndicators = true;
        BotCount = 0; // only the host ever spawns bots
        PrepareHostMatch();
    }

    // -------------------------------------------------------------------------------------------
    // Team assignment - the server decides, everyone receives the same broadcast
    // -------------------------------------------------------------------------------------------

    /// <summary>Server-only (a no-op on a client, harmless if ever called on one). GameWorld calls
    /// this for its own peer (id 1) exactly the way it self-spawns peer 1 in _Ready, since peer 1
    /// never raises Multiplayer.PeerConnected for itself; every other peer reaches this through
    /// OnPeerConnected below instead. A no-op outside Team Deathmatch, same as everything else
    /// here.</summary>
    public void AssignTeamAndBroadcast(long peerId)
    {
        if (GameMode != Mode.TeamDeathmatch) return;

        int countAlpha = 0, countBravo = 0;
        foreach (int assignedTeam in _peerTeams.Values)
        {
            if (assignedTeam == 0) countAlpha++;
            else if (assignedTeam == 1) countBravo++;
        }
        int team = countAlpha <= countBravo ? 0 : 1;

        if (!Multiplayer.HasMultiplayerPeer()) { ApplyTeamAssignment(peerId, team); return; }
        Rpc(MethodName.ReceiveTeamAssignment, peerId, team);
    }

    private void OnPeerConnected(long peerId)
    {
        if (!Multiplayer.IsServer()) return;
        // Reliable + targeted at exactly the peer that just connected: every other peer already
        // has the right values, this one has whatever GameMode/FriendlyFire/etc. defaulted to
        // when its own copy of this autoload was created (see PrepareClientMatch).
        RpcId(peerId, MethodName.ReceiveHostConfig, (int)GameMode, FriendlyFire, TeamIndicators,
            ScoreLimit, TimeLimitMinutes, _elapsedSeconds);
        // Teach the newcomer everyone who is already here (the host, other people, bots). The broadcast
        // below only carries the newcomer's own team, so without this a late joiner would not know who
        // is on which side.
        foreach (KeyValuePair<long, int> entry in _peerTeams)
            if (entry.Key != peerId) RpcId(peerId, MethodName.ReceiveTeamAssignment, entry.Key, entry.Value);
        AssignTeamAndBroadcast(peerId);
    }

    private void OnPeerDisconnected(long peerId) => _peerTeams.Remove(peerId);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveTeamAssignment(long peerId, int team) => ApplyTeamAssignment(peerId, team);

    private void ApplyTeamAssignment(long peerId, int team) => _peerTeams[peerId] = team;

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveHostConfig(int gameMode, bool friendlyFire, bool teamIndicators, int scoreLimit,
        int timeLimitMinutes, float hostElapsedSeconds)
    {
        GameMode = (Mode)gameMode;
        FriendlyFire = friendlyFire;
        TeamIndicators = teamIndicators;
        ScoreLimit = scoreLimit;
        TimeLimitMinutes = timeLimitMinutes;
        _elapsedSeconds = hostElapsedSeconds;
        _matchEnded = false;
        WinningTeam = -1;
    }

    /// <summary>-1 when the peer has not been assigned a team yet (or GameMode is FreeForAll, which
    /// never assigns one at all).</summary>
    public int GetTeam(long peerId) => _peerTeams.TryGetValue(peerId, out int team) ? team : -1;

    /// <summary>True only while a Team Deathmatch match is running and both peers are known members
    /// of the same team. Shared by WeaponSwitcher's shooter-side pre-check and Health's
    /// victim-authoritative check so the two can never disagree about who is friendly.</summary>
    public bool AreTeammates(long peerA, long peerB)
    {
        if (GameMode != Mode.TeamDeathmatch) return false;
        int teamA = GetTeam(peerA);
        return teamA >= 0 && teamA == GetTeam(peerB);
    }

    // -------------------------------------------------------------------------------------------
    // Team score / match end
    // -------------------------------------------------------------------------------------------

    public int GetTeamScore(int team) => team == 0 || team == 1 ? _teamScores[team] : 0;

    public static string TeamName(int team) => team == 0 ? TeamNames[0] : team == 1 ? TeamNames[1] : "UNASSIGNED";

    public static Color TeamColor(int team) => team == 0 ? TeamColors[0] : team == 1 ? TeamColors[1] : Colors.White;

    /// <summary>Every peer receives the identical KillFeed broadcast (see KillFeed.cs) and already
    /// agrees on every player's team, so every peer's tally increments on the exact same kills in
    /// the exact same order - no extra network traffic needed just to keep the team score in sync.</summary>
    private void OnKillAnnounced(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
        int totalPoints, long assistPeerId, int assistPoints, bool struggles)
    {
        if (GameMode != Mode.TeamDeathmatch || _matchEnded) return;

        int killerTeam = GetTeam(killerPeerId);
        // An unassigned killer shouldn't happen once a match is running; a team-kill only reaches
        // here at all when Friendly Fire is on. Either way no team's score should move - crediting
        // a team for killing its own member would be wrong twice.
        if (killerTeam < 0 || killerTeam == GetTeam(victimPeerId)) return;

        _teamScores[killerTeam]++;
        if (_teamScores[killerTeam] >= ScoreLimit) EndMatch(killerTeam, "score");
    }

    private void EndMatch(int winningTeam, string reason)
    {
        if (_matchEnded) return;
        _matchEnded = true;
        WinningTeam = winningTeam;
        EmitSignal(SignalName.MatchEnded, winningTeam, reason);
    }
}
