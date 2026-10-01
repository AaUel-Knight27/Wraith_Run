using Godot;
using System.Collections.Generic;

/// <summary>Small helpers shared by every training script (kept dependency-free on purpose).</summary>
public static class TrainingUtil
{
    public static Vector3 Flat(Vector3 v) => new Vector3(v.X, 0.0f, v.Z);

    public static float FlatDistance(Vector3 a, Vector3 b) => Flat(a - b).Length();

    /// <summary>Yaw (radians about Y) that makes a node's -Z axis point along dir (Godot forward).</summary>
    public static float YawFacing(Vector3 dir)
    {
        Vector3 flat = Flat(dir);
        if (flat.LengthSquared() < 0.0001f) return 0.0f;
        return Mathf.Atan2(-flat.X, -flat.Z);
    }

    public static string Clock(float seconds)
    {
        seconds = Mathf.Max(0.0f, seconds);
        int whole = Mathf.FloorToInt(seconds);
        int tenths = Mathf.FloorToInt((seconds - whole) * 10.0f);
        return $"{whole / 60}:{whole % 60:00}.{tenths}";
    }

    public static StandardMaterial3D Mat(Color color, bool unshaded = false, float alpha = 1.0f)
    {
        var mat = new StandardMaterial3D { AlbedoColor = new Color(color.R, color.G, color.B, alpha) };
        if (unshaded) mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        if (alpha < 0.999f) mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        return mat;
    }

    public static MeshInstance3D Box(Vector3 size, Color color, Vector3 position, string name = "Box")
    {
        return new MeshInstance3D
        {
            Name = name,
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = Mat(color),
            Position = position,
        };
    }

    /// <summary>A solid box the player/bullets collide with (visual + StaticBody3D). Layer 1 like the rest of the world.</summary>
    public static StaticBody3D SolidBox(Vector3 size, Color color, Vector3 position, string name = "Wall")
    {
        var body = new StaticBody3D { Name = name, Position = position };
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = Mat(color) });
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        return body;
    }

    public static Label3D Label(string text, Vector3 position, Color? color = null, int fontSize = 48, bool alwaysVisible = false)
    {
        return new Label3D
        {
            Text = text,
            Position = position,
            FontSize = fontSize,
            PixelSize = 0.01f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = color ?? Colors.White,
            OutlineSize = 10,
            OutlineModulate = new Color(0, 0, 0, 0.9f),
            NoDepthTest = alwaysVisible,
        };
    }

    /// <summary>A flat glowing disc on the ground (zones, blast radius, safe areas). Purely visual.</summary>
    public static MeshInstance3D GroundDisc(float radius, Color color, Vector3 position, float alpha = 0.35f)
    {
        return new MeshInstance3D
        {
            Name = "GroundDisc",
            Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.04f, RadialSegments = 48 },
            MaterialOverride = Mat(color, true, alpha),
            Position = position + new Vector3(0, 0.03f, 0),
        };
    }

    public static void SafeFree(Node? node)
    {
        if (node != null && GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion()) node.QueueFree();
    }

    public static void ClearChildren(Node parent)
    {
        foreach (Node child in parent.GetChildren()) child.QueueFree();
    }

    /// <summary>World-space bounds of every MeshInstance3D under root (8 corners each, so rotation/negative scale are handled).</summary>
    public static bool TryGetWorldAabb(Node3D root, out Aabb bounds)
    {
        bounds = new Aabb();
        bool any = false;
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            foreach (Node child in node.GetChildren()) stack.Push(child);
            if (node is not MeshInstance3D mesh || mesh.Mesh == null) continue;
            Aabb local = mesh.GetAabb();
            Transform3D xf = mesh.GlobalTransform;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    local.Position.X + ((i & 1) != 0 ? local.Size.X : 0.0f),
                    local.Position.Y + ((i & 2) != 0 ? local.Size.Y : 0.0f),
                    local.Position.Z + ((i & 4) != 0 ? local.Size.Z : 0.0f));
                Vector3 world = xf * corner;
                if (!any) { bounds = new Aabb(world, Vector3.Zero); any = true; }
                else bounds = bounds.Expand(world);
            }
        }
        return any;
    }

    /// <summary>Does the segment a->b pass through the sphere (centre, radius)? Used for smoke line-of-sight.</summary>
    public static bool SegmentHitsSphere(Vector3 a, Vector3 b, Vector3 centre, float radius)
    {
        Vector3 ab = b - a;
        float lenSq = ab.LengthSquared();
        float t = lenSq < 0.0001f ? 0.0f : Mathf.Clamp((centre - a).Dot(ab) / lenSq, 0.0f, 1.0f);
        Vector3 closest = a + ab * t;
        return closest.DistanceSquaredTo(centre) <= radius * radius;
    }
}
