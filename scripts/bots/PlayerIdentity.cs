using Godot;

/// <summary>
/// Who a player node "is". Humans are identified by their multiplayer authority (their peer id). Bots
/// are all simulated by the host, so they share authority 1 and instead carry a unique fake id in node
/// metadata. Everything that used GetMultiplayerAuthority() as an identity (scores, teams, kill feed,
/// damage attribution) goes through IdOf() so a bot is never confused with the host.
/// </summary>
public static class PlayerIdentity
{
	public const string BotMeta = "bot_id";

	public static bool IsBot(Node? player) => player != null && player.HasMeta(BotMeta);

	public static long IdOf(Node? player)
	{
		if (player == null) return 0;
		if (player.HasMeta(BotMeta)) return player.GetMeta(BotMeta).AsInt64();
		return player.GetMultiplayerAuthority();
	}

	/// <summary>Authority check that tolerates an ENet peer while it is connecting or has been
	/// disconnected. Node.IsMultiplayerAuthority() asks ENet for its local peer id and logs an error
	/// when ENet is inactive, which can otherwise happen every frame on a player node.</summary>
	public static bool HasAuthority(Node? node)
	{
		if (node == null) return false;
		MultiplayerPeer? peer = node.Multiplayer.MultiplayerPeer;
		if (peer is ENetMultiplayerPeer &&
			peer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected)
			return false;
		return node.IsMultiplayerAuthority();
	}

	/// <summary>True only for the person sitting at this machine (never for a bot the host simulates).</summary>
	public static bool IsLocalHuman(Node? player) =>
		player != null && HasAuthority(player) && !player.HasMeta(BotMeta);
}
