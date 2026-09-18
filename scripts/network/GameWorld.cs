using Godot;

public partial class GameWorld : Node3D
{
    /// <summary>Marker3D nodes in the map that are placed in this group become spawn points.
    /// Falls back to a small grid if the map has none, so a bare test scene still works.</summary>
    private const string SpawnPointGroup = "spawn_point";

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
        ApplySpawnTransform(player, _spawnIndex++);
        return player;
    }
    /// <summary>
    /// Places a freshly spawned player on the map's next spawn marker, facing whichever way the
    /// marker faces. Spawning everyone on a 2m grid at the origin - the old behaviour - dropped all
    /// four players inside the central ruin on top of each other.
    ///
    /// The index is deliberately the same on every peer for a given player: SpawnFunction runs on
    /// the server and on each client with the same data, so both sides pick the same marker and the
    /// synchronizer has nothing to correct on the first frame.
    /// </summary>
    private void ApplySpawnTransform(Node3D player, int index)
    {
        var points = GetTree().GetNodesInGroup(SpawnPointGroup);
        if (points.Count == 0)
        {
            player.Position = new Vector3((index % 4) * 2 - 3, 0.1f, (index / 4) * 2);
            GD.PushWarning($"No nodes in the '{SpawnPointGroup}' group; falling back to a grid spawn.");
            return;
        }

        if (points[index % points.Count] is not Node3D marker) return;
        player.GlobalPosition = marker.GlobalPosition;
        player.Rotation = new Vector3(0.0f, marker.GlobalRotation.Y, 0.0f);
    }

    public override void _ExitTree()
    {
        Multiplayer.PeerConnected -= OnPeerConnected;
        Multiplayer.PeerDisconnected -= OnPeerDisconnected;
    }
}
