using Godot;
using System;
using System.Collections.Generic;

/// <summary>A smoke cloud: blocks enemy line of sight through it for its lifetime (10 s, 4 m radius baseline).</summary>
public partial class SmokeCloud : Node3D
{
    public float Radius { get; set; } = TrainingKit.SmokeRadius;
    public float Lifetime { get; set; } = TrainingKit.SmokeSeconds;
    private float _age;

    public override void _Ready()
    {
        AddToGroup("smoke_cloud");
        AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = Radius, Height = Radius * 2.0f, RadialSegments = 24, Rings = 12 },
            MaterialOverride = TrainingUtil.Mat(new Color(0.75f, 0.75f, 0.78f), true, 0.72f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;
        if (_age >= Lifetime) QueueFree();
    }
}

public static class TrainingSmoke
{
    /// <summary>True if the line a->b passes through any live smoke cloud.</summary>
    public static bool Blocks(SceneTree tree, Vector3 a, Vector3 b)
    {
        foreach (Node node in tree.GetNodesInGroup("smoke_cloud"))
            if (node is SmokeCloud cloud && TrainingUtil.SegmentHitsSphere(a, b, cloud.GlobalPosition, cloud.Radius)) return true;
        return false;
    }
}
