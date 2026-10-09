using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// A shared character in the training arena: an enemy to read and shoot, or a friend to heal, cover and coordinate with.
/// Always built from AshenCorvus.fbx (SharedCharacterModel) - never any other character.
///
/// It is a plain CharacterBody3D rather than a Player.tscn instance on purpose: Player.tscn shares one
/// CapsuleShape3D resource between every instance (the local player's crouch would resize every bot's hitbox)
/// and drags in HUD/audio/networking nodes a bot must not have.
///
/// Damage comes in through IShootable.ReceiveShot (weapon hits), IExplosionReceiver (charges) or IHealable.Heal
/// (Medic). It shoots by hitscan with real LOS, so cover, smoke and the trainee's own body block it.
/// </summary>
public partial class TrainingActor : CharacterBody3D, IShootable, IHealable, IExplosionReceiver
{
    public enum FactionType { Enemy, Friend }
    public enum BehaviorType { Idle, Strafe, MoveTo, Peek, Rush }
    private enum PeekPhase { Hidden, Tell, Exposed }

    // ---- configuration (set by the drill before AddChild, or any time later) ----------------------
    public FactionType Side { get; set; } = FactionType.Enemy;
    public string ActorName { get; set; } = "shared character";
    public float MaxHp { get; set; } = 100.0f;
    public BehaviorType Mode { get; set; } = BehaviorType.Idle;
    public BehaviorType NextMode { get; set; } = BehaviorType.Idle;   // what to do after MoveTo arrives
    public bool Enabled { get; set; } = true;
    public string WeaponName { get; set; } = "AK-47";

    public bool CanShoot { get; set; }
    public bool ShootsPlayer { get; set; } = true;
    public bool ShootsOpponents { get; set; } = true;   // enemies: friends+trainee ; friends: enemies
    public bool ShootsCars { get; set; }
    public float FireInterval { get; set; } = 0.7f;
    public float Accuracy { get; set; } = 0.5f;          // 0 = sprays wide, 1 = laser
    public float ShotDamage { get; set; } = 6.0f;
    public float ShootRange { get; set; } = 55.0f;
    public float TellSeconds { get; set; } = 0.6f;       // the reaction window before the first shot

    public float MoveSpeed { get; set; } = 4.0f;
    public Vector3 Anchor { get; set; }                  // strafe centre / peek cover point
    public Vector3 Axis { get; set; } = Vector3.Right;   // strafe / peek axis (unit, flat)
    public float Amplitude { get; set; } = 3.0f;         // strafe half-width or peek distance
    public float Period { get; set; } = 3.0f;
    public float PhaseOffset { get; set; }
    public Vector3 Destination { get; set; }
    public float HideSeconds { get; set; } = 2.0f;       // Peek: hidden time
    public float ExposeSeconds { get; set; } = 1.4f;     // Peek: exposed time
    public float BleedPerSecond { get; set; }

    public bool ReactsToCallouts { get; set; }
    /// <summary>Peek only: flash "! PEEKING !" over the actor during the tell (a learning aid; off for the real test).</summary>
    public bool ShowsPeekTell { get; set; }

    // ---- state ----------------------------------------------------------------------------------
    public float Hp { get; private set; }
    public bool IsDead { get; private set; }
    public bool IsExposed => Mode != BehaviorType.Peek || _peekPhase == PeekPhase.Exposed;
    public bool IsEngaging => !IsDead && CanShoot && _hasLineOfSight && IsExposed;
    public float AliveSeconds => _aliveTime;

    public event Action<TrainingActor, ShotInfo?>? Died;
    public event Action<TrainingActor, float>? Damaged;
    /// <summary>Peek only: raised the instant the actor steps into view (reaction-time start).</summary>
    public event Action<TrainingActor>? BecameExposed;
    public event Action<TrainingActor>? Arrived;

    private Node3D _armature = null!;
    private Node3D _head = null!;
    private WeaponAttachment? _weapon;
    private Label3D _label = null!;
    private MeshInstance3D _ring = null!;
    private readonly RandomNumberGenerator _rng = new();

    private float _aliveTime;
    private float _fireCooldown;
    private float _tellRemaining = -1.0f;
    private float _losTimer;
    private bool _hasLineOfSight;
    private Node3D? _targetNode;
    private PeekPhase _peekPhase = PeekPhase.Hidden;
    private float _phaseTimer;
    private float _bleedAccumulator;
    private float _deathTimer = -1.0f;

    public string DisplayName => ActorName;
    public Vector3 WorldPosition => GlobalPosition;
    public float CurrentHp => Hp;
    public bool IsAlive => !IsDead;
    public Vector3 BlastPosition => GlobalPosition;
    public bool IsFleshTarget => true;

    public override void _Ready()
    {
        _rng.Randomize();
        Hp = MaxHp;
        AddToGroup("training_actor");
        AddToGroup("blast_receiver");
        CollisionLayer = 1;
        CollisionMask = 1;

        AddChild(new CollisionShape3D
        {
            Name = "Collision",
            Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.8f },
            Position = new Vector3(0, 0.9f, 0),
        });

        Node3D? body = SharedCharacterModel.InstantiateBody();
        if (body != null)
        {
            _armature = body;
            _armature.Transform = new Transform3D(SharedCharacterModel.ArmatureBasis, Vector3.Zero);
            AddChild(_armature);
        }
        else
        {
            _armature = new Node3D { Name = "Armature" };   // keeps the rest of the setup safe if the asset is missing
            AddChild(_armature);
        }

        _head = new Node3D { Name = "Head", Position = new Vector3(0, 1.62f, 0) };
        AddChild(_head);

        EquipWeapon();

        Color tint = Side == FactionType.Enemy ? new Color(1.0f, 0.3f, 0.25f) : new Color(0.3f, 1.0f, 0.45f);
        _ring = TrainingUtil.GroundDisc(0.65f, tint, Vector3.Zero, 0.5f);
        AddChild(_ring);
        _label = TrainingUtil.Label("", new Vector3(0, 2.35f, 0), tint, 32, true);
        AddChild(_label);
        RefreshLabel();

        if (ReactsToCallouts && TrainingSession.Instance != null)
            TrainingSession.Instance.Callouts.Issued += OnCallout;
    }

    public override void _ExitTree()
    {
        if (ReactsToCallouts && TrainingSession.Instance != null && IsInstanceValid(TrainingSession.Instance.Callouts))
            TrainingSession.Instance.Callouts.Issued -= OnCallout;
    }

    private void EquipWeapon()
    {
        WeaponData? data = TrainingWeapons.Find(WeaponName);
        if (data == null) return;
        _weapon = new WeaponAttachment { Name = "WeaponAttachment" };
        _armature.AddChild(_weapon);
        _weapon.Equip(data);
    }

    private void RefreshLabel()
    {
        string tag = Side == FactionType.Enemy ? "ENEMY" : "ALLY";
        _label.Text = IsDead ? $"{ActorName} (down)" : $"{tag} {ActorName}  {Mathf.CeilToInt(Hp)}/{Mathf.CeilToInt(MaxHp)}";
    }

    // ---------------------------------------------------------------------------------------------
    // Damage / healing
    // ---------------------------------------------------------------------------------------------
    public bool IsHeadHit(Vector3 worldPoint) => worldPoint.DistanceTo(_head.GlobalPosition) <= 0.24f;

    public void ReceiveShot(ShotInfo shot)
    {
        if (IsDead) return;
        float before = Hp;
        Hp = Mathf.Max(0.0f, Hp - shot.Damage);
        bool killed = Hp <= 0.0f;
        TrainingSession.Instance?.RegisterHit(this, shot, killed, Side == FactionType.Friend, before - Hp);
        AfterDamage(before - Hp, shot, killed);
    }

    public void ReceiveBlast(float damage, Vector3 origin, long sourcePeerId)
    {
        if (IsDead) return;
        float before = Hp;
        Hp = Mathf.Max(0.0f, Hp - damage);
        AfterDamage(before - Hp, null, Hp <= 0.0f);
    }

    /// <summary>Damage from another actor's bullet (bots do not go through WeaponSwitcher).</summary>
    public void ReceiveBotShot(float damage, Vector3 hitPoint)
    {
        if (IsDead) return;
        float before = Hp;
        Hp = Mathf.Max(0.0f, Hp - damage);
        AfterDamage(before - Hp, null, Hp <= 0.0f);
    }

    private void AfterDamage(float dealt, ShotInfo? shot, bool killed)
    {
        Damaged?.Invoke(this, dealt);
        RefreshLabel();
        if (killed) Die(shot);
    }

    public void Heal(float amount)
    {
        if (IsDead) return;
        Hp = Mathf.Min(MaxHp, Hp + amount);
        RefreshLabel();
    }

    /// <summary>Sets HP directly (drill set-up: "this friend starts at 20 HP").</summary>
    public void SetHp(float hp)
    {
        Hp = Mathf.Clamp(hp, 0.0f, MaxHp);
        RefreshLabel();
    }

    private void Die(ShotInfo? shot)
    {
        if (IsDead) return;
        IsDead = true;
        CanShoot = false;
        CollisionLayer = 0;
        CollisionMask = 0;
        Velocity = Vector3.Zero;
        _ring.Visible = false;
        RefreshLabel();
        _deathTimer = 3.0f;
        Died?.Invoke(this, shot);
    }

    // ---------------------------------------------------------------------------------------------
    // Callouts (friends only)
    // ---------------------------------------------------------------------------------------------
    private void OnCallout(CalloutMessage message)
    {
        if (IsDead || Side != FactionType.Friend || !message.FromPlayer) return;
        TrainingSession? session = TrainingSession.Instance;
        if (session == null) return;

        switch (message.Type)
        {
            case CalloutType.HoldPosition:
                Mode = BehaviorType.Idle;
                break;
            case CalloutType.MoveIn:
            case CalloutType.GoGo:
                CanShoot = true;
                Mode = BehaviorType.Rush;
                break;
            case CalloutType.CoverMe:
                CanShoot = true;
                break;
            case CalloutType.FireInTheHole:
                ClearBlastZones(session);
                break;
        }
    }

    private void ClearBlastZones(TrainingSession session)
    {
        foreach (Node node in GetTree().GetNodesInGroup("placed_charge"))
        {
            if (node is not PlacedCharge charge || charge.IsSpent) continue;
            float safe = charge.Radius + 2.0f;
            if (TrainingUtil.FlatDistance(GlobalPosition, charge.GlobalPosition) >= safe) continue;

            Vector3 away = TrainingUtil.Flat(GlobalPosition - charge.GlobalPosition);
            if (away.LengthSquared() < 0.01f) away = Vector3.Back;
            Destination = charge.GlobalPosition + away.Normalized() * (safe + 1.5f);
            Destination = new Vector3(Destination.X, GlobalPosition.Y, Destination.Z);
            NextMode = BehaviorType.Idle;
            Mode = BehaviorType.MoveTo;
            session.Callouts.Send(CalloutType.Clearing, ActorName, false, GlobalPosition);
            return;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Per-frame
    // ---------------------------------------------------------------------------------------------
    public override void _PhysicsProcess(double deltaValue)
    {
        float dt = (float)deltaValue;

        if (_deathTimer > 0.0f)
        {
            _deathTimer -= dt;
            if (_deathTimer <= 0.0f) QueueFree();
            return;
        }
        if (IsDead) return;

        TrainingSession? session = TrainingSession.Instance;
        bool live = Enabled && (session == null || session.DrillLive);

        Vector3 velocity = Velocity;
        if (!IsOnFloor()) velocity.Y += PlayerMovement.Gravity * dt;
        else velocity.Y = -0.5f;

        Vector3 desiredHorizontal = Vector3.Zero;
        Vector3? faceDir = null;

        if (live)
        {
            _aliveTime += dt;
            if (BleedPerSecond > 0.0f) ApplyBleed(dt);
            if (IsDead) return;

            UpdateTarget(dt);
            desiredHorizontal = ComputeMovement(dt);

            if (_targetNode != null && _hasLineOfSight)
                faceDir = _targetNode.GlobalPosition - GlobalPosition;
            else if (desiredHorizontal.LengthSquared() > 0.05f)
                faceDir = desiredHorizontal;

            UpdateShooting(dt);
        }

        velocity.X = desiredHorizontal.X;
        velocity.Z = desiredHorizontal.Z;
        Velocity = velocity;
        MoveAndSlide();

        if (faceDir.HasValue && TrainingUtil.Flat(faceDir.Value).LengthSquared() > 0.01f)
        {
            float targetYaw = TrainingUtil.YawFacing(faceDir.Value);
            Rotation = new Vector3(0, Mathf.LerpAngle(Rotation.Y, targetYaw, Mathf.Min(1.0f, 10.0f * dt)), 0);
        }

        // Full-body shared character clips were removed. Training actors keep their gameplay and rotate to face
        // their movement/target while remaining in the static bind pose.
    }

    private void ApplyBleed(float dt)
    {
        _bleedAccumulator += BleedPerSecond * dt;
        if (_bleedAccumulator < 1.0f) return;
        float whole = Mathf.Floor(_bleedAccumulator);
        _bleedAccumulator -= whole;
        Hp = Mathf.Max(0.0f, Hp - whole);
        RefreshLabel();
        if (Hp <= 0.0f) Die(null);
    }

    private Vector3 ComputeMovement(float dt)
    {
        switch (Mode)
        {
            case BehaviorType.Strafe:
            {
                float omega = Mathf.Tau / Mathf.Max(0.1f, Period);
                float t = _aliveTime * omega + PhaseOffset;
                Vector3 desired = Anchor + Axis.Normalized() * (Amplitude * Mathf.Sin(t));
                Vector3 flatVelocity = Axis.Normalized() * (Amplitude * omega * Mathf.Cos(t));
                Vector3 correction = TrainingUtil.Flat(desired - GlobalPosition) * 3.0f;
                return flatVelocity + correction;
            }
            case BehaviorType.MoveTo:
            {
                Vector3 to = TrainingUtil.Flat(Destination - GlobalPosition);
                if (to.Length() < 0.6f)
                {
                    Mode = NextMode;
                    Arrived?.Invoke(this);
                    return Vector3.Zero;
                }
                return to.Normalized() * MoveSpeed;
            }
            case BehaviorType.Rush:
            {
                Node3D? chase = FindTargetNode();
                if (chase == null) return Vector3.Zero;
                Vector3 to = TrainingUtil.Flat(chase.GlobalPosition - GlobalPosition);
                return to.Length() > 7.0f ? to.Normalized() * MoveSpeed : Vector3.Zero;
            }
            case BehaviorType.Peek:
                return ComputePeek(dt);
            default:
                return Vector3.Zero;
        }
    }

    private Vector3 ComputePeek(float dt)
    {
        _phaseTimer -= dt;
        Vector3 hidden = Anchor;
        Vector3 exposed = Anchor + Axis.Normalized() * Amplitude;
        Vector3 goal = _peekPhase == PeekPhase.Exposed ? exposed : hidden;

        switch (_peekPhase)
        {
            case PeekPhase.Hidden:
                if (_phaseTimer <= 0.0f)
                {
                    _peekPhase = PeekPhase.Tell;
                    _phaseTimer = TellSeconds;
                    if (ShowsPeekTell) _label.Text = "! PEEKING !";
                }
                break;
            case PeekPhase.Tell:
                if (_phaseTimer <= 0.0f)
                {
                    _peekPhase = PeekPhase.Exposed;
                    _phaseTimer = ExposeSeconds;
                    _tellRemaining = 0.0f;
                    RefreshLabel();
                    BecameExposed?.Invoke(this);
                }
                break;
            case PeekPhase.Exposed:
                if (_phaseTimer <= 0.0f)
                {
                    _peekPhase = PeekPhase.Hidden;
                    _phaseTimer = HideSeconds + _rng.RandfRange(0.0f, 1.0f);
                }
                break;
        }

        Vector3 to = TrainingUtil.Flat(goal - GlobalPosition);
        if (to.Length() < 0.15f) return Vector3.Zero;
        return to.Normalized() * Mathf.Min(6.0f, to.Length() * 8.0f);
    }

    /// <summary>The peek tell: the actor leans toward the exposed side before stepping out, and this is true for that window.</summary>
    public bool IsTelegraphing => Mode == BehaviorType.Peek && _peekPhase == PeekPhase.Tell;

    // ---------------------------------------------------------------------------------------------
    // Targeting + shooting
    // ---------------------------------------------------------------------------------------------
    private Node3D? FindTargetNode()
    {
        TrainingSession? session = TrainingSession.Instance;
        if (session == null) return null;

        Node3D? best = null;
        float bestDist = ShootRange;
        Vector3 from = GlobalPosition;

        if (Side == FactionType.Enemy)
        {
            if (ShootsPlayer && !session.TraineeDowned)
            {
                float d = from.DistanceTo(session.Player.GlobalPosition);
                if (d < bestDist) { best = session.Player; bestDist = d; }
            }
            if (ShootsOpponents)
                foreach (Node node in GetTree().GetNodesInGroup("training_actor"))
                    if (node is TrainingActor other && !other.IsDead && other.Side == FactionType.Friend)
                    {
                        float d = from.DistanceTo(other.GlobalPosition);
                        if (d < bestDist) { best = other; bestDist = d; }
                    }
        }
        else if (ShootsOpponents)
        {
            foreach (Node node in GetTree().GetNodesInGroup("training_actor"))
                if (node is TrainingActor other && !other.IsDead && other.Side == FactionType.Enemy)
                {
                    float d = from.DistanceTo(other.GlobalPosition);
                    if (d < bestDist) { best = other; bestDist = d; }
                }
        }

        if (ShootsCars)
            foreach (Node node in GetTree().GetNodesInGroup("training_car"))
                if (node is TrainingCar car && !car.IsDestroyed)
                {
                    float d = from.DistanceTo(car.GlobalPosition);
                    if (d < bestDist) { best = car; bestDist = d; }
                }
        return best;
    }

    private void UpdateTarget(float dt)
    {
        _losTimer -= dt;
        if (_losTimer > 0.0f && _targetNode != null && IsInstanceValid(_targetNode)) return;
        _losTimer = 0.2f;

        _targetNode = FindTargetNode();
        _hasLineOfSight = _targetNode != null && HasLineOfSightTo(_targetNode);
        if (!_hasLineOfSight) _tellRemaining = -1.0f;
    }

    private Vector3 AimPointOf(Node3D target)
    {
        if (target is TrainingCar) return target.GlobalPosition + new Vector3(0, 0.9f, 0);
        return target.GlobalPosition + new Vector3(0, 1.2f, 0);
    }

    private Vector3 MuzzlePosition()
    {
        if (_weapon?.Muzzle != null && IsInstanceValid(_weapon.Muzzle)) return _weapon.Muzzle.GlobalPosition;
        return _head.GlobalPosition;
    }

    private bool HasLineOfSightTo(Node3D target)
    {
        Vector3 eye = _head.GlobalPosition;
        Vector3 aim = AimPointOf(target);
        if (TrainingSmoke.Blocks(GetTree(), eye, aim)) return false;

        var query = PhysicsRayQueryParameters3D.Create(eye, aim, uint.MaxValue, new Godot.Collections.Array<Rid> { GetRid() });
        Godot.Collections.Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (result.Count == 0) return true;
        return result["collider"].As<Node>() == target;
    }

    private void UpdateShooting(float dt)
    {
        _fireCooldown -= dt;
        if (!CanShoot || _targetNode == null || !IsInstanceValid(_targetNode) || !_hasLineOfSight || !IsExposed)
        {
            _tellRemaining = -1.0f;
            return;
        }

        // The tell: after first spotting a target the actor waits TellSeconds before it fires - that is the
        // window a trainee has to shoot first. (Peek supplies its own longer tell in ComputePeek.)
        if (_tellRemaining < 0.0f) _tellRemaining = Mode == BehaviorType.Peek ? 0.0f : TellSeconds;
        if (_tellRemaining > 0.0f)
        {
            _tellRemaining -= dt;
            return;
        }
        if (_fireCooldown > 0.0f) return;

        _fireCooldown = FireInterval;
        FireAt(_targetNode);
    }

    private void FireAt(Node3D target)
    {
        TrainingSession? session = TrainingSession.Instance;
        if (session == null) return;

        Vector3 from = MuzzlePosition();
        Vector3 aim = AimPointOf(target);
        if (target is TrainingCar && _rng.Randf() < 0.6f) aim = target.GlobalPosition + new Vector3(0, 0.35f, 0);

        float distance = from.DistanceTo(aim);
        float miss = (1.0f - Mathf.Clamp(Accuracy, 0.0f, 1.0f)) * distance * 0.07f;
        aim += new Vector3(_rng.RandfRange(-miss, miss), _rng.RandfRange(-miss, miss) * 0.6f, _rng.RandfRange(-miss, miss));

        Vector3 direction = (aim - from).Normalized();
        Vector3 to = from + direction * (ShootRange + 5.0f);
        var query = PhysicsRayQueryParameters3D.Create(from, to, uint.MaxValue, new Godot.Collections.Array<Rid> { GetRid() });
        Godot.Collections.Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);

        Vector3 end = to;
        if (result.Count > 0)
        {
            end = (Vector3)result["position"];
            Node? collider = result["collider"].As<Node>();
            ApplyBotHit(collider, end, direction);
        }

        _weapon?.PlayFire();
        SpawnTracer(from, end);
    }

    private void ApplyBotHit(Node? collider, Vector3 point, Vector3 direction)
    {
        TrainingSession? session = TrainingSession.Instance;
        if (collider == null || session == null) return;

        if (collider == session.Player)
        {
            TrainingDamage.HitTrainee(session.PlayerHealth, ShotDamage);
            return;
        }
        if (collider is TrainingActor other)
        {
            if (other.Side == Side) return;      // bots never shoot their own side
            other.ReceiveBotShot(ShotDamage, point);
            return;
        }
        if (collider is IShootable shootable)
        {
            shootable.ReceiveShot(new ShotInfo
            {
                Damage = ShotDamage,
                IsHeadshot = false,
                Styles = KillStyle.None,
                HitPoint = point,
                Direction = direction,
                Distance = GlobalPosition.DistanceTo(point),
                AttackerPeerId = -1,
                WeaponName = WeaponName,
                WeaponClass = "Rifle",
            });
        }
    }

    private void SpawnTracer(Vector3 from, Vector3 to)
    {
        float length = from.DistanceTo(to);
        if (length < 0.5f) return;
        Node? parent = GetParent();
        if (parent == null) return;

        var tracer = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.025f, 0.025f, length) },
            MaterialOverride = TrainingUtil.Mat(new Color(1.0f, 0.85f, 0.4f), true),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        parent.AddChild(tracer);
        Vector3 mid = (from + to) * 0.5f;
        Vector3 dir = (to - from).Normalized();
        Vector3 up = Mathf.Abs(dir.Y) > 0.95f ? Vector3.Right : Vector3.Up;
        tracer.LookAtFromPosition(mid, mid + dir, up);
        GetTree().CreateTimer(0.05).Timeout += () => TrainingUtil.SafeFree(tracer);
    }
}
