using Godot;

public partial class GameWorld : Node3D
{
    [Export] public PackedScene PlayerScene { get; set; } = null!;
    private MultiplayerSpawner _spawner = null!;
    private int _spawnIndex;

    public override void _Ready()
    {
        _spawner = GetNode<MultiplayerSpawner>("MultiplayerSpawner");
        _spawner.SpawnPath = new NodePath("../Players");
        _spawner.SpawnFunction = new Callable(this, nameof(SpawnPlayer));
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        if (Multiplayer.IsServer()) SpawnForPeer(1);
    }

    private void OnPeerConnected(long peerId)
    {
        if (Multiplayer.IsServer()) SpawnForPeer((int)peerId);
    }
    private void OnPeerDisconnected(long peerId)
    {
        var player = GetNodeOrNull<Node>($"Players/Player_{peerId}");
        player?.QueueFree();
    }
    private void SpawnForPeer(int peerId) => _spawner.Spawn(peerId);

    private Node SpawnPlayer(Variant data)
    {
        int peerId = (int)data.AsInt64();
        var player = PlayerScene.Instantiate<CharacterBody3D>();
        player.Name = $"Player_{peerId}";
        player.SetMultiplayerAuthority(peerId);
        player.Position = new Vector3((_spawnIndex++ % 4) * 2 - 3, 0.1f, (_spawnIndex / 4) * 2);
        return player;
    }
    public override void _ExitTree()
    {
        Multiplayer.PeerConnected -= OnPeerConnected;
        Multiplayer.PeerDisconnected -= OnPeerDisconnected;
    }
}
