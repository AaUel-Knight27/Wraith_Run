using Godot;

/// <summary>Small shared helpers for drills that build props in an anchor's local space.</summary>
public static class DrillHelpers
{
    public static float YawDegrees(Transform3D anchor) => Mathf.RadToDeg(anchor.Basis.GetEuler().Y);

    /// <summary>A traffic cone. Purely visual - drills detect "hits" by distance so nothing physically snags the car.</summary>
    public static MeshInstance3D Cone(Color color)
    {
        return new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.4f, Height = 0.9f },
            MaterialOverride = TrainingUtil.Mat(color),
            Position = Vector3.Zero,
        };
    }

    /// <summary>Puts a node into the session's Props at anchor*local (facing along the anchor's yaw).</summary>
    public static T Place<T>(TrainingSession session, Transform3D anchor, T node, Vector3 local, float extraLift = 0.0f) where T : Node3D
    {
        session.Props.AddChild(node);
        node.GlobalTransform = new Transform3D(anchor.Basis, anchor * local + new Vector3(0, extraLift, 0));
        return node;
    }
}
