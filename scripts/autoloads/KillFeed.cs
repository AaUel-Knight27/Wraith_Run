using Godot;

/// <summary>
/// Autoload (see project.godot) that carries kill confirmations between peers and hands them to
/// whatever wants to react - the kill feed in the HUD, and each owner's PlayerScore.
///
/// It exists as an autoload rather than a node inside the match so it always has the same path on
/// every peer, which is what an RPC needs, and because the SDLC architecture doc already puts
/// cross-cutting match state in scripts/autoloads/ (GameManager, NetworkManager, AudioManager).
///
/// Only the victim's authoritative device publishes a death (same rule Health already applies to
/// damage), so AnnounceKill accepts a call only from the victim peer itself.
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
        Rpc(MethodName.AnnounceKill, killerPeerId, victimPeerId, styleFlags, basePoints, totalPoints,
            assistPeerId, assistPoints, struggles);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void AnnounceKill(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
        int totalPoints, long assistPeerId, int assistPoints, bool struggles)
    {
        long sender = Multiplayer.GetRemoteSenderId();
        // 0 means this execution is the local call_local copy, published by the victim's own device.
        // A bot has no device of its own: the host simulates it, so the host (peer 1) is the one
        // legitimately speaking for a bot victim.
        bool senderOk = sender == 0 || sender == victimPeerId || (sender == 1 && BotRoster.IsBot(victimPeerId));
        if (!senderOk) return;

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