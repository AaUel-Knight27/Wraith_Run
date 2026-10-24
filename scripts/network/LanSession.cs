using Godot;
using System;
using System.Text;

/// <summary>Persistent LAN connection and discovery broadcaster.</summary>
public partial class LanSession : Node
{
	[Signal] public delegate void ConnectionFailedEventHandler(string reason);
	[Signal] public delegate void ConnectionTimeoutEventHandler();

	public const int GamePort = 7777;
	public const int DiscoveryPort = 7778;
	private const int MaxPlayers = 15;

	public bool IsHosting { get; private set; }
	public int ActiveGamePort { get; private set; } = GamePort;
	public string GameName { get; private set; } = string.Empty;
	private string _sessionId = string.Empty;
	private PacketPeerUdp? _broadcastPeer;
	private ulong _nextBroadcastMs;
	private bool _connecting;

	public override void _Ready()
	{
		Multiplayer.ConnectedToServer += OnConnectedToServer;
		Multiplayer.ConnectionFailed += OnConnectionFailed;
		Multiplayer.ServerDisconnected += OnServerDisconnected;
	}

	public override void _ExitTree()
	{
		Multiplayer.ConnectedToServer -= OnConnectedToServer;
		Multiplayer.ConnectionFailed -= OnConnectionFailed;
		Multiplayer.ServerDisconnected -= OnServerDisconnected;
		Stop();
	}

	public Error StartHost(string gameName, int preferredPort = GamePort)
	{
		Stop();
		var peer = new ENetMultiplayerPeer();
		int port = preferredPort;
		Error error = peer.CreateServer(port, MaxPlayers);
		if (error != Error.Ok)
		{
			for (int p = preferredPort + 1; p <= preferredPort + 5; p++)
			{
				peer = new ENetMultiplayerPeer();
				error = peer.CreateServer(p, MaxPlayers);
				if (error == Error.Ok)
				{
					port = p;
					break;
				}
			}
		}
		if (error != Error.Ok) return error;

		ActiveGamePort = port;
		Multiplayer.MultiplayerPeer = peer;
		IsHosting = true;
		_sessionId = Guid.NewGuid().ToString("N");
		GameName = string.IsNullOrWhiteSpace(gameName) ? "Wraith Run LAN" : gameName.Trim();
		_broadcastPeer = new PacketPeerUdp();
		_broadcastPeer.SetBroadcastEnabled(true);
		_broadcastPeer.SetDestAddress("255.255.255.255", DiscoveryPort);
		BroadcastNow();
		return Error.Ok;
	}

	/// <summary>
	/// A private one-person match (VS BOTS).
	/// </summary>
	public Error StartSolo()
	{
		Stop();
		var peer = new ENetMultiplayerPeer();
		peer.Call("set_bind_ip", "127.0.0.1");
		Error error = peer.CreateServer(GamePort, 1);
		if (error != Error.Ok) return error;

		ActiveGamePort = GamePort;
		Multiplayer.MultiplayerPeer = peer;
		IsHosting = false;
		GameName = "Solo";
		return Error.Ok;
	}

	public Error Join(string address, int port = GamePort)
	{
		Stop();
		var peer = new ENetMultiplayerPeer();
		Error error = peer.CreateClient(address, port);
		if (error == Error.Ok)
		{
			Multiplayer.MultiplayerPeer = peer;
			_connecting = true;
			var timer = GetTree().CreateTimer(10.0);
			timer.Timeout += OnConnectionTimeout;
		}
		return error;
	}

	private void OnConnectionTimeout()
	{
		if (!_connecting) return;
		_connecting = false;
		Stop();
		EmitSignal(SignalName.ConnectionTimeout);
		EmitSignal(SignalName.ConnectionFailed, "Connection timed out after 10 seconds.");
	}

	private void OnConnectedToServer()
	{
		_connecting = false;
	}

	private void OnConnectionFailed()
	{
		_connecting = false;
		Stop();
		EmitSignal(SignalName.ConnectionFailed, "Failed to connect to host server.");
	}

	private void OnServerDisconnected()
	{
		_connecting = false;
		Stop();
		EmitSignal(SignalName.ConnectionFailed, "Server disconnected.");
	}

	public void Stop()
	{
		_connecting = false;
		_broadcastPeer?.Close();
		_broadcastPeer = null;
		IsHosting = false;
		if (Multiplayer.MultiplayerPeer != null)
			Multiplayer.MultiplayerPeer.Close();
		Multiplayer.MultiplayerPeer = null;
	}

	public override void _Process(double delta)
	{
		if (IsHosting && Time.GetTicksMsec() >= _nextBroadcastMs) BroadcastNow();
	}

	private void BroadcastNow()
	{
		if (_broadcastPeer == null) return;
		string ip = GetAdvertisedAddress();
		string json = Json.Stringify(new Godot.Collections.Dictionary
		{
			["session"] = _sessionId,
			["name"] = GameName,
			["host"] = ip,
			["port"] = ActiveGamePort,
			["players"] = Multiplayer.GetPeers().Length + 1,
			["map"] = MatchManager.Instance?.MapDisplayName ?? "Default",
			["mode"] = MatchManager.Instance?.ModeRules?.DisplayName ?? "Default",
		});
		_broadcastPeer.PutPacket(Encoding.UTF8.GetBytes(json));
		// Loopback makes Debug > Run Multiple Instances discover a local host too.
		_broadcastPeer.SetDestAddress("127.0.0.1", DiscoveryPort);
		_broadcastPeer.PutPacket(Encoding.UTF8.GetBytes(json));
		_broadcastPeer.SetDestAddress("255.255.255.255", DiscoveryPort);
		_nextBroadcastMs = Time.GetTicksMsec() + 1000;
	}

	private static string GetAdvertisedAddress()
	{
		foreach (string address in IP.GetLocalAddresses())
			if (!address.StartsWith("127.") && !address.StartsWith("169.254.") && address.Contains('.'))
				return address;
		return "127.0.0.1";
	}
}
