using Godot;
using System;

/// <summary>A wall a Demolisher charge can be planted on and blown open. Stand-in for the map's breachable walls:
/// your real breach objects only need IPlantable + IExplosionReceiver (see Ports.cs) to work with ExplosiveSystem.</summary>
public partial class BreachWall : StaticBody3D, IPlantable, IExplosionReceiver
{
    public Vector3 Size { get; set; } = new Vector3(5.0f, 3.0f, 0.5f);
    public float BreachThreshold { get; set; } = 25.0f;   // blast damage needed to open it
    public bool IsBreached { get; private set; }
    public event Action<BreachWall>? Breached;

    public string DisplayName => "Breachable wall";
    public Vector3 BlastPosition => GlobalPosition;

    private MeshInstance3D _mesh = null!;
    private CollisionShape3D _shape = null!;
    private Label3D _label = null!;

    public override void _Ready()
    {
        CollisionLayer = 1;
        CollisionMask = 0;
        AddToGroup("blast_receiver");
        _mesh = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Size },
            MaterialOverride = TrainingUtil.Mat(new Color(0.55f, 0.42f, 0.3f)),
            Position = new Vector3(0, Size.Y * 0.5f, 0),
        };
        AddChild(_mesh);
        _shape = new CollisionShape3D { Shape = new BoxShape3D { Size = Size }, Position = _mesh.Position };
        AddChild(_shape);
        _label = TrainingUtil.Label("BREACHABLE\nplant a charge", new Vector3(0, Size.Y + 0.6f, 0), new Color(1.0f, 0.75f, 0.3f), 32, true);
        AddChild(_label);
    }

    public void ReceiveBlast(float damage, Vector3 origin, long sourcePeerId)
    {
        if (IsBreached || damage < BreachThreshold) return;
        IsBreached = true;
        _mesh.Visible = false;
        _shape.Disabled = true;
        CollisionLayer = 0;
        _label.Text = "BREACHED";
        _label.Modulate = new Color(0.5f, 1.0f, 0.6f);
        Breached?.Invoke(this);
    }
}
