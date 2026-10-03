using Godot;
using System;

/// <summary>Small two-seat gameplay vehicle. The host owns the chassis; clients send driver
/// controls to it, while the two seat IDs and vehicle transform are replicated to every peer.</summary>
public partial class GameVehicle : CharacterBody3D
{
    public const string LandCruiserPath = "res://art/vehicles/Low_Poly_Car_-_Toyota_Land_Cruiser_70_GXL_2024.glb";
    public const string BuggyPath = "res://art/vehicles/raider_buggy.glb";

    [Export] public string ModelPath { get; set; } = BuggyPath;
    [Export] public string DisplayName { get; set; } = "Raider Buggy";
    [Export] public float TargetLength { get; set; } = 4.2f;
    [Export] public float MaxSpeed { get; set; } = 25.0f;
    [Export] public float Acceleration { get; set; } = 10.0f;
    [Export] public float BrakePower { get; set; } = 20.0f;
    [Export] public float SteeringRate { get; set; } = 1.55f;

    [Export] public int DriverPeerId { get; set; }
    [Export] public int PassengerPeerId { get; set; }

    private Node3D _visual = null!;
    private Camera3D? _activeSeatCamera;
    private float _speed;
    private float _steer;
    private float _throttle;
    private bool _brake;
    private bool _handbrake;
    private float _inputSendTimer;
    private Label3D? _prompt;
    private Vector3 _driverSeat = new(-0.48f, 0.38f, -0.12f);
    private Vector3 _passengerSeat = new(0.48f, 0.38f, -0.12f);
    private const float EnterRange = 3.2f;
    private static readonly Vector3 Gravity = new(0, -18.0f, 0);

    public bool IsDriver(int peerId) => peerId > 0 && DriverPeerId == peerId;
    public bool IsPassenger(int peerId) => peerId > 0 && PassengerPeerId == peerId;
    public bool HasSeat(int peerId) => IsDriver(peerId) || IsPassenger(peerId);
    public Vector3 SeatPosition(int peerId) => ToGlobal(IsDriver(peerId) ? _driverSeat : _passengerSeat);
    public Vector3 SeatExitPosition(int peerId)
    {
        float side = IsDriver(peerId) ? -1.0f : 1.0f;
        return ToGlobal(new Vector3(side * 1.55f, 0.15f, 0.15f));
    }

    public override void _Ready()
    {
        AddToGroup("gameplay_vehicles");
        CollisionLayer = 1 << 4;
        CollisionMask = 1;
        AddChild(new CollisionShape3D
        {
            Name = "Hull",
            Shape = new BoxShape3D { Size = new Vector3(1.9f, 1.45f, TargetLength) },
            Position = new Vector3(0, 0.75f, 0),
        });
        _visual = new Node3D { Name = "Visual" };
        AddChild(_visual);
        BuildVisual();
        _prompt = new Label3D
        {
            Name = "Prompt",
            Text = DisplayName,
            Position = new Vector3(0, 2.0f, 0),
            PixelSize = 0.004f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            NoDepthTest = true,
            Modulate = new Color(0.75f, 0.95f, 1.0f),
        };
        AddChild(_prompt);
        VehicleInput.EnsureRegistered();
        GetNode<MultiplayerSynchronizer>("MultiplayerSynchronizer").SetMultiplayerAuthority(1);
        RefreshSeatLocks();
    }

    private void BuildVisual()
    {
        var packed = ResourceLoader.Load<PackedScene>(ModelPath);
        if (packed == null)
        {
            GD.PushWarning($"Gameplay vehicle model was not found: {ModelPath}");
            return;
        }
        var model = packed.Instantiate<Node3D>();
        _visual.AddChild(model);
        FitModel(model);
    }

    private void FitModel(Node3D model)
    {
        var meshes = model.FindChildren("*", "MeshInstance3D", true, false);
        var bounds = new Aabb();
        bool found = false;
        foreach (Node item in meshes)
        {
            if (item is not MeshInstance3D mesh || mesh.Mesh == null) continue;
            Aabb box = mesh.Transform * mesh.Mesh.GetAabb();
            bounds = found ? bounds.Merge(box) : box;
            found = true;
        }
        if (!found || bounds.Size.LengthSquared() < 0.0001f) return;

        bool longX = bounds.Size.X >= bounds.Size.Z;
        float length = longX ? bounds.Size.X : bounds.Size.Z;
        float scale = TargetLength / Mathf.Max(0.001f, length);
        _visual.Scale = Vector3.One * scale;
        // Both source assets have their hood identifiable as the taller front quarter. Orient that
        // end to Godot forward (-Z), then set the wheels on the ground and centre the hull.
        float lowY = float.MinValue, highY = float.MinValue;
        foreach (Node item in meshes)
        {
            if (item is not MeshInstance3D mesh || mesh.Mesh == null) continue;
            Aabb box = mesh.Transform * mesh.Mesh.GetAabb();
            float centre = longX ? box.GetCenter().X : box.GetCenter().Z;
            float pivot = longX ? bounds.Position.X + bounds.Size.X * 0.5f : bounds.Position.Z + bounds.Size.Z * 0.5f;
            if (centre < pivot) lowY = Mathf.Max(lowY, box.End.Y);
            else highY = Mathf.Max(highY, box.End.Y);
        }
        float yaw = longX ? (lowY < highY ? -90f : 90f) : (lowY < highY ? 0f : 180f);
        _visual.RotationDegrees = new Vector3(0, yaw, 0);
        _visual.Position = new Vector3(-bounds.GetCenter().X * scale, -bounds.Position.Y * scale, -bounds.GetCenter().Z * scale);
        if (DisplayName == "Raider Buggy")
        {
            // The source buggy is an open two-seat side-by-side rig. Keep the collision width and
            // seat anchors scaled to the same wheelbase used by the rendered model.
            _driverSeat = new Vector3(-0.43f, 0.48f, -0.13f);
            _passengerSeat = new Vector3(0.43f, 0.48f, -0.13f);
        }
    }

    public override void _Process(double delta)
    {
        if (!Multiplayer.HasMultiplayerPeer() || Multiplayer.IsServer())
            ProcessServerInteraction();
        else
            ProcessClientInteraction();
        RefreshSeatLocks();
        UpdatePrompt();

        int localId = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetUniqueId() : 1;
        if (PlayerIdentity.IsLocalHuman(FindPlayer(localId)) && IsDriver(localId))
        {
            if (Input.IsKeyPressed(Key.C) && !_cameraKeyWasDown) ToggleDriverCamera(localId);
            _cameraKeyWasDown = Input.IsKeyPressed(Key.C);
        }
        else _cameraKeyWasDown = false;
    }

    private bool _cameraKeyWasDown;

    private void ProcessServerInteraction()
    {
        int id = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetUniqueId() : 1;
        var player = FindPlayer(id);
        if (player == null || !PlayerIdentity.IsLocalHuman(player)) return;
        if (Input.IsActionJustPressed("vehicle_interact")) RequestSeatChange(id);
        if (IsDriver(id)) ReadDriverInput();
    }

    private void ProcessClientInteraction()
    {
        int id = Multiplayer.GetUniqueId();
        var player = FindPlayer(id);
        if (player == null || !PlayerIdentity.IsLocalHuman(player)) return;
        if (Input.IsActionJustPressed("vehicle_interact")) RpcId(1, MethodName.RpcRequestSeatChange, id);
        if (IsDriver(id))
        {
            ReadDriverInput();
            _inputSendTimer += (float)GetProcessDeltaTime();
            if (_inputSendTimer >= 0.05f)
            {
                _inputSendTimer = 0;
                RpcId(1, MethodName.RpcSetDriverInput, _steer, _throttle, _brake, _handbrake);
            }
        }
    }

    private void ReadDriverInput()
    {
        _steer = Input.GetAxis("vehicle_left", "vehicle_right");
        _throttle = Input.GetActionStrength("vehicle_gas") - Input.GetActionStrength("vehicle_reverse");
        _brake = Input.IsActionPressed("vehicle_brake");
        _handbrake = Input.IsActionPressed("vehicle_handbrake");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcRequestSeatChange(int peerId)
    {
        if (!Multiplayer.IsServer()) return;
        RequestSeatChange(peerId);
    }

    private void RequestSeatChange(int peerId)
    {
        if (!Multiplayer.IsServer() && Multiplayer.HasMultiplayerPeer()) return;
        if (HasSeat(peerId))
        {
            if (IsDriver(peerId) && Mathf.Abs(_speed) > 1.0f) return;
            Vector3 exit = SeatExitPosition(peerId);
            Rpc(MethodName.RpcSetSeats, DriverPeerId == peerId ? 0 : DriverPeerId,
                PassengerPeerId == peerId ? 0 : PassengerPeerId, peerId, exit);
            return;
        }
        if (GlobalPosition.DistanceTo(FindPlayer(peerId)?.GlobalPosition ?? Vector3.Inf) > EnterRange) return;
        if (DriverPeerId != 0 && PassengerPeerId != 0) return;
        bool driver = DriverPeerId == 0;
        int newDriver = driver ? peerId : DriverPeerId;
        int newPassenger = driver ? PassengerPeerId : peerId;
        Rpc(MethodName.RpcSetSeats, newDriver, newPassenger, 0, Vector3.Zero);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RpcSetSeats(int driver, int passenger, int exitingPeer, Vector3 exitPosition)
    {
        DriverPeerId = driver;
        PassengerPeerId = passenger;
        if (exitingPeer != 0 && FindPlayer(exitingPeer) is PlayerMovement exiting)
            exiting.LeaveVehicle(this, exitPosition);
        RefreshSeatLocks();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void RpcSetDriverInput(float steer, float throttle, bool brake, bool handbrake)
    {
        if (!Multiplayer.IsServer()) return;
        int sender = Multiplayer.GetRemoteSenderId();
        if (!IsDriver(sender)) return;
        _steer = Mathf.Clamp(steer, -1f, 1f);
        _throttle = Mathf.Clamp(throttle, -1f, 1f);
        _brake = brake;
        _handbrake = handbrake;
    }

    public override void _PhysicsProcess(double deltaValue)
    {
        if (Multiplayer.HasMultiplayerPeer() && !Multiplayer.IsServer()) return;
        float dt = (float)deltaValue;
        if (DriverPeerId != 0)
        {
            float target = _throttle * MaxSpeed;
            float rate = Mathf.Abs(_throttle) > 0.01f ? Acceleration : ( _brake ? BrakePower : 2.5f );
            if (_handbrake) rate *= 2.2f;
            _speed = Mathf.MoveToward(_speed, target, rate * dt);
            float steerScale = Mathf.Clamp(Mathf.Abs(_speed) / 3.0f, 0.15f, 1.0f);
            RotateY(-_steer * SteeringRate * steerScale * dt * Mathf.Sign(_speed == 0 ? 1 : _speed));
        }
        else _speed = Mathf.MoveToward(_speed, 0, 3.0f * dt);

        Velocity = -GlobalBasis.Z * _speed + new Vector3(0, Velocity.Y, 0);
        if (!IsOnFloor()) Velocity += Gravity * dt;
        else if (Velocity.Y < 0) Velocity = new Vector3(Velocity.X, -0.1f, Velocity.Z);
        MoveAndSlide();
    }

    private void RefreshSeatLocks()
    {
        foreach (int id in new[] { DriverPeerId, PassengerPeerId })
        {
            if (id <= 0 || FindPlayer(id) is not PlayerMovement player) continue;
            player.EnterVehicle(this, IsDriver(id), SeatPosition(id));
        }
        int local = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetUniqueId() : 1;
        if (!IsDriver(local) && _activeSeatCamera != null)
        {
            _activeSeatCamera.Current = false;
            _activeSeatCamera = null;
        }
    }

    public void ToggleDriverCamera(int peerId)
    {
        if (!IsDriver(peerId) || FindPlayer(peerId) is not PlayerMovement player) return;
        player.SetVehicleCameraView(!player.IsThirdPersonVehicleView, this);
    }

    private void UpdatePrompt()
    {
        if (_prompt == null) return;
        int local = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetUniqueId() : 1;
        var player = FindPlayer(local);
        bool near = player != null && GlobalPosition.DistanceTo(player.GlobalPosition) <= EnterRange;
        _prompt.Text = HasSeat(local)
            ? (IsDriver(local) ? "DRIVER  •  E EXIT  •  C CAMERA" : "GUNNER  •  E EXIT")
            : near ? $"{DisplayName}  •  E ENTER" : DisplayName;
    }

    private static PlayerMovement? FindPlayer(int peerId)
    {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null) return null;
        foreach (Node node in tree.GetNodesInGroup("players"))
            if (node is PlayerMovement player && (int)player.GetMultiplayerAuthority() == peerId) return player;
        return null;
    }
}

public static class VehicleInput
{
    public static void EnsureRegistered()
    {
        Register("vehicle_interact", Key.E);
        Register("vehicle_gas", Key.W);
        Register("vehicle_reverse", Key.S);
        Register("vehicle_left", Key.A);
        Register("vehicle_right", Key.D);
        Register("vehicle_brake", Key.Space);
        Register("vehicle_handbrake", Key.Shift);
    }

    private static void Register(string action, Key key)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
    }
}
