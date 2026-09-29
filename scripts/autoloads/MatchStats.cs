using Godot;
using System.Collections.Generic;

/// <summary>
/// Autoload (see project.godot, next to KillFeed/MatchManager) that keeps every player's kills,
/// deaths and assists for the current match, identically on every peer, for the Tab scoreboard and
/// the pause menu's objectives screen.
///
/// Why this exists instead of reading PlayerScore: PlayerScore.Kills is only counted on the
/// owning device (its own doc says so - "display counters local to the owning device for now"), so
/// a remote player's kill count is simply not available on your machine. This class uses the same
/// trick MatchManager already uses for the team score: every peer receives the identical
/// KillFeed.KillAnnounced broadcast, in the same order, so every peer's own tally ends up the same
/// without a single extra RPC per kill.
///
/// Counting rules match what the HUD already shows for the local player, so the scoreboard and the
/// "K x / D y" line can never disagree: every announced kill is one death for the victim and one
/// kill for the killer (unless the killer is the victim or unknown), and the assist peer, if any,
/// gets one assist.
///
/// The one thing a tally-from-events design cannot do is know about kills that happened BEFORE this
/// peer joined. That is what the snapshot below is for: when a peer connects, the server sends it
/// the current table once (reliable, so it arrives before any later kill event), and from then on
/// the peer just keeps counting like everyone else.
/// </summary>
public partial class MatchStats : Node
{
    public sealed class Stats
    {
        public int Kills;
        public int Deaths;
        public int Assists;

        public float KdRatio => GameUiState.KdRatio(Kills, Deaths);
    }

    public static MatchStats Instance { get; private set; } = null!;

    private static readonly Stats Empty = new();

    private readonly Dictionary<long, Stats> _stats = new();
    private KillFeed? _killFeed;

    public override void _Ready()
    {
        Instance = this;
        _killFeed = KillFeed.From(this);
        if (_killFeed != null) _killFeed.KillAnnounced += OnKillAnnounced;
        else GD.PushWarning("MatchStats could not reach the KillFeed autoload; the scoreboard will stay at 0.");
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
    }

    public override void _ExitTree()
    {
        if (_killFeed != null && IsInstanceValid(_killFeed)) _killFeed.KillAnnounced -= OnKillAnnounced;
        Multiplayer.PeerConnected -= OnPeerConnected;
        Multiplayer.PeerDisconnected -= OnPeerDisconnected;
    }

    /// <summary>Read-only lookup. A peer that has not scored or died yet has no entry, so this hands
    /// back a shared all-zero record instead of creating one - callers must not write to it.</summary>
    public Stats Get(long peerId) => _stats.TryGetValue(peerId, out Stats? stats) ? stats : Empty;

    /// <summary>Called at the start of every match (MatchManager.PrepareHostMatch/PrepareClientMatch)
    /// and when leaving to the main menu, so a second match never inherits the first one's numbers.</summary>
    public void Reset() => _stats.Clear();

    private Stats GetOrCreate(long peerId)
    {
        if (!_stats.TryGetValue(peerId, out Stats? stats))
        {
            stats = new Stats();
            _stats[peerId] = stats;
        }
        return stats;
    }

    private void OnKillAnnounced(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
        int totalPoints, long assistPeerId, int assistPoints, bool struggles)
    {
        GetOrCreate(victimPeerId).Deaths++;
        if (killerPeerId >= 0 && killerPeerId != victimPeerId) GetOrCreate(killerPeerId).Kills++;
        if (assistPeerId >= 0 && assistPeerId != victimPeerId && assistPeerId != killerPeerId)
            GetOrCreate(assistPeerId).Assists++;
    }

    // -------------------------------------------------------------------------------------------
    // Late join
    // -------------------------------------------------------------------------------------------

    private void OnPeerConnected(long peerId)
    {
        if (!Multiplayer.IsServer() || _stats.Count == 0) return;

        int count = _stats.Count;
        var ids = new long[count];
        var kills = new int[count];
        var deaths = new int[count];
        var assists = new int[count];
        int i = 0;
        foreach (KeyValuePair<long, Stats> entry in _stats)
        {
            ids[i] = entry.Key;
            kills[i] = entry.Value.Kills;
            deaths[i] = entry.Value.Deaths;
            assists[i] = entry.Value.Assists;
            i++;
        }
        RpcId(peerId, MethodName.ReceiveSnapshot, ids, kills, deaths, assists);
    }

    private void OnPeerDisconnected(long peerId) => _stats.Remove(peerId);

    /// <summary>Server -> newly connected peer only (RpcMode.Authority: the server owns this node,
    /// so a client cannot call it on another client). Replaces the table rather than merging, since
    /// a peer that has just connected has nothing of its own worth keeping.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveSnapshot(long[] peerIds, int[] kills, int[] deaths, int[] assists)
    {
        int count = Mathf.Min(Mathf.Min(peerIds.Length, kills.Length), Mathf.Min(deaths.Length, assists.Length));
        _stats.Clear();
        for (int i = 0; i < count; i++)
            _stats[peerIds[i]] = new Stats { Kills = kills[i], Deaths = deaths[i], Assists = assists[i] };
    }
}
