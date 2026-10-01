using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The training vehicle. NOT the real vehicle system (that does not exist yet): a small arcade car that is
/// just good enough to practise driving, shooting from a seat, swapping seats and repairing.
///
/// What it models, and where the numbers come from:
///  * Damage - small arms shot at the lower body puncture a tire; any flat tire or 0 HP immobilises it (a
///    repairable "repair window", FR-CA-07/09); a heavy weapon (Bazooka) destroys it outright.
///  * Seats - driver and shooter; the seat swap takes 2 s and is only allowed below a speed limit (FR-CA-04/05).
///    The partner in the other seat is AI: an AI driver follows the route, an AI shooter shoots enemy actors.
///  * Fuel + boost - consumption scales with throttle; boost is faster and thirstier.
///
/// REAL WIRING: the future vehicle class should implement IRepairable / IShootable / IPlantable / IExplosionReceiver
/// (interfaces in Ports.cs and IShootable.cs) and the Fixer/Demolisher mechanics work on it unchanged.
/// The seat/swap rules live in Kit + SwapRule; move them to the real seat system when it lands.
/// </summary>
public partial class TrainingCar : CharacterBody3D, IShootable, IExplosionReceiver, IRepairable, IPlantable
{
    public const string ModelPath = "res://art/vehicles/Low_Poly_Car_-_Toyota_Land_Cruiser_70_GXL_2024.glb";
    private const float TargetLength = 4.9f;
    private const float HalfWidth = 0.95f;
    private const float HalfLength = 2.45f;

    public enum Seat { None, Driver, Shooter }

    public struct DriveInput
    {
        public float Throttle;   // -1..1
        public float Steer;      // -1 (left) .. 1 (right)
        public bool Brake;
        public bool Handbrake;
        public bool Boost;
    }

    // ---- tuning ---------------------------------------------------------------------------------
    [Export] public float MaxSpeed { get; set; } = 22.0f;
    [Export] public float MaxReverseSpeed { get; set; } = 7.0f;
    [Export] public float Acceleration { get; set; } = 8.0f;
    [Export] public float BrakeDecel { get; set; } = 18.0f;
    [Export] public float SteerRate { get; set; } = 1.7f;
    [Export] public float BoostMultiplier { get; set; } = 1.35f;
    [Export] public float FuelCapacity { get; set; } = 100.0f;
    [Export] public float BaseFuelPerSecond { get; set; } = 0.55f;
    [Export] public float MaxHp { get; set; } = 100.0f;
    /// <summary>If the car drives backwards in your scene, flip this - it is a straight 180 degree turn of the model only.</summary>
    [Export] public bool FlipModelFront { get; set; }

    // ---- state ----------------------------------------------------------------------------------
    public DriveInput Drive;
    public float Fuel { get; set; }
    public float Hp { get; private set; }
    public readonly bool[] TireFlat = new bool[4];   // FL FR RL RR
    public bool IsDestroyed { get; private set; }
    /// <summary>Enemy vehicle (shooting it is the goal) vs friendly (shooting it is a mistake).</summary>
    public bool Hostile { get; set; }

    public Seat PlayerSeat { get; private set; } = Seat.None;
    public Seat PreferredSeat { get; set; } = Seat.Driver;
    public bool AiDriverPresent { get; set; }
    public bool AiShooterPresent { get; set; }
    public bool AllowEnterExit { get; set; } = true;
    public bool AllowSwap { get; set; }
    public SeatSwapRule SwapRule { get; set; } = SeatSwapRule.FullStop;
    public bool Swapping => _swapRemaining > 0.0f;

    // Autopilot (AI driver / enemy convoy car)
    public List<Vector3> Route { get; } = new();
    public bool AutopilotEnabled { get; set; }
    public bool LoopRoute { get; set; }
    public float CruiseSpeed { get; set; } = 10.0f;
    public bool Halted { get; set; }
    public bool RouteFinished { get; private set; }

    public event Action<TrainingCar>? DestroyedEvent;
    public event Action<TrainingCar, int>? TirePunctured;
    public event Action<TrainingCar, Seat>? SeatSwapped;
    public event Action<TrainingCar>? RouteCompleted;
    /// <summary>A seat swap was refused because the car was too fast. Args: car, the speed limit that applies.</summary>
    public event Action<TrainingCar, float>? SwapRefused;

    // IRepairable / IPlantable / IExplosionReceiver
    public string DisplayName => "Vehicle";
    public Vector3 WorldPosition => GlobalPosition;
    public float CurrentSpeed => Mathf.Abs(_speed);
    public float SignedSpeed => _speed;
    public int FlatTireCount
    {
        get
        {
            int n = 0;
            foreach (bool flat in TireFlat) if (flat) n++;
            return n;
        }
    }
    public float HpFraction => MaxHp <= 0.0f ? 0.0f : Hp / MaxHp;
    public bool NeedsRepair => FlatTireCount > 0 || Hp < MaxHp * 0.9f;
    public bool IsImmobilized => IsDestroyed || Hp <= 0.0f || FlatTireCount > 0 || Fuel <= 0.0f;
    public Vector3 BlastPosition => GlobalPosition + new Vector3(0, 0.8f, 0);
    public bool IsFleshTarget => false;

    private Node3D _modelPivot = null!;
    private Node3D _driverSeat = null!;
    private Node3D _shooterSeat = null!;
    private Label3D _statusLabel = null!;
    private readonly RandomNumberGenerator _rng = new();

    private float _speed;
    private float _swapRemaining;
    private int _routeIndex;
    private float _prevYaw;
    private float _swayTimer;
    private float _aiShotTimer;
    private bool _promptOwned;
    private bool _seatVisualHidden;

    public override void _Ready()
    {
        _rng.Randomize();
        Hp = MaxHp;
        if (Fuel <= 0.0f) Fuel = FuelCapacity;
        AddToGroup("training_car");
        AddToGroup("blast_receiver");
        ProcessPriority = 10;             // after the player's own physics step, so seat-locking wins
        CollisionLayer = 1 << 4;          // layer 5: on foot you pass through it; bullets (mask all) still hit it
        CollisionMask = 1;                // it still drives on the floor and stops at walls

        AddChild(new CollisionShape3D
        {
            Name = "Hull",
            Shape = new BoxShape3D { Size = new Vector3(HalfWidth * 2.0f, 1.7f, HalfLength * 2.0f) },
            Position = new Vector3(0, 0.85f, 0),
        });

        _modelPivot = new Node3D { Name = "ModelPivot" };
        AddChild(_modelPivot);
        BuildModel();

        _driverSeat = new Node3D { Name = "DriverSeat", Position = new Vector3(-0.42f, 0.1f, -0.2f) };
        _shooterSeat = new Node3D { Name = "ShooterSeat", Position = new Vector3(0.42f, 0.1f, -0.2f) };
        AddChild(_driverSeat);
        AddChild(_shooterSeat);

        _statusLabel = TrainingUtil.Label("", new Vector3(0, 2.7f, 0), Colors.White, 30, true);
        AddChild(_statusLabel);

        _prevYaw = GlobalRotation.Y;
        if (TrainingSession.Instance != null) TrainingSession.Instance.Callouts.Issued += OnCallout;
    }

    public override void _ExitTree()
    {
        if (TrainingSession.Instance != null)
        {
            if (IsInstanceValid(TrainingSession.Instance.Callouts)) TrainingSession.Instance.Callouts.Issued -= OnCallout;
            ReleasePlayer();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Model
    // ---------------------------------------------------------------------------------------------
    private void BuildModel()
    {
        Node3D? model = null;
        var scene = ResourceLoader.Load<PackedScene>(ModelPath);
        if (scene != null) model = scene.Instantiate<Node3D>();

        if (model == null)
        {
            GD.PushWarning($"TrainingCar: {ModelPath} not found - using a plain box. Put the GLB there for the real Land Cruiser.");
            _modelPivot.AddChild(TrainingUtil.Box(new Vector3(HalfWidth * 2.0f, 1.4f, HalfLength * 2.0f), new Color(0.35f, 0.4f, 0.3f), new Vector3(0, 0.9f, 0), "PlaceholderBody"));
            _modelPivot.AddChild(TrainingUtil.Box(new Vector3(1.6f, 0.6f, 0.15f), new Color(0.9f, 0.9f, 0.6f), new Vector3(0, 1.2f, -HalfLength + 0.05f), "PlaceholderFrontMarker"));
            return;
        }

        _modelPivot.AddChild(model);
        FixMaterials(model);
        FitModel(model);
    }

    /// <summary>The GLB's one material is alpha-blended (bad sorting in the Compatibility renderer) and double-sided
    /// (a seated player would see the inside of the shell). Make it opaque and back-face-culled so it looks right
    /// from outside and you can see out from inside.</summary>
    private static void FixMaterials(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            foreach (Node child in node.GetChildren()) stack.Push(child);
            if (node is not MeshInstance3D mesh || mesh.Mesh == null) continue;
            for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
            {
                if (mesh.GetActiveMaterial(i) is not BaseMaterial3D source) continue;
                var copy = (BaseMaterial3D)source.Duplicate();
                copy.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
                copy.CullMode = BaseMaterial3D.CullModeEnum.Back;
                mesh.SetSurfaceOverrideMaterial(i, copy);
            }
        }
    }

    /// <summary>The GLB is authored in odd units (its own scale node makes it ~250 m long) and its front points along +X.
    /// Measure it, scale it to a real 4.9 m, turn its front to -Z (Godot forward) and sit its wheels on y = 0.</summary>
    private void FitModel(Node3D model)
    {
        // Pass 1: size + which end is the front (the hood end is the LOWER end of the silhouette).
        if (!MeasureLocal(model, out Vector3 min, out Vector3 max, out float lowEndMaxY, out float highEndMaxY)) return;
        Vector3 size = max - min;
        bool lengthAlongX = size.X >= size.Z;
        float length = lengthAlongX ? size.X : size.Z;
        if (length < 0.0001f) return;

        // lowEnd = the end at the smaller coordinate on the length axis.
        bool frontAtLowEnd = lowEndMaxY < highEndMaxY;
        float yaw;
        if (lengthAlongX) yaw = frontAtLowEnd ? -90.0f : 90.0f;
        else yaw = frontAtLowEnd ? 0.0f : 180.0f;
        if (FlipModelFront) yaw += 180.0f;

        float scale = TargetLength / length;
        _modelPivot.Scale = new Vector3(scale, scale, scale);
        _modelPivot.RotationDegrees = new Vector3(0, yaw, 0);
        _modelPivot.Position = Vector3.Zero;

        // Pass 2: re-measure after scale/rotation and centre it on the hull, wheels on the ground.
        if (!MeasureLocal(model, out Vector3 min2, out Vector3 max2, out _, out _)) return;
        Vector3 centre = (min2 + max2) * 0.5f;
        _modelPivot.Position = new Vector3(-centre.X, -min2.Y, -centre.Z);
    }

    /// <summary>Bounds of the model in THIS car's local space, plus the tallest point in each outer quarter of the length axis.</summary>
    private bool MeasureLocal(Node3D model, out Vector3 min, out Vector3 max, out float lowEndMaxY, out float highEndMaxY)
    {
        min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        lowEndMaxY = 0.0f;
        highEndMaxY = 0.0f;

        Transform3D toLocal = GlobalTransform.AffineInverse();
        var points = new List<Vector3>();
        var stack = new Stack<Node>();
        stack.Push(model);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            foreach (Node child in node.GetChildren()) stack.Push(child);
            if (node is not MeshInstance3D mesh || mesh.Mesh == null) continue;
            Transform3D toCar = toLocal * mesh.GlobalTransform;
            for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                Godot.Collections.Array arrays = mesh.Mesh.SurfaceGetArrays(surface);
                if (arrays.Count <= (int)Mesh.ArrayType.Vertex) continue;
                Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                foreach (Vector3 v in vertices) points.Add(toCar * v);
            }
        }
        if (points.Count == 0) return false;

        foreach (Vector3 p in points)
        {
            min = new Vector3(Mathf.Min(min.X, p.X), Mathf.Min(min.Y, p.Y), Mathf.Min(min.Z, p.Z));
            max = new Vector3(Mathf.Max(max.X, p.X), Mathf.Max(max.Y, p.Y), Mathf.Max(max.Z, p.Z));
        }

        bool alongX = (max.X - min.X) >= (max.Z - min.Z);
        float lo = alongX ? min.X : min.Z;
        float hi = alongX ? max.X : max.Z;
        float quarter = (hi - lo) * 0.25f;
        lowEndMaxY = float.MinValue;
        highEndMaxY = float.MinValue;
        foreach (Vector3 p in points)
        {
            float axis = alongX ? p.X : p.Z;
            if (axis <= lo + quarter) lowEndMaxY = Mathf.Max(lowEndMaxY, p.Y);
            if (axis >= hi - quarter) highEndMaxY = Mathf.Max(highEndMaxY, p.Y);
        }
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // Damage
    // ---------------------------------------------------------------------------------------------
    public bool IsHeadHit(Vector3 worldPoint) => false;

    public void ReceiveShot(ShotInfo shot)
    {
        if (IsDestroyed) return;
        float before = Hp;
        bool wasIntact = !IsImmobilized;

        if (shot.IsHeavy)
        {
            Destroy();
        }
        else
        {
            Vector3 local = ToLocal(shot.HitPoint);
            if (local.Y < 0.6f) PunctureNearestTire(local);
            else Hp = Mathf.Max(0.0f, Hp - shot.Damage * 0.35f);
        }

        bool stoppedNow = wasIntact && IsImmobilized;
        TrainingSession.Instance?.RegisterHit(this, shot, stoppedNow, !Hostile, Mathf.Max(0.0f, before - Hp));
    }

    public void ReceiveBlast(float damage, Vector3 origin, long sourcePeerId)
    {
        if (IsDestroyed) return;
        Hp = Mathf.Max(0.0f, Hp - damage);
        if (damage >= 30.0f) PunctureAllTires();
        if (damage >= 75.0f) Destroy();
    }

    public void Destroy()
    {
        if (IsDestroyed) return;
        IsDestroyed = true;
        Hp = 0.0f;
        Drive = default;
        DestroyedEvent?.Invoke(this);
        SetModelTint(new Color(0.12f, 0.12f, 0.12f));
    }

    /// <summary>Drill set-up: start the car already damaged (HP, and which tires are flat: 0=FL 1=FR 2=RL 3=RR).</summary>
    public void PresetDamage(float hp, params int[] flatTires)
    {
        Hp = Mathf.Clamp(hp, 0.0f, MaxHp);
        foreach (int index in flatTires)
            if (index >= 0 && index < TireFlat.Length) TireFlat[index] = true;
    }

    public void PunctureAllTires()
    {
        for (int i = 0; i < TireFlat.Length; i++) PunctureTire(i);
    }

    public void PunctureTire(int index)
    {
        if (index < 0 || index >= TireFlat.Length || TireFlat[index]) return;
        TireFlat[index] = true;
        TirePunctured?.Invoke(this, index);
    }

    private void PunctureNearestTire(Vector3 localPoint)
    {
        // Local -Z is forward: front tires have z < 0; left tires have x < 0.
        int index = (localPoint.Z < 0.0f ? 0 : 2) + (localPoint.X < 0.0f ? 0 : 1);
        if (TireFlat[index])
        {
            // Already flat - take the next intact one on the same axle side so a burst always does something.
            for (int i = 0; i < TireFlat.Length; i++)
                if (!TireFlat[i]) { index = i; break; }
        }
        PunctureTire(index);
    }

    public float DistanceToHull(Vector3 worldPoint)
    {
        Vector3 local = ToLocal(worldPoint);
        float dx = Mathf.Max(Mathf.Abs(local.X) - HalfWidth, 0.0f);
        float dz = Mathf.Max(Mathf.Abs(local.Z) - HalfLength, 0.0f);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    public RepairResult ApplyRepairCharge(float hpAmount)
    {
        if (IsDestroyed) return RepairResult.None;
        for (int i = 0; i < TireFlat.Length; i++)
        {
            if (!TireFlat[i]) continue;
            TireFlat[i] = false;
            return RepairResult.TireFixed;
        }
        if (Hp < MaxHp)
        {
            Hp = Mathf.Min(MaxHp, Hp + hpAmount);
            return RepairResult.BodyRepaired;
        }
        return RepairResult.None;
    }

    private void SetModelTint(Color color)
    {
        var stack = new Stack<Node>();
        stack.Push(_modelPivot);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            foreach (Node child in node.GetChildren()) stack.Push(child);
            if (node is not MeshInstance3D mesh || mesh.Mesh == null) continue;
            for (int i = 0; i < mesh.Mesh.GetSurfaceCount(); i++)
                if (mesh.GetSurfaceOverrideMaterial(i) is BaseMaterial3D material) material.AlbedoColor = color;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Callouts (the AI driver listens)
    // ---------------------------------------------------------------------------------------------
    private void OnCallout(CalloutMessage message)
    {
        if (!message.FromPlayer || !AiDriverPresent) return;
        if (message.Type == CalloutType.SlowDown || message.Type == CalloutType.HoldPosition) Halted = true;
        else if (message.Type == CalloutType.GoGo || message.Type == CalloutType.MoveIn) Halted = false;
    }

    // ---------------------------------------------------------------------------------------------
    // Per-frame
    // ---------------------------------------------------------------------------------------------
    public override void _PhysicsProcess(double deltaValue)
    {
        float dt = (float)deltaValue;
        TrainingSession? session = TrainingSession.Instance;
        bool live = session == null || session.DrillLive;

        if (live)
        {
            HandleSeatInput(session);
            UpdateSwap(dt, session);
            // Exactly one source drives the car each frame; with none, input is cleared so a parked car never inherits
            // the last frame's throttle.
            if (PlayerSeat == Seat.Driver)
            {
                if (Swapping) Drive = default;
                else ReadPlayerDriving();
            }
            else if (AutopilotEnabled) UpdateAutopilot();
            else Drive = default;
            if (AiShooterPresent) UpdateAiShooter(dt);
        }
        else
        {
            Drive = default;
        }

        Integrate(dt);
        FollowSeatLock(session, dt);
        UpdateStatus(session);
    }

    private void ReadPlayerDriving()
    {
        Drive = new DriveInput
        {
            Throttle = Input.GetAxis("move_back", "move_forward"),
            Steer = Input.GetAxis("move_left", "move_right"),
            Brake = Input.IsActionPressed("crouch"),
            Handbrake = Input.IsActionPressed("jump"),
            Boost = Input.IsActionPressed("sprint"),
        };
    }

    private void UpdateAutopilot()
    {
        Drive = default;
        if (Swapping)
        {
            Drive.Brake = true;
            return;
        }
        if (Halted || IsImmobilized || Route.Count == 0 || RouteFinished)
        {
            Drive.Brake = true;
            return;
        }

        Vector3 target = Route[_routeIndex];
        Vector3 to = TrainingUtil.Flat(target - GlobalPosition);
        if (to.Length() < 5.0f)
        {
            _routeIndex++;
            if (_routeIndex >= Route.Count)
            {
                if (LoopRoute)
                {
                    _routeIndex = 0;
                }
                else
                {
                    RouteFinished = true;
                    _routeIndex = Route.Count - 1;
                    RouteCompleted?.Invoke(this);
                    Drive.Brake = true;
                    return;
                }
            }
            target = Route[_routeIndex];
            to = TrainingUtil.Flat(target - GlobalPosition);
        }

        float error = Mathf.Wrap(TrainingUtil.YawFacing(to) - GlobalRotation.Y, -Mathf.Pi, Mathf.Pi);
        Drive.Steer = Mathf.Clamp(-error * 1.6f, -1.0f, 1.0f);
        float desired = CruiseSpeed * (1.0f - Mathf.Min(Mathf.Abs(error), 1.0f) * 0.6f);
        if (_speed < desired) Drive.Throttle = 1.0f;
        else if (_speed > desired + 2.0f) Drive.Brake = true;
    }

    private void Integrate(float dt)
    {
        float boostMult = 1.0f;
        bool boosting = Drive.Boost && Fuel > 0.0f && !IsImmobilized && Drive.Throttle > 0.1f;
        if (boosting) boostMult = BoostMultiplier * (TrainingSession.Instance?.Kit.BoostOutputMult ?? 1.0f);

        if (IsImmobilized)
        {
            _speed = Mathf.MoveToward(_speed, 0.0f, BrakeDecel * 0.9f * dt);
        }
        else if (Drive.Handbrake || Drive.Brake)
        {
            _speed = Mathf.MoveToward(_speed, 0.0f, (Drive.Handbrake ? BrakeDecel * 1.3f : BrakeDecel) * dt);
        }
        else if (Drive.Throttle > 0.05f)
        {
            _speed = Mathf.MoveToward(_speed, MaxSpeed * boostMult * Drive.Throttle, Acceleration * (boosting ? 1.5f : 1.0f) * dt);
        }
        else if (Drive.Throttle < -0.05f)
        {
            if (_speed > 0.5f) _speed = Mathf.MoveToward(_speed, 0.0f, BrakeDecel * dt);
            else _speed = Mathf.MoveToward(_speed, -MaxReverseSpeed * (-Drive.Throttle), Acceleration * 0.6f * dt);
        }
        else
        {
            _speed = Mathf.MoveToward(_speed, 0.0f, 3.5f * dt);
        }

        float steerGrip = Mathf.Clamp(Mathf.Abs(_speed) / 5.0f, 0.0f, 1.0f) * Mathf.Sign(_speed);
        float yawRate = Drive.Steer * SteerRate * steerGrip * (1.0f - 0.35f * Mathf.Min(1.0f, Mathf.Abs(_speed) / MaxSpeed)) * (Drive.Handbrake ? 1.4f : 1.0f);
        RotateY(-yawRate * dt);

        if (!IsImmobilized && (Mathf.Abs(_speed) > 0.2f || Mathf.Abs(Drive.Throttle) > 0.05f))
        {
            float fuelMult = TrainingSession.Instance?.Kit.FuelUseMult ?? 1.0f;
            Fuel = Mathf.Max(0.0f, Fuel - BaseFuelPerSecond * (0.3f + Mathf.Abs(Drive.Throttle)) * (boosting ? 3.0f : 1.0f) * fuelMult * dt);
        }

        Vector3 forward = -GlobalTransform.Basis.Z;
        Vector3 velocity = forward * _speed;
        velocity.Y = IsOnFloor() ? -1.0f : Velocity.Y + PlayerMovement.Gravity * dt;
        Velocity = velocity;
        MoveAndSlide();

        // If we hit a wall the body slowed down - keep our scalar speed honest.
        float actual = new Vector3(Velocity.X, 0, Velocity.Z).Dot(forward);
        if (Mathf.Abs(actual) < Mathf.Abs(_speed)) _speed = actual;

        if (GlobalPosition.Y < -20.0f)
        {
            _speed = 0.0f;
            Velocity = Vector3.Zero;
            GlobalPosition = new Vector3(GlobalPosition.X, 1.0f, GlobalPosition.Z);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Seats
    // ---------------------------------------------------------------------------------------------
    private bool SeatFree(Seat seat) => seat == Seat.Driver ? !AiDriverPresent && PlayerSeat != Seat.Driver
                                                             : !AiShooterPresent && PlayerSeat != Seat.Shooter;

    private void HandleSeatInput(TrainingSession? session)
    {
        if (session == null || !Input.IsActionJustPressed(TrainingInput.Interact)) return;

        if (PlayerSeat == Seat.None)
        {
            if (!AllowEnterExit || IsDestroyed) return;
            if (DistanceToHull(session.Player.GlobalPosition) > 3.0f) return;
            Seat pick = SeatFree(PreferredSeat) ? PreferredSeat
                      : SeatFree(PreferredSeat == Seat.Driver ? Seat.Shooter : Seat.Driver) ? (PreferredSeat == Seat.Driver ? Seat.Shooter : Seat.Driver)
                      : Seat.None;
            if (pick == Seat.None)
            {
                session.Toast("Both seats are taken", new Color(1.0f, 0.6f, 0.4f));
                return;
            }
            EnterSeat(session, pick);
            return;
        }

        bool partnerPresent = PlayerSeat == Seat.Driver ? AiShooterPresent : AiDriverPresent;
        if (partnerPresent && AllowSwap)
        {
            TryStartSwap(session);
            return;
        }
        if (AllowEnterExit && CurrentSpeed < 3.0f) ReleasePlayer();
        else if (AllowEnterExit) session.Toast("Slow down before you get out", new Color(1.0f, 0.75f, 0.35f));
    }

    public void EnterSeat(TrainingSession session, Seat seat)
    {
        PlayerSeat = seat;
        _prevYaw = GlobalRotation.Y;
        session.SetTraineeSeated(true, seat == Seat.Shooter);
        session.Toast(seat == Seat.Driver ? "Driver seat: WASD drive, Shift boost, C brake, Space handbrake"
                                          : "Shooter seat: aim and fire from the moving car", new Color(0.7f, 0.9f, 1.0f));
    }

    private void TryStartSwap(TrainingSession session)
    {
        if (Swapping) return;
        float limit = session.Kit.SwapSpeedLimit(SwapRule);
        if (CurrentSpeed > limit)
        {
            session.Toast($"Too fast to swap - slow below {limit:0} m/s ({CurrentSpeed:0.0} now)", new Color(1.0f, 0.6f, 0.4f));
            SwapRefused?.Invoke(this, limit);
            return;
        }
        _swapRemaining = session.Kit.SeatSwapSeconds;
        session.Toast("Swapping seats...", new Color(0.7f, 0.9f, 1.0f));
    }

    private void UpdateSwap(float dt, TrainingSession? session)
    {
        if (_swapRemaining <= 0.0f) return;
        _swapRemaining -= dt;
        if (_swapRemaining > 0.0f) return;
        _swapRemaining = 0.0f;

        // Roles trade places: the player takes the partner's seat, the AI takes the player's.
        Seat newSeat = PlayerSeat == Seat.Driver ? Seat.Shooter : Seat.Driver;
        AiDriverPresent = newSeat == Seat.Shooter;
        AiShooterPresent = newSeat == Seat.Driver;
        PlayerSeat = newSeat;
        if (AiDriverPresent && Route.Count > 0) AutopilotEnabled = true;
        else if (!AiDriverPresent) AutopilotEnabled = false;
        session?.SetTraineeSeated(true, newSeat == Seat.Shooter);
        session?.Toast(newSeat == Seat.Driver ? "You are the DRIVER" : "You are the SHOOTER", new Color(0.6f, 1.0f, 0.7f));
        SeatSwapped?.Invoke(this, newSeat);
    }

    public void ReleasePlayer()
    {
        if (PlayerSeat == Seat.None) return;
        TrainingSession? session = TrainingSession.Instance;
        PlayerSeat = Seat.None;
        _swapRemaining = 0.0f;
        if (session == null || !IsInstanceValid(session.Player)) return;
        session.SetTraineeSeated(false, true);
        if (IsInsideTree())
        {
            Vector3 side = GlobalTransform.Basis.X;
            session.Player.GlobalPosition = GlobalPosition + side * (HalfWidth + 1.2f) + new Vector3(0, 0.3f, 0);
        }
    }

    private void FollowSeatLock(TrainingSession? session, float dt)
    {
        float yaw = GlobalRotation.Y;
        float deltaYaw = Mathf.Wrap(yaw - _prevYaw, -Mathf.Pi, Mathf.Pi);
        _prevYaw = yaw;
        if (session == null || PlayerSeat == Seat.None || !IsInstanceValid(session.Player)) return;

        CharacterBody3D player = session.Player;
        Node3D seat = PlayerSeat == Seat.Driver ? _driverSeat : _shooterSeat;
        player.GlobalPosition = seat.GlobalPosition;
        player.Velocity = Vector3.Zero;
        if (PlayerSeat == Seat.Driver) player.GlobalRotation = new Vector3(0, yaw, 0);
        else player.RotateY(deltaYaw);       // the shooter's view turns with the car; the mouse still aims freely

        if (PlayerSeat == Seat.Shooter && CurrentSpeed > 1.0f)
        {
            _swayTimer -= dt;
            if (_swayTimer <= 0.0f)
            {
                _swayTimer = 0.12f;
                float amount = 0.25f * Mathf.Clamp(CurrentSpeed / MaxSpeed, 0.0f, 1.0f);
                session.Movement.AddRecoil(_rng.RandfRange(-amount, amount), _rng.RandfRange(-amount, amount), 120.0f);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // AI shooter: the partner in the shooter seat fires at the nearest visible enemy actor.
    // ---------------------------------------------------------------------------------------------
    private void UpdateAiShooter(float dt)
    {
        _aiShotTimer -= dt;
        if (_aiShotTimer > 0.0f || Swapping) return;
        _aiShotTimer = 0.8f;

        TrainingActor? best = null;
        float bestDistance = 45.0f;
        foreach (Node node in GetTree().GetNodesInGroup("training_actor"))
        {
            if (node is not TrainingActor actor || actor.IsDead || actor.Side != TrainingActor.FactionType.Enemy) continue;
            float d = GlobalPosition.DistanceTo(actor.GlobalPosition);
            if (d < bestDistance) { best = actor; bestDistance = d; }
        }
        if (best == null) return;
        if (_rng.Randf() < 0.55f) best.ReceiveBotShot(14.0f, best.GlobalPosition + new Vector3(0, 1.2f, 0));
    }

    // ---------------------------------------------------------------------------------------------
    // Readouts
    // ---------------------------------------------------------------------------------------------
    private void UpdateStatus(TrainingSession? session)
    {
        string tires = $"{4 - FlatTireCount}/4";
        string state = IsDestroyed ? "DESTROYED" : Hp <= 0.0f ? "DISABLED" : FlatTireCount > 0 ? "FLAT TIRE" : Fuel <= 0.0f ? "OUT OF FUEL" : "OK";
        _statusLabel.Text = $"{(Hostile ? "ENEMY " : "")}VEHICLE  {state}\nHP {Mathf.CeilToInt(Hp)}  Tires {tires}  Fuel {Mathf.CeilToInt(Fuel / FuelCapacity * 100.0f)}%";
        _statusLabel.Modulate = IsDestroyed ? new Color(0.6f, 0.6f, 0.6f) : IsImmobilized ? new Color(1.0f, 0.55f, 0.4f) : Hostile ? new Color(1.0f, 0.5f, 0.45f) : new Color(0.7f, 1.0f, 0.75f);

        if (session == null) return;
        if (PlayerSeat != Seat.None)
        {
            string swap = Swapping ? "   SWAPPING..." : "";
            session.VehicleStatus = $"{(PlayerSeat == Seat.Driver ? "DRIVER" : "SHOOTER")}   {CurrentSpeed * 3.6f:0} km/h   Fuel {Mathf.CeilToInt(Fuel / FuelCapacity * 100.0f)}%   HP {Mathf.CeilToInt(Hp)}   Tires {tires}{swap}";
            if (_promptOwned) { session.PromptText = string.Empty; _promptOwned = false; }
            _seatVisualHidden = true;
            return;
        }

        if (_seatVisualHidden) { session.VehicleStatus = string.Empty; _seatVisualHidden = false; }

        bool near = AllowEnterExit && !IsDestroyed && DistanceToHull(session.Player.GlobalPosition) <= 3.0f;
        if (near && session.DrillLive)
        {
            session.PromptText = $"[{TrainingInput.KeyText(TrainingInput.Interact)}] Get in";
            _promptOwned = true;
        }
        else if (_promptOwned)
        {
            session.PromptText = string.Empty;
            _promptOwned = false;
        }
    }
}
