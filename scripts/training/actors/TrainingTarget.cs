using Godot;
using System;

/// <summary>
/// A shootable range target. One class covers every aim drill: a static or pop-up plate-man, a strafing one,
/// a no-shoot (friendly) one that penalises hits, and a big indestructible wall for recoil analysis.
/// It is a StaticBody3D moved by code, so bullets, headshot zones and raycasts behave like any world body.
/// </summary>
public partial class TrainingTarget : StaticBody3D, IShootable
{
    public enum MoveMode { Static, Strafe, Line }

    public bool NoShoot { get; set; }              // friendly silhouette: hitting it is a mistake
    public bool Indestructible { get; set; }       // counts hits forever (tracking / recoil)
    public bool IsWall { get; set; }               // big flat wall, no head zone
    public Vector2 WallSize { get; set; } = new Vector2(3.0f, 3.0f);
    public float MaxHp { get; set; } = 100.0f;
    public float Lifetime { get; set; } = -1.0f;   // seconds before it vanishes (pop-up); <=0 = forever

    public MoveMode Movement { get; set; } = MoveMode.Static;
    public Vector3 Anchor { get; set; }
    public Vector3 Axis { get; set; } = Vector3.Right;
    public float Amplitude { get; set; } = 3.0f;
    public float Period { get; set; } = 3.0f;
    public float PhaseOffset { get; set; }
    public Vector3 LineTo { get; set; }
    public float LineSpeed { get; set; } = 5.0f;

    public float Hp { get; private set; }
    public bool IsDown { get; private set; }
    public int HitCount { get; private set; }
    public float Age => _age;
    public bool IsFleshTarget => false;

    public event Action<TrainingTarget, ShotInfo>? Hit;
    public event Action<TrainingTarget, ShotInfo>? Killed;
    public event Action<TrainingTarget>? Expired;

    private Node3D _head = null!;
    private MeshInstance3D _torso = null!;
    private StandardMaterial3D _torsoMaterial = null!;
    private CollisionShape3D _bodyShape = null!;
    private CollisionShape3D? _headShape;
    private float _age;
    private bool _reactionLogged;
    private float _flash;
    private Color _baseColor;

    public override void _Ready()
    {
        Hp = MaxHp;
        CollisionLayer = 1;
        CollisionMask = 0;
        _baseColor = NoShoot ? new Color(0.25f, 0.8f, 0.4f) : new Color(0.85f, 0.25f, 0.2f);
        _torsoMaterial = TrainingUtil.Mat(_baseColor);

        if (IsWall)
        {
            var size = new Vector3(WallSize.X, WallSize.Y, 0.2f);
            _torso = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = _torsoMaterial, Position = new Vector3(0, WallSize.Y * 0.5f, 0) };
            AddChild(_torso);
            _bodyShape = new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = _torso.Position };
            AddChild(_bodyShape);
            // Concentric rings so recoil drift is readable at a glance.
            AddChild(TrainingUtil.Box(new Vector3(WallSize.X * 0.5f, WallSize.Y * 0.5f, 0.01f), new Color(1, 1, 1, 1), _torso.Position + new Vector3(0, 0, 0.11f), "RingOuter"));
            AddChild(TrainingUtil.Box(new Vector3(WallSize.X * 0.16f, WallSize.Y * 0.16f, 0.01f), new Color(0.15f, 0.15f, 0.15f, 1), _torso.Position + new Vector3(0, 0, 0.12f), "RingInner"));
            _head = new Node3D { Name = "Head", Position = new Vector3(0, 9999.0f, 0) };
            AddChild(_head);
            return;
        }

        _torso = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.55f, 0.9f, 0.12f) },
            MaterialOverride = _torsoMaterial,
            Position = new Vector3(0, 1.0f, 0),
        };
        AddChild(_torso);
        _bodyShape = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.55f, 0.9f, 0.12f) }, Position = _torso.Position };
        AddChild(_bodyShape);

        _head = new Node3D { Name = "Head", Position = new Vector3(0, 1.62f, 0) };
        _head.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.17f, Height = 0.34f }, MaterialOverride = _torsoMaterial });
        AddChild(_head);
        _headShape = new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.17f }, Position = _head.Position };
        AddChild(_headShape);

        AddChild(TrainingUtil.Box(new Vector3(0.06f, 0.55f, 0.06f), new Color(0.3f, 0.3f, 0.3f), new Vector3(0, 0.27f, -0.05f), "Post"));
    }

    public bool IsHeadHit(Vector3 worldPoint) => !IsWall && worldPoint.DistanceTo(_head.GlobalPosition) <= 0.22f;

    public void ReceiveShot(ShotInfo shot)
    {
        if (IsDown) return;
        HitCount++;
        float before = Hp;
        if (!Indestructible) Hp = Mathf.Max(0.0f, Hp - shot.Damage);
        bool killed = !Indestructible && !NoShoot && Hp <= 0.0f;

        if (!NoShoot && !_reactionLogged)
        {
            _reactionLogged = true;
            TrainingSession.Instance?.Stats.ReactionTimes.Add(_age);
        }
        TrainingSession.Instance?.RegisterHit(this, shot, killed, NoShoot, before - Hp);
        _flash = 0.12f;
        Hit?.Invoke(this, shot);

        if (!killed) return;
        IsDown = true;
        CollisionLayer = 0;
        Killed?.Invoke(this, shot);
        Visible = false;
        QueueFree();
    }

    public override void _PhysicsProcess(double deltaValue)
    {
        float dt = (float)deltaValue;
        TrainingSession? session = TrainingSession.Instance;
        if (session != null && !session.DrillLive) return;

        _age += dt;
        if (Lifetime > 0.0f && _age >= Lifetime && !IsDown)
        {
            IsDown = true;
            Expired?.Invoke(this);
            QueueFree();
            return;
        }

        switch (Movement)
        {
            case MoveMode.Strafe:
            {
                float omega = Mathf.Tau / Mathf.Max(0.1f, Period);
                Position = Anchor + Axis.Normalized() * (Amplitude * Mathf.Sin(_age * omega + PhaseOffset));
                break;
            }
            case MoveMode.Line:
            {
                Position = Position.MoveToward(LineTo, LineSpeed * dt);
                break;
            }
        }

        if (_flash > 0.0f)
        {
            _flash -= dt;
            _torsoMaterial.AlbedoColor = _flash > 0.0f ? Colors.White : _baseColor;
        }
    }
}
