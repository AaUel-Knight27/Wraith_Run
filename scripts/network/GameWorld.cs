using Godot;
using System;
using System.Collections.Generic;

public partial class GameWorld : Node3D
{
    /// <summary>Marker3D nodes in the map that are placed in this group become spawn points.
    /// Falls back to a small grid if the map has none, so a bare test scene still works.</summary>
    private const string SpawnPointGroup = "spawn_point";

    [Export] public PackedScene PlayerScene { get; set; } = null!;
    [Export] public PackedScene VehicleScene { get; set; } = null!;
    /// <summary>True for Hub.tscn: the walkable warehouse home base. Same spawning and map code as a
    /// match, but no match rules, no bots, no clock and no scoring.</summary>
    [Export] public bool IsHub { get; set; }
    private const string HubMapPath = "res://scenes/maps/DefaultWarehouse.tscn";
    private MultiplayerSpawner _spawner = null!;
    private MultiplayerSpawner _vehicleSpawner = null!;

	private const int MaxBots = 11;
    private const float NavCell = 0.5f;
    private readonly RandomNumberGenerator _botRng = new();
    private bool _mapReady;
    private readonly HashSet<long> _pendingPeerSpawns = new();
    private readonly HashSet<int> _spawnedPeers = new();

    public override void _Ready()
    {
        _spawner = GetNode<MultiplayerSpawner>("MultiplayerSpawner");
        _spawner.SpawnPath = new NodePath("../Players");
        _spawner.SpawnFunction = new Callable(this, nameof(SpawnPlayer));
        _vehicleSpawner = GetNode<MultiplayerSpawner>("VehicleSpawner");
        _vehicleSpawner.SpawnPath = new NodePath("../Vehicles");
        _vehicleSpawner.SpawnFunction = new Callable(this, nameof(SpawnGameVehicle));
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        HubState.Active = IsHub;
        if (IsHub)
        {
            // The hub reads the same spawner but spawns people itself (see HubController) so that a
            // peer is only spawned once its own copy of the hub exists.
            AddChild(new HubController { Name = "HubController" });
        }
        else
        {
            MatchManager.Instance?.EnsureSessionActive();
            CareerTracker.Instance?.BeginMatch();
        }
        InitializeMatchWorldAsync();
    }

    private void LoadModeMap()
    {
        MatchManager? match = MatchManager.Instance;
        GameModeBase rules = match?.ModeRules ?? GameModeBase.For(MatchManager.Mode.Default);
        string mapScenePath = IsHub ? HubMapPath : match?.MapScenePath ?? "res://scenes/maps/Warzone.tscn";
        var packed = ResourceLoader.Load<PackedScene>(mapScenePath);
        if (packed == null)
        {
            GD.PushError($"Could not load {match?.MapDisplayName ?? "Warzone"} for {rules.DisplayName}: {mapScenePath}");
            return;
        }
        var map = packed.Instantiate<Node3D>();
        map.Name = "Map";
        AddChild(map);
        BuildMapCollisions(map, IsHub ? MatchManager.ArenaMap.Warehouse : match?.SelectedMap ?? MatchManager.ArenaMap.Warehouse);
    }

    /// <summary>Imported GLBs are visual meshes without physics. Add inexpensive box proxies to
    /// their mesh bounds and a broad walkable floor so the high-detail art stays usable on mobile.</summary>
    private static void BuildMapCollisions(Node3D map, MatchManager.ArenaMap mapKind)
    {
        // The warehouse GLB is several million triangles and its four imported mesh bounds span
        // whole wall sections. A box around each complete mesh blocks the centre of the room, so
        // the free-roam home uses a clear deck and perimeter walls instead of those art bounds.
        if (mapKind != MatchManager.ArenaMap.Warehouse)
        {
            foreach (Node node in map.FindChildren("*", "MeshInstance3D", true, false))
            {
                var mesh = node as MeshInstance3D;
                if (mesh?.Mesh == null || mesh.Mesh.GetSurfaceCount() == 0) continue;
                var body = new StaticBody3D { Name = "MapCollision" };
                var bounds = mesh.Mesh.GetAabb();
                Vector3 size = bounds.Size;
                size = new Vector3(Mathf.Max(size.X, 0.025f), Mathf.Max(size.Y, 0.025f), Mathf.Max(size.Z, 0.025f));
                var shape = new CollisionShape3D
                {
                    Shape = new BoxShape3D { Size = size },
                    Position = bounds.Position + bounds.Size * 0.5f,
                };
                body.AddChild(shape);
                mesh.AddChild(body);
            }
        }

        bool warehouse = mapKind == MatchManager.ArenaMap.Warehouse;
        // The supplied art is a compact GLB shell; this collision deck gives the interior a
        // continuous floor and catches players at the edge without asking mobile GPUs/CPUs to
        // cook millions of source triangles into a trimesh collider.
        var floor = new StaticBody3D { Name = "WalkableDeck" };
        var floorShape = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(warehouse ? 18.0f : 80.0f, 0.5f, warehouse ? 18.0f : 80.0f) },
            Position = new Vector3(0.0f, -0.3f, 0.0f),
        };
        floor.AddChild(floorShape);
        map.AddChild(floor);

        if (warehouse)
        {
            // The room art is scaled to a roughly 16 m footprint. These four boundaries keep the
            // player in the warehouse while leaving the entire interior unobstructed for roaming.
            AddRoomWall(map, new Vector3(0.0f, 2.5f, -8.6f), new Vector3(18.0f, 5.0f, 0.4f));
            AddRoomWall(map, new Vector3(0.0f, 2.5f, 8.6f), new Vector3(18.0f, 5.0f, 0.4f));
            AddRoomWall(map, new Vector3(-8.6f, 2.5f, 0.0f), new Vector3(0.4f, 5.0f, 18.0f));
            AddRoomWall(map, new Vector3(8.6f, 2.5f, 0.0f), new Vector3(0.4f, 5.0f, 18.0f));
        }
    }

    private static void AddRoomWall(Node3D parent, Vector3 position, Vector3 size)
    {
        var wall = new StaticBody3D { Name = "WarehouseBoundary" };
        wall.Position = position;
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        parent.AddChild(wall);
    }

    private async void InitializeMatchWorldAsync()
    {
        MatchManager? match = MatchManager.Instance;
        if (Multiplayer.HasMultiplayerPeer() && !Multiplayer.IsServer() && match != null)
        {
            // A joining player can enter GameWorld before the host's lobby config RPC arrives.
            // Hold map loading and spawn initialization until the chosen map and mode are known.
            float waitSeconds = 0.0f;
            while (!match.HostConfigReceived && waitSeconds < 5.0f && IsInsideTree())
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                waitSeconds += (float)GetProcessDeltaTime();
            }
            if (!match.HostConfigReceived)
                GD.PushWarning("Host match settings did not arrive before map loading; using the local fallback selection.");
        }
        LoadModeMap();
        // Map choice is applied at runtime; wait for its static collision bodies to register before
        // doing ground/obstruction checks for the initial spawn points.
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (!IsInsideTree()) return;
        _mapReady = true;
        if (IsHub)
        {
            // The hub spawns people through HubController once every connected peer has its own
            // copy of the hub up (see there); no bots, no scoring.
            LoadingScreen.Instance?.Hide();
            GetNodeOrNull<HubController>("HubController")?.OnWorldReady();
            return;
        }
        bool wantBots = false;
        if (Multiplayer.IsServer())
        {
            // Lobby peers connected before GameWorld existed, so GameWorld never saw their
            // PeerConnected signals. Spawn the current roster explicitly on the first world load;
            // later arrivals are handled by OnPeerConnected. Clear the queued ids first because
            // the current peer snapshot below includes peers that joined during map loading.
            _pendingPeerSpawns.Clear();
            MatchManager.Instance.AssignTeamAndBroadcast(1);
            SpawnForPeer(1);
            foreach (long peerId in Multiplayer.GetPeers())
                SpawnForPeer((int)peerId);
            SpawnMatchVehicles();
            wantBots = MatchManager.Instance.BotCount > 0;
        }
        // With bots, keep the loading screen up until the navigation grid is built and they exist.
        if (wantBots) StartBotsAsync();
        else LoadingScreen.Instance?.Hide();
    }

    /// <summary>Playable vehicle roster: the buggy belongs to the two-versus-two mode; the reused
    /// Land Cruiser serves the other driving modes. Cursed Deathmatch remains a foot-only mode.</summary>
    private void SpawnMatchVehicles()
    {
        var match = MatchManager.Instance;
        if (match == null || match.GameMode == MatchManager.Mode.CursedDeathmatch) return;
        bool buggyMode = match.GameMode == MatchManager.Mode.DuoBuggy;
        string model = buggyMode ? GameVehicle.BuggyPath : GameVehicle.LandCruiserPath;
        string display = buggyMode ? "Raider Buggy" : "STV · Land Cruiser";
        float length = buggyMode ? 4.2f : 4.9f;
        int count = buggyMode ? 2 : (match.GameMode == MatchManager.Mode.ConvoyAssault ? 2 : 1);
        for (int i = 0; i < count; i++)
        {
            float side = i == 0 ? -1.0f : 1.0f;
            var data = new Godot.Collections.Dictionary
            {
                ["model"] = model,
                ["display"] = display,
                ["length"] = length,
                ["pos"] = new Vector3(side * (buggyMode ? 5.0f : 7.0f), 0.1f, 3.5f),
                ["yaw"] = Mathf.Atan2(side * (buggyMode ? 5.0f : 7.0f), 3.5f),
                ["slot"] = i,
            };
            if (_vehicleSpawner.Spawn(data) == null)
                GD.PushError($"Failed to spawn {display}.");
        }
    }

    private Node SpawnGameVehicle(Variant data)
    {
        var d = data.AsGodotDictionary();
        var vehicle = VehicleScene.Instantiate<GameVehicle>();
        vehicle.Name = $"Vehicle_{d["slot"].AsInt32()}";
        vehicle.ModelPath = d["model"].AsString();
        vehicle.DisplayName = d["display"].AsString();
        vehicle.TargetLength = d["length"].AsSingle();
        vehicle.Position = d["pos"].AsVector3();
        vehicle.Rotation = new Vector3(0.0f, d["yaw"].AsSingle(), 0.0f);
        vehicle.SetMultiplayerAuthority(1);
        return vehicle;
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
        if (!Multiplayer.IsServer()) return;
        if (IsHub) return; // HubController spawns a peer when that peer reports its hub is ready
        if (_mapReady) SpawnForPeer((int)peerId);
        else _pendingPeerSpawns.Add(peerId);
    }
    private void OnPeerDisconnected(long peerId)
    {
        _pendingPeerSpawns.Remove(peerId);
        _spawnedPeers.Remove((int)peerId);
        var player = GetNodeOrNull<Node>($"Players/Player_{peerId}");
        player?.QueueFree();
    }

    /// <summary>
    /// Picks this peer's spawn spot once, here on the server, and sends the result - not just the
    /// peer id - through the spawner. Unlike the old fixed-marker system (cycle to the next of 8
    /// hand-placed points), the v1.1 random spawn is not something every peer could reproduce
    /// identically on its own: SpawnFunction below runs independently on every connected machine, and
    /// each one has its own random number generator, so leaving the pick for SpawnFunction to make
    /// locally would show a newly-joined player in a different spot on every screen until the network
    /// sync corrected it a moment later. Deciding once, authoritatively, and broadcasting the answer -
    /// the same pattern SpawnBots below already uses for a bot's random start position - avoids that
    /// pop entirely.
    /// </summary>
    /// <summary>Hub only: spawn this person's player (server). Safe to call twice.</summary>
    public void SpawnHubPlayer(int peerId) => SpawnForPeer(peerId);

    /// <summary>Hub only: a client that just connected to someone else's hub drops the player it
    /// spawned for itself while it was alone; the host spawns its real one.</summary>
    public void RemoveSoloHubPlayer()
    {
        var solo = GetNodeOrNull<Node>("Players/Player_1");
        if (solo != null) { solo.Name = "Player_1_discarded"; solo.QueueFree(); }
        _spawnedPeers.Remove(1);
    }

    /// <summary>Hub only: where somebody stands. Team modes put each side in its own copy of the room,
    /// so both teams use the same spots; the slot is the person's order within their own team.</summary>
    private (Vector3 Position, float Yaw) ComputeHubSpawn(int peerId)
    {
        MatchManager? match = MatchManager.Instance;
        int myTeam = HubController.SplitRooms(match) ? match!.GetTeam(peerId) : -1;
        int slot = 0;
        if (Multiplayer.HasMultiplayerPeer())
        {
            var ids = new List<int> { 1 };
            foreach (int id in Multiplayer.GetPeers()) ids.Add(id);
            ids.Sort();
            foreach (int id in ids)
            {
                if (id >= peerId) break;
                if (myTeam < 0 || (match != null && match.GetTeam(id) == myTeam)) slot++;
            }
        }
        return (HubSpawnPoint(slot), 0.0f);
    }

    /// <summary>Spawn grid in the middle of the room, 3 wide, clear of the props along the walls.</summary>
    public static Vector3 HubSpawnPoint(int slot)
    {
        float x = (slot % 3 - 1) * 1.2f;
        float z = ((slot / 3) % 4) * 1.2f - 0.6f;
        return new Vector3(x, 0.08f, z);
    }

    private void SpawnForPeer(int peerId)
    {
        if (!_spawnedPeers.Add(peerId)) return;
        (Vector3 pos, float yaw) = ComputeHumanSpawnTransform(peerId);
        var data = new Godot.Collections.Dictionary
        {
            ["peer"] = peerId,
            ["pos"] = pos,
            ["yaw"] = yaw,
        };
        if (_spawner.Spawn(data) == null)
        {
            _spawnedPeers.Remove(peerId);
            GD.PushError($"Failed to spawn player for peer {peerId}.");
        }
    }

    /// <summary>
    /// Team Deathmatch sends peerId's player to their own TeamSpawnArea; Free-For-All (and any map
    /// with no team areas configured) sends them to a fully random point anywhere in the arena - the
    /// same v1.1 policy Health.Respawn uses for a mid-match respawn, so a player's first spawn and
    /// every later respawn behave the same way. See RespawnPointSelector for the
    /// ground/obstruction/other-player validation every candidate gets.
    ///
    /// Server-only, called from SpawnForPeer before the player's own node exists anywhere at all, so
    /// there is nothing yet to exclude from the physics queries.
    /// </summary>
    private (Vector3 Position, float Yaw) ComputeHumanSpawnTransform(int peerId)
    {
        if (IsHub) return ComputeHubSpawn(peerId);
        MatchManager? match = MatchManager.Instance;
        if (match?.SelectedMap is MatchManager.ArenaMap.Warehouse or MatchManager.ArenaMap.DefaultMap)
        {
            int slot = Math.Max(0, peerId - 1);
            float x = (slot % 3 - 1) * 1.1f;
            float z = ((slot / 3) % 3 - 1) * 1.1f;
            return (new Vector3(x, 0.08f, z), 0.0f);
        }
        if (match != null && match.IsTeamMode)
        {
            int team = match.GetTeam(peerId);
            if (team >= 0 && TeamSpawnArea.TryGet(this, team, out Vector3 center, out Vector2 halfExtent))
            {
                Vector3 areaPos = RespawnPointSelector.SelectInArea(this, center, halfExtent);
                return (areaPos, FacingTowardCentre(areaPos));
            }
            GD.PushWarning($"No TeamSpawnArea configured for team {team}; falling back to a map-wide random spawn.");
        }

        Vector3 pos = RespawnPointSelector.SelectAnywhere(this, RespawnPointSelector.DefaultMapHalfExtent);
        return (pos, FacingTowardCentre(pos));
    }

    /// <summary>A random spot has no "facing" of its own the way a hand-placed marker did, so point the
    /// player roughly at the middle of the arena instead of leaving whatever rotation the scene
    /// defaults to - the same "roughly toward the centre" convention SpawnBots already uses below.</summary>
    private static float FacingTowardCentre(Vector3 position) => Mathf.Atan2(position.X, position.Z);

    private Node SpawnPlayer(Variant data)
    {
        // Both kinds of player now arrive as a dictionary computed on the server: a bot carries its
        // identity/name/level/start pose (see SpawnBots), a person carries just their peer id plus the
        // spawn spot ComputeHumanSpawnTransform already picked for them in SpawnForPeer above.
        var d = data.AsGodotDictionary();
        if (d.ContainsKey("id")) return SpawnBot(d);

        int peerId = (int)d["peer"].AsInt64();
        var player = PlayerScene.Instantiate<CharacterBody3D>();
        player.Name = $"Player_{peerId}";
        player.SetMultiplayerAuthority(peerId);
        player.Position = d["pos"].AsVector3();
        player.Rotation = new Vector3(0.0f, d["yaw"].AsSingle(), 0.0f);
        EnsureTeamIndicator(player);
        // Hub: nobody is told about this player until HubController says who may see them (the same
        // team, or everyone in a solo mode). A peer that is not told never even spawns the node.
        if (IsHub && Multiplayer.IsServer())
        {
            var sync = player.GetNodeOrNull<MultiplayerSynchronizer>("MultiplayerSynchronizer");
            if (sync != null) sync.PublicVisibility = false;
        }
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
    public override void _ExitTree()
    {
        Multiplayer.PeerConnected -= OnPeerConnected;
        Multiplayer.PeerDisconnected -= OnPeerDisconnected;
        BotNav.Clear();
        BotRoster.Clear();
        BotSenses.Reset();
    }
}
