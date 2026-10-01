using Godot;
using System;
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
    public enum Mode
    {
        FreeForAll = 0, Default = FreeForAll, TeamDeathmatch = 1,
        PureChase = 2, StormRespawn = 3, ClanChase = 4, CursedDeathmatch = 5,
        ConvoyAssault = 6, DuoBuggy = 7
    }

    public enum ArenaMap { Warzone = 0, Ashfields = 1, Depot = 2, Lab = 3 }

    public static readonly ArenaMap[] SelectableMaps =
    {
        ArenaMap.Warzone, ArenaMap.Ashfields, ArenaMap.Depot, ArenaMap.Lab
    };

    public ArenaMap SelectedMap = ArenaMap.Warzone;
    public string MapScenePath => SelectedMap switch
    {
        ArenaMap.Ashfields => "res://scenes/maps/Ashfields.tscn",
        ArenaMap.Depot => "res://scenes/maps/Depot.tscn",
        ArenaMap.Lab => "res://scenes/maps/Lab.tscn",
        _ => "res://scenes/maps/Warzone.tscn",
    };
    public string MapDisplayName => SelectedMap switch
    {
        ArenaMap.Ashfields => "Ashfields",
        ArenaMap.Depot => "Depot",
        ArenaMap.Lab => "Lab",
        _ => "Warzone",
    };
    public static string MapName(ArenaMap map) => map switch
    {
        ArenaMap.Ashfields => "Ashfields",
        ArenaMap.Depot => "Depot",
        ArenaMap.Lab => "Lab",
        _ => "Warzone",
    };

    public static readonly Mode[] SelectableModes =
    {
        Mode.Default, Mode.PureChase, Mode.StormRespawn, Mode.ClanChase,
        Mode.CursedDeathmatch, Mode.ConvoyAssault, Mode.DuoBuggy
    };

    private Mode _cachedRulesMode = (Mode)(-1);
    private GameModeBase? _cachedRules;
    public GameModeBase ModeRules
    {
        get
        {
            if (_cachedRules == null || _cachedRulesMode != GameMode)
            {
                _cachedRules = GameModeBase.For(GameMode);
                _cachedRulesMode = GameMode;
            }
            return _cachedRules;
        }
    }
    public bool IsTeamMode => GameMode == Mode.TeamDeathmatch || ModeRules.UsesTeams;

    private const int TeamCount = 2;
    public static readonly string[] TeamNames = { "ALPHA", "BRAVO" };
    public static readonly Color[] TeamColors =
    {
        new(0.35f, 0.85f, 0.40f), // Alpha - green
        new(0.30f, 0.55f, 0.95f), // Bravo - blue
    };

    [Signal] public delegate void MatchEndedEventHandler(int winningTeam, string reason);
    [Signal] public delegate void LobbyChangedEventHandler();
    [Signal] public delegate void MatchStartingEventHandler();

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
    /// <summary>Selected total match roster, including the local host/player.</summary>
    public int LobbyPlayerCount = 6;
    public BotDifficulty BotLevel = BotDifficulty.Normal;
    /// <summary>Lets bots ease off a little if a person is being crushed (see BotDirector).</summary>
    public bool AdaptiveBots = true;

    private readonly Dictionary<long, int> _peerTeams = new();
    private readonly Dictionary<long, bool> _lobbyReady = new();
    private readonly int[] _teamScores = new int[TeamCount];
    private float _elapsedSeconds;
    private bool _matchEnded;
    public bool HostConfigReceived { get; private set; }
    // True from PrepareHostMatch/PrepareClientMatch (the lobby -> match handoff) until EndSession
    // (leaving to the main menu). The clock only runs while this is true, so it never counts up
    // while the player is sitting in a menu.
    private bool _sessionActive;

    public bool IsMatchEnded => _matchEnded;
    public int WinningTeam { get; private set; } = -1;

    public bool LocalPlayerReady => _lobbyReady.TryGetValue(Multiplayer.GetUniqueId(), out bool ready) && ready;
    public bool CanHostStartMatch
    {
        get
        {
            if (!Multiplayer.IsServer()) return false;
            int humans = Multiplayer.GetPeers().Length + 1;
            if (humans + BotCount < ModeRules.MinimumPlayers) return false;
            if (GameMode == Mode.DuoBuggy && humans + BotCount != ModeRules.MaximumPlayers) return false;
            foreach (int peerId in Multiplayer.GetPeers())
                if (!_lobbyReady.TryGetValue(peerId, out bool ready) || !ready) return false;
            return true;
        }
    }

    public string LobbyReadySummary
    {
        get
        {
            if (_lobbyReady.Count == 0) return "Waiting for lobby connection…";
            var peerIds = new List<long>(_lobbyReady.Keys);
            peerIds.Sort();
            var lines = new List<string>(peerIds.Count);
            foreach (long peerId in peerIds)
            {
                string who = peerId == 1 ? "HOST" : $"PLAYER {peerId}";
                bool ready = _lobbyReady[peerId];
                lines.Add($"{who}   {(ready ? "READY" : "NOT READY")}");
            }
            return string.Join("\n", lines);
        }
    }

    /// <summary>Seconds since this match began, in every game mode (Free-For-All used to have no clock
    /// at all). On a joining client it starts from the host's clock (see ReceiveHostConfig), so both
    /// sides show roughly the same time. Read by the Tab scoreboard and the objectives screen.</summary>
    public float ElapsedSeconds => _elapsedSeconds;

    /// <summary>Mode-specific timed resolution; open-ended modes run until the host ends them.</summary>
    public bool HasTimeLimit => (GameMode == Mode.TeamDeathmatch || ModeRules.HasTimedResolution) &&
        TimeLimitMinutes > 0;

    /// <summary>Seconds left on the clock, or -1 when this mode has no time limit.</summary>
    public float TimeRemainingSeconds =>
        HasTimeLimit ? Mathf.Max(0.0f, TimeLimitMinutes * 60.0f - _elapsedSeconds) : -1.0f;

    public override void _Ready()
    {
        Instance = this;
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        KillFeed? killFeed = GetNodeOrNull<KillFeed>("/root/KillFeed");
        if (killFeed != null) killFeed.KillAnnounced += OnKillAnnounced;
    }

    public override void _ExitTree()
    {
        Multiplayer.PeerConnected -= OnPeerConnected;
        Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        Multiplayer.ConnectedToServer -= OnConnectedToServer;
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
        _lobbyReady.Clear();
        _teamScores[0] = 0;
        _teamScores[1] = 0;
        _elapsedSeconds = 0.0f;
        _matchEnded = false;
        WinningTeam = -1;
        _sessionActive = true;
        HostConfigReceived = true;
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
        HostConfigReceived = false;
        _peerTeams.Clear();
        _lobbyReady.Clear();
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
        HostConfigReceived = false;
        _sessionActive = false;
    }

    public void BeginHostLobby()
    {
        _sessionActive = false;
        _elapsedSeconds = 0.0f;
        _lobbyReady.Clear();
        _lobbyReady[1] = true;
        foreach (int peerId in Multiplayer.GetPeers()) _lobbyReady[peerId] = false;
        BroadcastLobbyState();
    }

    public void SetLocalReady(bool ready)
    {
        if (!Multiplayer.HasMultiplayerPeer()) return;
        if (Multiplayer.IsServer())
        {
            _lobbyReady[Multiplayer.GetUniqueId()] = true;
            BroadcastLobbyState();
        }
        else RpcId(1, MethodName.SubmitReadyState, ready);
    }

    public void StartMatchWhenReady()
    {
        if (!CanHostStartMatch || IsMatchEnded) return;
        Rpc(MethodName.ReceiveMatchStart);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void SubmitReadyState(bool ready)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (sender <= 1 || !_lobbyReady.ContainsKey(sender)) return;
        _lobbyReady[sender] = ready;
        BroadcastLobbyState();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void RequestLobbySnapshot()
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        SendLobbyStateTo((int)sender);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveLobbySnapshot(long[] peerIds, int[] readyStates)
    {
        _lobbyReady.Clear();
        int count = Math.Min(peerIds.Length, readyStates.Length);
        for (int i = 0; i < count; i++) _lobbyReady[peerIds[i]] = readyStates[i] != 0;
        EmitSignal(SignalName.LobbyChanged);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveMatchStart()
    {
        if (!Multiplayer.IsServer() && !HostConfigReceived) return;
        PrepareHostMatch();
        EmitSignal(SignalName.MatchStarting);
    }

    private void BroadcastLobbyState()
    {
        var peerIds = new long[_lobbyReady.Count];
        var readyStates = new int[_lobbyReady.Count];
        int index = 0;
        foreach (KeyValuePair<long, bool> entry in _lobbyReady)
        {
            peerIds[index] = entry.Key;
            readyStates[index] = entry.Value ? 1 : 0;
            index++;
        }
        Rpc(new StringName(nameof(ReceiveLobbySnapshot)), peerIds, readyStates);
    }

    private void SendLobbyStateTo(int peerId)
    {
        var peerIds = new long[_lobbyReady.Count];
        var readyStates = new int[_lobbyReady.Count];
        int index = 0;
        foreach (KeyValuePair<long, bool> entry in _lobbyReady)
        {
            peerIds[index] = entry.Key;
            readyStates[index] = entry.Value ? 1 : 0;
            index++;
        }
        RpcId(peerId, new StringName(nameof(ReceiveLobbySnapshot)), peerIds, readyStates);
    }

    private void OnConnectedToServer() => RpcId(1, MethodName.RequestLobbySnapshot);

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
        if (!IsTeamMode) return;

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
        if (Multiplayer.GetPeers().Length + 1 > ModeRules.MaximumPlayers)
        {
            GD.PushWarning($"Rejected peer {peerId}: {ModeRules.DisplayName} supports at most {ModeRules.MaximumPlayers} players.");
            Multiplayer.MultiplayerPeer?.DisconnectPeer((int)peerId);
            return;
        }
        _lobbyReady[peerId] = false;
        // Reliable + targeted at exactly the peer that just connected: every other peer already
        // has the right values, this one has whatever GameMode/FriendlyFire/etc. defaulted to
        // when its own copy of this autoload was created (see PrepareClientMatch).
        RpcId(peerId, MethodName.ReceiveHostConfig, (int)GameMode, FriendlyFire, TeamIndicators,
            ScoreLimit, TimeLimitMinutes, (int)SelectedMap, LobbyPlayerCount, _elapsedSeconds);
        // Teach the newcomer everyone who is already here (the host, other people, bots). The broadcast
        // below only carries the newcomer's own team, so without this a late joiner would not know who
        // is on which side.
        foreach (KeyValuePair<long, int> entry in _peerTeams)
            if (entry.Key != peerId) RpcId(peerId, MethodName.ReceiveTeamAssignment, entry.Key, entry.Value);
        AssignTeamAndBroadcast(peerId);
        BroadcastLobbyState();
    }

    private void OnPeerDisconnected(long peerId)
    {
        _peerTeams.Remove(peerId);
        if (Multiplayer.IsServer() && _lobbyReady.Remove(peerId)) BroadcastLobbyState();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveTeamAssignment(long peerId, int team) => ApplyTeamAssignment(peerId, team);

    private void ApplyTeamAssignment(long peerId, int team) => _peerTeams[peerId] = team;

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveHostConfig(int gameMode, bool friendlyFire, bool teamIndicators, int scoreLimit,
        int timeLimitMinutes, int arenaMap, int lobbyPlayerCount, float hostElapsedSeconds)
    {
        GameMode = System.Enum.IsDefined(typeof(Mode), gameMode) ? (Mode)gameMode : Mode.Default;
        FriendlyFire = friendlyFire;
        TeamIndicators = teamIndicators;
        ScoreLimit = scoreLimit;
        TimeLimitMinutes = timeLimitMinutes;
        SelectedMap = System.Enum.IsDefined(typeof(ArenaMap), arenaMap) ? (ArenaMap)arenaMap : ArenaMap.Warzone;
        LobbyPlayerCount = Math.Clamp(lobbyPlayerCount, ModeRules.MinimumPlayers, ModeRules.MaximumPlayers);
        _elapsedSeconds = hostElapsedSeconds;
        HostConfigReceived = true;
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
        if (!IsTeamMode) return false;
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
        if (!IsTeamMode || _matchEnded) return;

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
