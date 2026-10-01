using Godot;
using System.Collections.Generic;

[GlobalClass]
public partial class TrainingZone : Node3D
{
    [Export] public string ZoneName { get; set; } = "ZONE";
    /// <summary>Half extents on X/Z in metres.</summary>
    [Export] public Vector2 HalfSize { get; set; } = new Vector2(18.0f, 18.0f);
    [Export] public Color ZoneColor { get; set; } = new Color(0.4f, 0.7f, 1.0f);

    public override void _Ready() => AddToGroup(TrainingAnchors.ZoneGroup);

    public bool Contains(Vector3 world)
    {
        Vector3 local = GlobalTransform.AffineInverse() * world;
        return Mathf.Abs(local.X) <= HalfSize.X && Mathf.Abs(local.Z) <= HalfSize.Y;
    }
}
