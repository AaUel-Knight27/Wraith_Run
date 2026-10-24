using System.Collections.Generic;
using Godot;

/// <summary>
/// The hub: a walkable warehouse home base that is also the lobby. Lives under Hub.tscn's GameWorld
/// node (GameWorld.IsHub) and owns everything that is hub-only: who is in the room, who can see
/// whom, the tablet, the weapon wall, the props, text chat, and the hand-off into a match.
///
/// How it works with the rest of the game:
///  * It always runs on a real ENet server. Alone, that is the same loopback "solo" server VS BOTS
///    uses; pressing HOST swaps it for a LAN server (the player you are already controlling stays),
///    pressing JOIN swaps it for a client of somebody else's hub.
///  * In team modes each team stands in its own copy of the warehouse. Nothing is duplicated:
///    the host simply never tells a peer about players on the other team (the player's
///    MultiplayerSynchronizer visibility, which also decides whether the node spawns there at all),
///    so both teams use the same coordinates without ever seeing, hearing or hitting each other.
///  * START GAME makes every peer load GameWorld.tscn (the match) with the connection and teams kept.
/// </summary>
public partial class HubController : Node
{
	public const string HubScenePath = "res://scenes/Hub.tscn";
	public const string MatchScenePath = "res://scenes/GameWorld.tscn";
	private const int MaxChatLength = 140;
	private const double MinSecondsBetweenChats = 0.4;

	public static HubController? Instance { get; private set; }
	private static bool _firstEntry = true;

	[Signal] public delegate void ChatReceivedEventHandler(string sender, int team, string text);
	[Signal] public delegate void RosterChangedEventHandler();

	private GameWorld _world = null!;
	private HubTablet? _tablet;
	private readonly HashSet<int> _readyPeers = new();
	private readonly Dictionary<int, double> _lastChat = new();
	private bool _worldReady;
	private bool _leavingForMatch;

	public bool IsHost => Multiplayer.HasMultiplayerPeer() && Multiplayer.IsServer() && (LanSessionNode?.IsHosting ?? false);
	public bool IsClient => Multiplayer.HasMultiplayerPeer() && !Multiplayer.IsServer();
	public bool InSession => IsHost || IsClient;
	public string StatusLine { get; private set; } = string.Empty;
	private LanSession? LanSessionNode => GetNodeOrNull<LanSession>("/root/LanSession");

	public override void _Ready()
	{
		Instance = this;
		_world = GetParent<GameWorld>();
		MatchManager? match = MatchManager.Instance;
		if (match != null)
		{
			match.MatchStarting += OnMatchStarting;
			match.HubReadyReported += OnHubReadyReported;
			match.HubReportRequested += OnHubReportRequested;
		}
		Multiplayer.PeerConnected += OnPeerConnected;
		Multiplayer.PeerDisconnected += OnPeerDisconnected;
		Multiplayer.ConnectedToServer += OnConnectedToServer;
		Multiplayer.ConnectionFailed += OnConnectionFailed;
		Multiplayer.ServerDisconnected += OnServerDisconnected;
		if (LanSessionNode != null) LanSessionNode.ConnectionFailed += OnLanSessionFailed;

		// The very first time the hub opens in this run of the game the lobby starts on Team Deathmatch
		// (the mode the two warehouses are for). Later visits keep whatever the host last picked.
		bool firstEntry = _firstEntry;
		_firstEntry = false;
		if (firstEntry && match != null) match.GameMode = MatchManager.Mode.TeamDeathmatch;

		if (!Multiplayer.HasMultiplayerPeer())
		{
			// A private one-person session so every code path (spawning, damage rules, teams) is the
			// real one. (GameWorld normally starts it already when nobody has.)
			LanSessionNode?.StartSolo();
			match?.PrepareHostMatch();
		}
		else
		{
			// Back from a match: same connection, same people.
			match?.ResetForHub();
		}
	}

	public override void _ExitTree()
	{
		if (Instance == this) Instance = null;
		if (MatchManager.Instance != null)
		{
			MatchManager.Instance.MatchStarting -= OnMatchStarting;
			MatchManager.Instance.HubReadyReported -= OnHubReadyReported;
			MatchManager.Instance.HubReportRequested -= OnHubReportRequested;
		}
		Multiplayer.PeerConnected -= OnPeerConnected;
		Multiplayer.PeerDisconnected -= OnPeerDisconnected;
		Multiplayer.ConnectedToServer -= OnConnectedToServer;
		Multiplayer.ConnectionFailed -= OnConnectionFailed;
		Multiplayer.ServerDisconnected -= OnServerDisconnected;
		if (LanSessionNode != null) LanSessionNode.ConnectionFailed -= OnLanSessionFailed;
		HubState.Active = false;
	}

	/// <summary>GameWorld calls this once the warehouse is loaded and collidable.</summary>
	public void OnWorldReady()
	{
		_worldReady = true;
		HubProps.Build(_world);
		HubWeaponWall.Build(_world);

		_tablet = new HubTablet { Name = "HubTablet" };
		_world.AddChild(_tablet);

		if (Multiplayer.IsServer())
		{
			_world.SpawnHubPlayer(1);
			foreach (int peer in Multiplayer.GetPeers()) _world.SpawnHubPlayer(peer);
			RefreshVisibility();
			// Anyone who reached their hub before this one existed has already tried to report in
			// and been missed: ask everyone to do it again.
			MatchManager.Instance?.AskPeersToReportHub();
		}
		else
		{
			MatchManager.Instance?.ReportHubReadyToHost();
		}
		EmitSignal(SignalName.RosterChanged);
	}

	// -------------------------------------------------------------------------------------------
	// Session actions (called by the tablet)
	// -------------------------------------------------------------------------------------------

	/// <summary>Open the lobby to the network. Mode and map are already set on MatchManager.</summary>
	public bool HostSession(string gameName)
	{
		LanSession? session = LanSessionNode;
		MatchManager? match = MatchManager.Instance;
		if (session == null || match == null) return false;
		match.PrepareHostMatch();
		Error error = session.StartHost(string.IsNullOrWhiteSpace(gameName) ? "Wraith Run" : gameName.Trim());
		if (error != Error.Ok)
		{
			StatusLine = $"Could not host ({error}). Is another game already using port {LanSession.GamePort}?";
			// Hosting failed after Stop(): get the private session back so the room keeps working.
			session.StartSolo();
			return false;
		}
		match.BeginHostLobby();
		StatusLine = string.Empty;
		EmitSignal(SignalName.RosterChanged);
		return true;
	}

	/// <summary>Leave this room and join somebody else's hub.</summary>
	public bool JoinSession(string address, int port = LanSession.GamePort)
	{
		LanSession? session = LanSessionNode;
		MatchManager? match = MatchManager.Instance;
		if (session == null || match == null) return false;
		_world.RemoveSoloHubPlayer();
		match.PrepareClientMatch();
		Error error = session.Join(address.Trim(), port);
		if (error != Error.Ok)
		{
			StatusLine = $"Could not connect to {address}:{port} ({error}).";
			CallDeferred(nameof(RestartHub));
			return false;
		}
		StatusLine = $"Connecting to {address.Trim()}...";
		return true;
	}

	/// <summary>Back to a private, one-person hub.</summary>
	public void LeaveSession(bool restartHub = true)
	{
		GameUiState.Reset();
		LanSessionNode?.Stop();
		MatchManager.Instance?.EndSession();
		if (restartHub) RestartHub();
	}

	public void StartMatch()
	{
		if (!Multiplayer.IsServer()) return;
		MatchManager.Instance?.StartMatchWhenReady();
	}

	/// <summary>Alpha and Bravo get separate warehouses (and separate chat) only in Team Deathmatch.
	/// Every other mode - crews, free-for-all style - is one shared room.</summary>
	public static bool SplitRooms(MatchManager? match) => match != null && match.GameMode == MatchManager.Mode.TeamDeathmatch;

	private void RestartHub() => GetTree().ChangeSceneToFile(HubScenePath);

	// -------------------------------------------------------------------------------------------
	// Network events
	// -------------------------------------------------------------------------------------------

	private void OnPeerConnected(long peerId)
	{
		if (!Multiplayer.IsServer() || !_worldReady) return;
		// The person is spawned right away but hidden from everyone, including themselves, until
		// their own copy of the hub reports in (a peer that is not in the hub yet cannot spawn one).
		_world.SpawnHubPlayer((int)peerId);
		EmitSignal(SignalName.RosterChanged);
	}

	private void OnPeerDisconnected(long peerId)
	{
		_readyPeers.Remove((int)peerId);
		_lastChat.Remove((int)peerId);
		EmitSignal(SignalName.RosterChanged);
	}

	private void OnConnectedToServer()
	{
		StatusLine = string.Empty;
		if (_worldReady) MatchManager.Instance?.ReportHubReadyToHost();
		EmitSignal(SignalName.RosterChanged);
	}

	private void OnConnectionFailed()
	{
		StatusLine = "Could not reach that game.";
		LanSessionNode?.Stop();
		RestartHub();
	}

	private void OnLanSessionFailed(string reason)
	{
		StatusLine = reason;
		LanSessionNode?.Stop();
		RestartHub();
	}

	private void OnServerDisconnected()
	{
		// The host closed the room (or the Wi-Fi dropped): fall back to a private hub.
		GameUiState.Reset();
		LanSessionNode?.Stop();
		MatchManager.Instance?.EndSession();
		RestartHub();
	}

	private void OnMatchStarting()
	{
		if (_leavingForMatch) return;
		_leavingForMatch = true;
		GameUiState.Reset();
		if (!OS.HasFeature("mobile")) Input.MouseMode = Input.MouseModeEnum.Visible;
		LoadingScreen.Instance?.Show();
		GetTree().ChangeSceneToFile(MatchScenePath);
	}

	private void OnHubReadyReported(long peerId)
	{
		if (!Multiplayer.IsServer() || !_worldReady) return;
		_readyPeers.Add((int)peerId);
		_world.SpawnHubPlayer((int)peerId);
		RefreshVisibility();
		EmitSignal(SignalName.RosterChanged);
	}

	private void OnHubReportRequested()
	{
		if (_worldReady && !Multiplayer.IsServer()) MatchManager.Instance?.ReportHubReadyToHost();
	}

	// -------------------------------------------------------------------------------------------
	// Who can see whom (server)
	// -------------------------------------------------------------------------------------------

	/// <summary>Recompute, for every player in the room, which connected peers are told about them.
	/// Team modes: only their own team. Solo modes: everyone in the hub.</summary>
	public void RefreshVisibility()
	{
		if (!Multiplayer.HasMultiplayerPeer() || !Multiplayer.IsServer()) return;
		MatchManager? match = MatchManager.Instance;
		bool teams = SplitRooms(match);
		Node? players = _world.GetNodeOrNull("Players");
		if (players == null) return;

		foreach (Node node in players.GetChildren())
		{
			if (!node.Name.ToString().StartsWith("Player_")) continue;
			if (!int.TryParse(node.Name.ToString().Substring("Player_".Length), out int ownerId)) continue;
			var sync = node.GetNodeOrNull<MultiplayerSynchronizer>("MultiplayerSynchronizer");
			if (sync == null) continue;

			foreach (int peer in Multiplayer.GetPeers())
			{
				bool inHub = _readyPeers.Contains(peer);
				bool sameSide = !teams || match == null || match.GetTeam(peer) == match.GetTeam(ownerId);
				sync.SetVisibilityFor(peer, inHub && sameSide);
			}
		}
	}

	// ---- the host's own view -----------------------------------------------------------------------
	// The host machine holds every player node (it is the server), so the per-peer visibility above
	// cannot hide the other team from the person sitting at it. Do that here: other-team players
	// are invisible, silent and non-solid on the host, exactly as they are on everybody else's screen.

	private double _viewTimer;

	public override void _Process(double delta)
	{
		if (!_worldReady || !Multiplayer.HasMultiplayerPeer() || !Multiplayer.IsServer()) return;
		_viewTimer += delta;
		if (_viewTimer < 0.25) return;
		_viewTimer = 0;
		ApplyHostView();
	}

	private void ApplyHostView()
	{
		MatchManager? match = MatchManager.Instance;
		bool teams = SplitRooms(match);
		int myTeam = match?.GetTeam(1) ?? -1;
		Node? players = _world.GetNodeOrNull("Players");
		if (players == null) return;
		foreach (Node node in players.GetChildren())
		{
			string name = node.Name.ToString();
			if (node is not Node3D body || !name.StartsWith("Player_")) continue;
			if (!int.TryParse(name.Substring("Player_".Length), out int ownerId) || ownerId == 1) continue;
			bool hide = teams && match != null && match.GetTeam(ownerId) != myTeam;
			if (body.Visible == !hide && (body.HasMeta("hub_hidden") == hide)) continue;
			body.Visible = !hide;
			body.SetMeta("hub_hidden", hide);
			SetSolid(body, !hide);
		}
	}

	private static void SetSolid(Node root, bool solid)
	{
		if (root is CollisionObject3D collider)
		{
			if (!solid && !collider.HasMeta("hub_layers"))
			{
				collider.SetMeta("hub_layers", new Godot.Collections.Array { collider.CollisionLayer, collider.CollisionMask });
				collider.CollisionLayer = 0;
				collider.CollisionMask = 0;
			}
			else if (solid && collider.HasMeta("hub_layers"))
			{
				var saved = collider.GetMeta("hub_layers").AsGodotArray();
				collider.CollisionLayer = (uint)saved[0].AsInt64();
				collider.CollisionMask = (uint)saved[1].AsInt64();
				collider.RemoveMeta("hub_layers");
			}
		}
		if (root is AudioStreamPlayer3D speaker)
		{
			if (!solid && !speaker.HasMeta("hub_db")) { speaker.SetMeta("hub_db", speaker.VolumeDb); speaker.VolumeDb = -80.0f; }
			else if (solid && speaker.HasMeta("hub_db")) { speaker.VolumeDb = (float)speaker.GetMeta("hub_db").AsDouble(); speaker.RemoveMeta("hub_db"); }
		}
		foreach (Node child in root.GetChildren()) SetSolid(child, solid);
	}

	// -------------------------------------------------------------------------------------------
	// Text chat: your team only in team modes, everyone otherwise
	// -------------------------------------------------------------------------------------------

	public void SendChat(string text)
	{
		text = CleanChat(text);
		if (text.Length == 0 || !Multiplayer.HasMultiplayerPeer()) return;
		if (Multiplayer.IsServer()) RouteChat(1, text);
		else RpcId(1, MethodName.SubmitChat, text);
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	public void SubmitChat(string text)
	{
		if (!Multiplayer.IsServer()) return;
		int sender = Multiplayer.GetRemoteSenderId();
		if (sender <= 1) return;
		double now = Time.GetTicksMsec() / 1000.0;
		if (_lastChat.TryGetValue(sender, out double last) && now - last < MinSecondsBetweenChats) return;
		_lastChat[sender] = now;
		text = CleanChat(text);
		if (text.Length == 0) return;
		RouteChat(sender, text);
	}

	private void RouteChat(int sender, string text)
	{
		MatchManager? match = MatchManager.Instance;
		bool teams = SplitRooms(match);
		int team = teams && match != null ? match.GetTeam(sender) : -1;
		string name = GameUiState.PlayerName(sender);

		var recipients = new List<int> { 1 };
		foreach (int peer in Multiplayer.GetPeers()) recipients.Add(peer);
		foreach (int peer in recipients)
		{
			if (teams && match != null && match.GetTeam(peer) != team) continue;
			if (peer == 1) EmitSignal(SignalName.ChatReceived, name, team, text);
			else RpcId(peer, MethodName.ReceiveChat, name, team, text);
		}
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	public void ReceiveChat(string sender, int team, string text) =>
		EmitSignal(SignalName.ChatReceived, sender, team, CleanChat(text));

	private static string CleanChat(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
		string text = raw.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
		return text.Length > MaxChatLength ? text.Substring(0, MaxChatLength) : text;
	}
}
