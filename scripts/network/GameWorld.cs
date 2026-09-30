using Godot;
using System;
using System.Collections.Generic;

public partial class GameWorld : Node3D
{
    /// <summary>Marker3D nodes in the map that are placed in this group become spawn points.
    /// Falls back to a small grid if the map has none, so a bare test scene still works.</summary>
    private const string SpawnPointGroup = "spawn_point";

    [Export] public PackedScene PlayerScene { get; set; } = null!;
    private MultiplayerSpawner _spawner = null!;
    private int _spawnIndex;

    private const int MaxBots = 10;
    private const float NavCell = 0.5f;
    private readonly RandomNumberGenerator _botRng = new();

    public override void _Ready()
    {
        _spawner = GetNode<MultiplayerSpawner>("MultiplayerSpawner");
        _spawner.SpawnPath = new NodePath("../Players");
        _spawner.SpawnFunction = new Callable(this, nameof(SpawnPlayer));
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        MatchManager.Instance?.EnsureSessionActive();
        bool wantBots = false;
        if (Multiplayer.IsServer())
        {
            // MatchManager's own PeerConnected hook assigns a team to everyone who joins later;
            // peer 1 (the host) never raises that signal for itself, the same reason SpawnForPeer
            // below is also called explicitly here instead of waiting for OnPeerConnected.
            MatchManager.Instance.AssignTeamAndBroadcast(1);
            SpawnForPeer(1);
            wantBots = MatchManager.Instance.BotCount > 0;
        }
        // With bots, keep the loading screen up until the navigation grid is built and they exist.
        if (wantBots) StartBotsAsync();
        else LoadingScreen.Instance?.Hide();
    }

    // -------------------------------------------------------------------------------------------
    // Bots (host only)
    // -------------------------------------------------------------------------------------------

    private async void StartBotsAsync()
    {
        try
        {
            // Let the freshly loaded map's colliders register with the physics server before probing them.
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (!IsInsideTree()) return;
            if (BuildNavigation()) SpawnBots();
        }
        catch (Exception e)
        {
            GD.PushError($"Bot setup failed: {e}");
        }
        finally
        {
            LoadingScreen.Instance?.Hide();
        }
    }

    private bool BuildNavigation()
    {
        // Cover the arena plus a margin; anything past the walls has no floor and is discarded.
        float extent = 30.0f;
        foreach (Node n in GetTree().GetNodesInGroup(SpawnPointGroup))
            if (n is Node3D marker)
                extent = Mathf.Max(extent, Mathf.Max(Mathf.Abs(marker.GlobalPosition.X), Mathf.Abs(marker.GlobalPosition.Z)) + 6.0f);
        extent = Mathf.Min(extent, 80.0f);

        var exclude = new Godot.Collections.Array<Rid>();
        foreach (Node n in GetTree().GetNodesInGroup("players"))
            if (n is CollisionObject3D body) exclude.Add(body.GetRid());

        NavGrid grid = NavGrid.Build(GetWorld3D().DirectSpaceState, exclude, Vector2.Zero, extent, NavCell);
        if (grid.WalkableCount < 100)
        {
            GD.PushError("Bots disabled: the navigation grid found almost no walkable ground on this map.");
            return false;
        }
        BotNav.Grid = grid;
        return true;
    }

    private void SpawnBots()
    {
        MatchManager match = MatchManager.Instance;
        NavGrid grid = BotNav.Grid!;
        _botRng.Randomize();
        int count = Mathf.Clamp(match.BotCount, 0, MaxBots);

        var placed = new List<Vector3>();
        foreach (Node n in GetTree().GetNodesInGroup("players"))
            if (n is Node3D p) placed.Add(p.GlobalPosition);

        var usedNames = new HashSet<string>();
        for (int i = 0; i < count; i++)
        {
            long id = BotRoster.AllocateId();
            while (System.Array.IndexOf(Multiplayer.GetPeers(), (int)id) >= 0) id = BotRoster.AllocateId();

            BotDifficulty level = RollLevel(match.BotLevel);
            string name = PickName(usedNames, i);

            // Random spot, as far as possible from everyone already placed, so bots start scattered.
            Vector3 pos = grid.PickSpawn(placed, 14.0f, _botRng);
            placed.Add(pos);
            float yaw = Mathf.Atan2(pos.X, pos.Z) + _botRng.RandfRange(-0.8f, 0.8f); // roughly toward the centre

            match.AssignTeamAndBroadcast(id);
            var data = new Godot.Collections.Dictionary
            {
                ["id"] = id,
                ["name"] = name,
                ["level"] = (int)level,
                ["pos"] = new Vector3(pos.X, pos.Y + 0.1f, pos.Z),
                ["yaw"] = yaw,
            };
            if (_spawner.Spawn(data) == null) GD.PushError($"Failed to spawn bot {name}.");
        }

        if (match.AdaptiveBots) AddChild(new BotDirector { Name = "BotDirector" });
    }

    private BotDifficulty RollLevel(BotDifficulty chosen)
    {
        if (chosen != BotDifficulty.Mixed) return chosen;
        float r = _botRng.Randf();
        return r < 0.35f ? BotDifficulty.Easy : (r < 0.75f ? BotDifficulty.Normal : BotDifficulty.Advanced);
    }

    private string PickName(HashSet<string> used, int index)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            string candidate = BotRoster.NamePool[_botRng.RandiRange(0, BotRoster.NamePool.Length - 1)];
            if (used.Add(candidate)) return candidate;
        }
        return $"Bot{index + 1}";
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
        // Bots are spawned with a dictionary (identity, name, level, start pose); people with their peer id.
        if (data.VariantType == Variant.Type.Dictionary) return SpawnBot(data.AsGodotDictionary());

        int peerId = (int)data.AsInt64();
        var player = PlayerScene.Instantiate<CharacterBody3D>();
        player.Name = $"Player_{peerId}";
        player.SetMultiplayerAuthority(peerId);
        ApplySpawnTransform(player, _spawnIndex++);
        EnsureTeamIndicator(player);
        return player;
    }

    /// <summary>Runs on every peer with the same data, so names/levels/teams line up everywhere.
    /// Only the host attaches the brain that actually plays; other peers just see a player.</summary>
    private Node SpawnBot(Godot.Collections.Dictionary d)
    {
        long id = d["id"].AsInt64();
        string name = d["name"].AsString();
        int level = d["level"].AsInt32();
        Vector3 pos = d["pos"].AsVector3();
        float yaw = d["yaw"].AsSingle();

        BotRoster.Register(id, name, level);
        var player = PlayerScene.Instantiate<CharacterBody3D>();
        player.Name = $"Bot_{id}";
        player.SetMultiplayerAuthority(1); // the host simulates every bot
        player.SetMeta(PlayerIdentity.BotMeta, id);
        player.Position = pos;
        player.Rotation = new Vector3(0.0f, yaw, 0.0f);
        EnsureTeamIndicator(player);

        if (Multiplayer.IsServer())
        {
            var brain = new BotBrain { Name = "BotBrain" };
            brain.Configure(id, (BotDifficulty)level);
            player.AddChild(brain);
            ((PlayerMovement)player).Bot = brain.Controls;
        }
        return player;
    }

    /// <summary>The name tag that tells allies from enemies in Team Deathmatch (and shows bot names).
    /// Player.tscn in this project no longer contains one, so add it when a player spawns.</summary>
    private static void EnsureTeamIndicator(Node player)
    {
        if (player.GetNodeOrNull("TeamIndicator") != null) return;
        player.AddChild(new TeamIndicator { Name = "TeamIndicator" });
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
        // Local Position, not GlobalPosition: this node is not in the tree yet (the spawner adds it
        // after SpawnPlayer returns), so a global set logs "!is_inside_tree()" every spawn. The
        // Players node sits at the origin with no rotation, so local == global once it is added.
        player.Position = marker.GlobalPosition;
        player.Rotation = new Vector3(0.0f, marker.GlobalRotation.Y, 0.0f);
    }

    public override void _ExitTree()
    {
        Multiplayer.PeerConnected -= OnPeerConnected;
        Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        BotNav.Clear();
        BotRoster.Clear();
        BotSenses.Reset();
    }
}
