using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The Training Center. Root of scenes/training/TrainingCenter.tscn.
///
/// Owns the trainee (always plain Manny - see MannyOnly), the placeholder arena, the drill lifecycle, the
/// helpers drills use to spawn things, and the plumbing that turns weapon events and hit reports into TrainingStats.
///
/// MAP: the arena is placeholder geometry. To use your own map, add it to TrainingCenter.tscn, drop TrainingAnchor
/// nodes (ids in TrainingAnchors) where each kind of drill should happen, TrainingZone nodes for the map-reading
/// drill, and untick BuildPlaceholderArena. Drills spawn their own props at the anchors and remove them afterwards.
/// </summary>
public partial class TrainingSession : Node3D
{
    public static TrainingSession? Instance { get; private set; }

    [Export] public bool BuildPlaceholderArena { get; set; } = true;
    /// <summary>Reserve ammo is refilled continuously so drills are about technique, not ammo (reloads still take time).</summary>
    [Export] public bool InfiniteReserveAmmo { get; set; } = true;

    public CharacterBody3D Player { get; private set; } = null!;
    public Health PlayerHealth { get; private set; } = null!;
    public PlayerMovement Movement { get; private set; } = null!;
    public WeaponSwitcher Weapons { get; private set; } = null!;

    public TrainingKit Kit { get; private set; } = new();
    public CalloutSystem Callouts { get; private set; } = null!;
    public TrainingStats Stats { get; } = new();
    /// <summary>Extra points a drill wants on the [M] map (reported contacts, objectives). Cleared between drills.</summary>
    public readonly List<MapMarker> MapMarkers = new();
    public Node3D Props { get; private set; } = null!;

    public TrainingDrill? ActiveDrill { get; private set; }
    public DrillDefinition? ActiveDefinition { get; private set; }
    public bool DrillLive => ActiveDrill != null && ActiveDrill.IsLive && ActiveDrill.Outcome == DrillOutcome.Running;
    public bool TraineeDowned { get; private set; }

    // HUD-facing state written by mechanics / cars, read by TrainingHud every frame.
    public string PromptText { get; set; } = string.Empty;
    public string ChannelLabel { get; set; } = string.Empty;
    public float ChannelProgress { get; set; } = -1.0f;
    public string VehicleStatus { get; set; } = string.Empty;
    public float CountdownRemaining => _countdownActive ? _countdown : -1.0f;

    public TrainingHud Hud { get; private set; } = null!;
    public TrainingMenu Menu { get; private set; } = null!;

    /// <summary>A target/actor the trainee killed (bot fire never raises this).</summary>
    public event Action<Node3D, ShotInfo>? TargetKilled;
    /// <summary>target, shot, killed, wasFriendly.</summary>
    public event Action<Node3D, ShotInfo, bool, bool>? HitRegistered;

    private float _countdown;
    private bool _countdownActive;
    private Transform3D _lastPlacement = Transform3D.Identity;
    private bool _wasFirePressed;

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Pausable;

        TrainingInput.EnsureRegistered();
        EnsureOfflineSession();

        Callouts = new CalloutSystem { Name = "Callouts" };
        AddChild(Callouts);
        Props = new Node3D { Name = "Props" };
        AddChild(Props);

        if (BuildPlaceholderArena) BuildArena();
        SpawnTrainee();

        Hud = new TrainingHud { Name = "TrainingHud" };
        AddChild(Hud);
        Menu = new TrainingMenu { Name = "TrainingMenu" };
        AddChild(Menu);

        WireWeaponEvents();
        LoadingScreen.Instance?.Hide();
        ReturnToHub(true);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (GetTree() != null) GetTree().Paused = false;
    }

    // ---------------------------------------------------------------------------------------------
    // Set-up
    // ---------------------------------------------------------------------------------------------
    private void EnsureOfflineSession()
    {
        // Leave any LAN host/client state behind, then make sure there is a connected offline peer, so authority,
        // unique id 1 and CallLocal RPCs (kill feed, weapon effects) behave exactly as in single-instance dev play.
        GetNodeOrNull<LanSession>("/root/LanSession")?.Stop();
        if (Multiplayer.MultiplayerPeer is not OfflineMultiplayerPeer)
            Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();

        MatchManager? match = GetNodeOrNull<MatchManager>("/root/MatchManager");
        if (match != null)
        {
            match.GameMode = MatchManager.Mode.FreeForAll;
            match.FriendlyFire = false;
        }
    }

    private void SpawnTrainee()
    {
        var scene = ResourceLoader.Load<PackedScene>(MannyOnly.PlayerScenePath);
        if (scene == null)
        {
            GD.PushError("Training Center: could not load " + MannyOnly.PlayerScenePath);
            return;
        }

        var player = scene.Instantiate<CharacterBody3D>();
        MannyOnly.PrepareTrainee(player);
        player.Name = "TraineeManny";
        AddChild(player);

        Player = player;
        PlayerHealth = player.GetNode<Health>("Health");
        Movement = (PlayerMovement)player;
        Weapons = player.GetNode<WeaponSwitcher>("WeaponSwitcher");
        MannyOnly.VerifyTrainee(player);
    }

    private void WireWeaponEvents()
    {
        Weapons.ShotFired += (weapon, pellets) =>
        {
            if (!DrillLive) return;
            Stats.Shots++;
            Stats.Pellets += pellets;
        };
        Weapons.ReloadStarted += weapon =>
        {
            if (!DrillLive) return;
            Stats.Reloads++;
            if (AnyEnemyEngagingTrainee()) Stats.BadReloads++;
        };
    }

    private bool AnyEnemyEngagingTrainee()
    {
        foreach (Node node in GetTree().GetNodesInGroup("training_actor"))
            if (node is TrainingActor actor && actor.Side == TrainingActor.FactionType.Enemy && actor.IsEngaging) return true;
        return false;
    }

    private void BuildArena()
    {
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
        };
        AddChild(new WorldEnvironment { Name = "PlaceholderEnvironment", Environment = environment });
        AddChild(new DirectionalLight3D { Name = "PlaceholderSun", RotationDegrees = new Vector3(-55.0f, 35.0f, 0.0f), LightEnergy = 1.1f, ShadowEnabled = false });

        Node3D arena = new Node3D { Name = "PlaceholderArena" };
        AddChild(arena);
        arena.AddChild(TrainingUtil.SolidBox(new Vector3(520.0f, 1.0f, 420.0f), new Color(0.32f, 0.34f, 0.36f), new Vector3(0, -0.5f, 0), "Floor"));

        // Grid lines so speed and distance are readable on an otherwise bare floor.
        for (int x = -260; x <= 260; x += 20)
            arena.AddChild(TrainingUtil.Box(new Vector3(0.12f, 0.02f, 420.0f), new Color(0.2f, 0.21f, 0.22f), new Vector3(x, 0.01f, 0), "GridX"));
        for (int z = -210; z <= 210; z += 20)
            arena.AddChild(TrainingUtil.Box(new Vector3(520.0f, 0.02f, 0.12f), new Color(0.2f, 0.21f, 0.22f), new Vector3(0, 0.01f, z), "GridZ"));

        foreach (string id in TrainingAnchors.Ids)
        {
            Vector3 at = TrainingAnchors.DefaultPositions[id];
            arena.AddChild(TrainingUtil.GroundDisc(id == TrainingAnchors.Hub ? 9.0f : 6.0f, new Color(0.9f, 0.25f, 0.2f), at + new Vector3(0, 0.03f, 0), 0.3f));
            arena.AddChild(TrainingUtil.Label(id.Replace('_', ' ').ToUpperInvariant(), at + new Vector3(0, 4.0f, 0), new Color(1.0f, 0.8f, 0.7f), 64, false));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Per-frame
    // ---------------------------------------------------------------------------------------------
    public override void _Process(double delta)
    {
        if (!IsInstanceValid(Player)) return;
        float dt = (float)delta;

        if (InfiniteReserveAmmo) Weapons.TopUpReserve();
        if (TraineeDowned && PlayerHealth.CurrentHealth > 1.5f) TraineeDowned = false;
        if (Player.GlobalPosition.Y < -15.0f) PlacePlayerAt(_lastPlacement);

        if (_countdownActive)
        {
            _countdown -= dt;
            if (_countdown <= 0.0f)
            {
                _countdownActive = false;
                ActiveDrill?.GoLive();
                Toast("GO!", new Color(0.6f, 1.0f, 0.6f), 1.0f);
            }
        }

        // Dry-fire: pulled the trigger on an empty magazine.
        bool firePressed = Input.IsActionPressed("fire");
        if (DrillLive && firePressed && !_wasFirePressed && Weapons.CurrentAmmo == 0 && !Weapons.IsReloading && Weapons.MagazineSize > 0)
            Stats.DryFires++;
        _wasFirePressed = firePressed;

        if (Input.IsActionJustPressed(TrainingInput.TrainingMenu)) Menu.ShowPause();
        else if (Input.IsActionJustPressed(TrainingInput.CalloutMenu)) Menu.ShowCalloutPicker();
        else if (Input.IsActionJustPressed(TrainingInput.RestartDrill) && ActiveDefinition != null) StartDrill(ActiveDefinition);
        else if (Input.IsActionJustPressed(TrainingInput.MapToggle)) Hud.ToggleMap();
        else if (Input.IsActionJustPressed(TrainingInput.CalloutQuick) && ActiveDrill != null)
            PlayerCallout(ActiveDrill.ContextCallout ?? CalloutType.EnemySpotted);
    }

    // ---------------------------------------------------------------------------------------------
    // Drill lifecycle
    // ---------------------------------------------------------------------------------------------
    public void StartDrill(DrillDefinition definition)
    {
        EndActiveDrill();
        ClearProps();
        Stats.Reset();
        Kit = new TrainingKit();
        Callouts.Recent.Clear();
        ResetHudState();
        ResetTrainee();

        ActiveDefinition = definition;
        TrainingDrill drill = definition.Create();
        ActiveDrill = drill;
        AddChild(drill);
        drill.Begin(definition);

        _countdown = 3.0f;
        _countdownActive = true;
        Hud.Visible = true;
        Menu.CloseAll();
        Toast(definition.Title.ToUpperInvariant(), new Color(1.0f, 0.85f, 0.6f), 2.5f);
    }

    public void ReturnToHub(bool openMenu)
    {
        EndActiveDrill();
        ClearProps();
        ActiveDefinition = null;
        Stats.Reset();
        Kit = new TrainingKit();
        ResetHudState();
        ResetTrainee();
        _countdownActive = false;
        PlacePlayer(Anchor(TrainingAnchors.Hub));
        Hud.Visible = true;
        if (openMenu) Menu.ShowHub();
    }

    private void EndActiveDrill()
    {
        if (ActiveDrill == null) return;
        TrainingDrill drill = ActiveDrill;
        ActiveDrill = null;
        drill.Abort();
        if (drill.GetParent() == this) RemoveChild(drill);
        drill.QueueFree();
    }

    private void ClearProps()
    {
        foreach (Node child in Props.GetChildren())
        {
            Props.RemoveChild(child);   // leave groups immediately so a new drill never sees the old drill's actors
            child.QueueFree();
        }
    }

    private void ResetHudState()
    {
        MapMarkers.Clear();
        PromptText = string.Empty;
        ChannelLabel = string.Empty;
        ChannelProgress = -1.0f;
        VehicleStatus = string.Empty;
    }

    private void ResetTrainee()
    {
        if (!IsInstanceValid(Player)) return;
        SetTraineeSeated(false, true);
        TrainingDamage.FullHealTrainee(PlayerHealth);
        Weapons.RefillAmmo();
        TraineeDowned = false;
    }

    /// <summary>Called by a drill when it finishes: records progress, then shows the results screen after a beat.</summary>
    public void OnDrillFinished(TrainingDrill drill)
    {
        if (drill.Definition == null) return;
        TrainingProgress.RecordRun(drill.Definition.Id, drill.Rating, drill.Elapsed, drill.Outcome == DrillOutcome.Succeeded, drill.Definition.Skills);
        bool ok = drill.Outcome == DrillOutcome.Succeeded;
        Toast(ok ? "DRILL COMPLETE" : "DRILL FAILED", ok ? new Color(0.6f, 1.0f, 0.6f) : new Color(1.0f, 0.5f, 0.4f), 1.6f);
        GetTree().CreateTimer(1.6).Timeout += () =>
        {
            if (IsInstanceValid(this) && ActiveDrill == drill) Menu.ShowResults(drill);
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Trainee helpers
    // ---------------------------------------------------------------------------------------------
    public Transform3D Anchor(string anchorId) => TrainingAnchors.Get(GetTree(), anchorId);

    /// <summary>Teleports the trainee to anchor * localOffset, facing the anchor's -Z rotated by yawDegrees.</summary>
    public void PlacePlayer(Transform3D anchor, Vector3 localOffset = default, float yawDegrees = 0.0f)
    {
        Vector3 position = anchor * localOffset;
        float yaw = anchor.Basis.GetEuler().Y + Mathf.DegToRad(yawDegrees);
        PlacePlayerAt(new Transform3D(new Basis(Vector3.Up, yaw), position));
    }

    private void PlacePlayerAt(Transform3D placement)
    {
        if (!IsInstanceValid(Player)) return;
        _lastPlacement = placement;
        Player.GlobalPosition = placement.Origin + new Vector3(0, 0.15f, 0);
        Player.Velocity = Vector3.Zero;
        Player.GlobalRotation = new Vector3(0, placement.Basis.GetEuler().Y, 0);
    }

    /// <summary>Seated in a vehicle: hide the body, and (as driver) stop weapon input.</summary>
    public void SetTraineeSeated(bool seated, bool weaponsAllowed)
    {
        if (!IsInstanceValid(Player)) return;
        Weapons.ProcessMode = (seated && !weaponsAllowed) ? Node.ProcessModeEnum.Disabled : Node.ProcessModeEnum.Inherit;
        Node3D? armature = Player.GetNodeOrNull<Node3D>("Armature");
        if (armature != null) armature.Visible = !seated;
    }

    /// <summary>Damage reached the trainee through TrainingDamage.</summary>
    public void NotifyTraineeDamaged(float amount, bool downed)
    {
        if (DrillLive) Stats.DamageTaken += amount;
        if (!downed || TraineeDowned) return;
        TraineeDowned = true;
        if (DrillLive) ActiveDrill?.OnTraineeDowned();
    }

    /// <summary>A weapon hit landed on a shootable. Bot-vs-bot and bot-vs-trainee fire (AttackerPeerId &lt; 0) is ignored here.</summary>
    public void RegisterHit(Node3D target, ShotInfo shot, bool killed, bool wasFriendly, float dealt)
    {
        if (shot.AttackerPeerId < 0 || !DrillLive) return;
        Stats.Hits++;
        if (shot.IsHeadshot) Stats.Headshots++;
        Stats.DamageDealt += dealt;
        Stats.HitPoints.Add(shot.HitPoint);
        if (wasFriendly)
        {
            Stats.FriendlyHits++;
            Toast("Friendly hit!", new Color(1.0f, 0.5f, 0.4f), 1.2f);
        }
        else if (killed)
        {
            Stats.Kills++;
            TargetKilled?.Invoke(target, shot);
        }
        HitRegistered?.Invoke(target, shot, killed, wasFriendly);
    }

    // ---------------------------------------------------------------------------------------------
    // Comms / aiming helpers
    // ---------------------------------------------------------------------------------------------
    public void PlayerCallout(CalloutType type)
    {
        Vector3 position = Player.GlobalPosition;
        if (AimRay(80.0f, out Vector3 hit, out _, out _)) position = hit;
        Callouts.Send(type, "You", true, position);
    }

    /// <summary>Ray from the trainee's camera along the crosshair. Excludes the trainee's own body.</summary>
    public bool AimRay(float maxDistance, out Vector3 point, out Node? collider, out Vector3 normal)
    {
        point = Vector3.Zero;
        collider = null;
        normal = Vector3.Up;

        Camera3D? camera = GetViewport().GetCamera3D();
        if (camera == null || !IsInstanceValid(Player)) return false;

        Vector3 from = camera.GlobalPosition;
        Vector3 to = from - camera.GlobalTransform.Basis.Z * maxDistance;
        var query = PhysicsRayQueryParameters3D.Create(from, to, uint.MaxValue, new Godot.Collections.Array<Rid> { Player.GetRid() });
        Godot.Collections.Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (result.Count == 0) return false;

        point = (Vector3)result["position"];
        normal = (Vector3)result["normal"];
        collider = result["collider"].As<Node>();
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Spawning (all Manny, all under Props so they vanish when the drill ends)
    // ---------------------------------------------------------------------------------------------
    public TrainingActor SpawnActor(TrainingActor.FactionType side, Vector3 position, string name, Action<TrainingActor>? configure = null)
    {
        var actor = new TrainingActor { Side = side, ActorName = name, Anchor = position };
        configure?.Invoke(actor);   // before entering the tree, so _Ready sees the final configuration
        Props.AddChild(actor);
        actor.GlobalPosition = position;
        return actor;
    }

    public TrainingTarget SpawnTarget(Vector3 position, Action<TrainingTarget>? configure = null)
    {
        var target = new TrainingTarget { Anchor = position };
        configure?.Invoke(target);
        Props.AddChild(target);
        target.GlobalPosition = position;
        return target;
    }

    public TrainingCar SpawnCar(Vector3 position, float yawDegrees, Action<TrainingCar>? configure = null)
    {
        var car = new TrainingCar();
        configure?.Invoke(car);
        Props.AddChild(car);
        car.GlobalPosition = position + new Vector3(0, 0.1f, 0);
        car.GlobalRotation = new Vector3(0, Mathf.DegToRad(yawDegrees), 0);
        return car;
    }

    public T AddMechanic<T>() where T : TrainingMechanic, new()
    {
        var mechanic = new T();
        Props.AddChild(mechanic);
        return mechanic;
    }

    public List<TrainingActor> Actors(TrainingActor.FactionType? side = null, bool aliveOnly = true)
    {
        var list = new List<TrainingActor>();
        foreach (Node node in GetTree().GetNodesInGroup("training_actor"))
            if (node is TrainingActor actor && (!aliveOnly || !actor.IsDead) && (side == null || actor.Side == side.Value)) list.Add(actor);
        return list;
    }

    public void Toast(string text, Color? color = null, float seconds = 2.5f)
    {
        if (IsInstanceValid(Hud)) Hud.Toast(text, color ?? Colors.White, seconds);
    }

    public void SetPausedForMenu(bool paused)
    {
        GetTree().Paused = paused;
        bool touch = SettingsManager.Instance != null && SettingsManager.Instance.ShouldShowTouchControls();
        if (touch) return;
        Input.MouseMode = paused ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }
}
