using Godot;
using System;
using System.Text;

/// <summary>Persistent LAN connection and discovery broadcaster.</summary>
public partial class LanSession : Node
{
    public const int GamePort = 7777;
    public const int DiscoveryPort = 7778;
    private const int MaxPlayers = 8;

    public bool IsHosting { get; private set; }
    public string GameName { get; private set; } = string.Empty;
    private PacketPeerUdp? _broadcastPeer;
    private ulong _nextBroadcastMs;

    public Error StartHost(string gameName)
    {
        Stop();
        var peer = new ENetMultiplayerPeer();
        Error error = peer.CreateServer(GamePort, MaxPlayers);
        if (error != Error.Ok) return error;

        Multiplayer.MultiplayerPeer = peer;
        IsHosting = true;
        GameName = string.IsNullOrWhiteSpace(gameName) ? "Wraith Run LAN" : gameName.Trim();
        _broadcastPeer = new PacketPeerUdp();
        _broadcastPeer.SetBroadcastEnabled(true);
        _broadcastPeer.SetDestAddress("255.255.255.255", DiscoveryPort);
        BroadcastNow();
        return Error.Ok;
    }

    public Error Join(string address)
    {
        Stop();
        var peer = new ENetMultiplayerPeer();
        Error error = peer.CreateClient(address, GamePort);
        if (error == Error.Ok) Multiplayer.MultiplayerPeer = peer;
        return error;
    }

    public void Stop()
    {
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
            ["name"] = GameName,
            ["host"] = ip,
            ["players"] = Multiplayer.GetPeers().Length + 1,
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
