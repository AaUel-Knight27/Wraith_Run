using Godot;

/// <summary>
/// Autoload (see project.godot) that carries kill confirmations between peers and hands them to
/// whatever wants to react - the kill feed in the HUD, and each owner's PlayerScore.
///
/// It exists as an autoload rather than a node inside the match so it always has the same path on
/// every peer, which is what an RPC needs, and because the SDLC architecture doc already puts
/// cross-cutting match state in scripts/autoloads/ (GameManager, NetworkManager, AudioManager).
///
/// A client can submit only its own death report; the host confirms the victim is dead and then
/// broadcasts the event. The reported killer/style still depend on client-reported damage context.
/// </summary>
public partial class KillFeed : Node
{
    /// <summary>Lifetime of one kill-feed line (SDLC "Add kill feed message" task: 4 seconds).</summary>
    public const float MessageLifetimeSeconds = 4.0f;

    [Signal]
    public delegate void KillAnnouncedEventHandler(long killerPeerId, long victimPeerId, int styleFlags,
        int basePoints, int totalPoints, long assistPeerId, int assistPoints, bool struggles);

    /// <summary>Looks up the autoload without assuming a hardcoded path in every call site.</summary>
    public static KillFeed? From(Node node) => node.GetNodeOrNull<KillFeed>("/root/KillFeed");

    /// <summary>
    /// Publishes a confirmed kill to every peer. With no multiplayer peer configured - single-instance
    /// dev play, which is how this project is tested most of the time - it is delivered locally so
    /// kills still score and the feed still fills in.
    ///
    /// struggles is the victim's own read of how they died (see Health.ReceiveDamage) - whether
    /// PlayerAudio should play the victim's "instant, clean kill" death clip or the "didn't go
    /// instantly" one. It rides along here rather than through its own message because this is
    /// already the one channel guaranteed to reach every peer, in order, exactly once per kill.
    /// </summary>
    public void Publish(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
        int totalPoints, long assistPeerId, int assistPoints, bool struggles)
    {
        if (!Multiplayer.HasMultiplayerPeer())
        {
            EmitKill(killerPeerId, victimPeerId, styleFlags, basePoints, totalPoints, assistPeerId,
                assistPoints, struggles);
            return;
        }
        if (Multiplayer.IsServer())
            Rpc(MethodName.AnnounceKill, killerPeerId, victimPeerId, styleFlags, basePoints, totalPoints,
                assistPeerId, assistPoints, struggles);
        else
            RpcId(1, MethodName.SubmitKill, killerPeerId, victimPeerId, styleFlags, basePoints,
                totalPoints, assistPeerId, assistPoints, struggles);
    }

    /// <summary>Clients may report only their own death; the server checks the victim is actually
    /// dead, then relays the event through its authority-only broadcast.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void SubmitKill(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
        int totalPoints, long assistPeerId, int assistPoints, bool struggles)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (sender != victimPeerId) return;
        if (GetTree().GetNodesInGroup("players").Count == 0) return;
        CharacterBody3D? victim = null;
        foreach (Node node in GetTree().GetNodesInGroup("players"))
            if (node is CharacterBody3D body && PlayerIdentity.IdOf(body) == victimPeerId) { victim = body; break; }
        if (victim?.GetNodeOrNull<Health>("Health")?.IsDead != true) return;
        Rpc(MethodName.AnnounceKill, killerPeerId, victimPeerId, styleFlags, basePoints, totalPoints,
            assistPeerId, assistPoints, struggles);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void AnnounceKill(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
        int totalPoints, long assistPeerId, int assistPoints, bool struggles)
    {
        EmitKill(killerPeerId, victimPeerId, styleFlags, basePoints, totalPoints, assistPeerId,
            assistPoints, struggles);
    }

    private void EmitKill(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
        int totalPoints, long assistPeerId, int assistPoints, bool struggles) =>
        EmitSignal(SignalName.KillAnnounced, killerPeerId, victimPeerId, styleFlags, basePoints,
            totalPoints, assistPeerId, assistPoints, struggles);

    /// <summary>Feed text for one kill, shared by all peers so every client shows identical wording.</summary>
    public static string DescribeKill(long killerPeerId, long victimPeerId, int styleFlags, int totalPoints) =>
        KillStyleBonus.DescribeKill(killerPeerId, victimPeerId, (KillStyle)styleFlags, totalPoints);
}
