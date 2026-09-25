#nullable enable
using System.Collections.Generic;
using Godot;

namespace WraithRun.MapKit;

public static class MapKitUtil
{
    public const string GeneratedNodeName = "Generated";
    public const string SpawnGroup = "spawn_point"; // GameWorld.cs looks for this group

    public static void Log(string message) => GD.Print($"[MapKit] {message}");
    public static void Warn(string message) => GD.PushWarning($"[MapKit] {message}");

    /// <summary>The node that must own generated children so they are written into the saved scene.</summary>
    public static Node GetOwnerRoot(Node node)
    {
        Node? root = null;
        if (Engine.IsEditorHint() && node.IsInsideTree())
        {
            root = node.GetTree().EditedSceneRoot;
        }
        return root ?? node.Owner ?? node;
    }

    public static void SetOwnerRecursive(Node node, Node owner)
    {
        node.Owner = owner;
        foreach (Node child in node.GetChildren())
        {
            SetOwnerRecursive(child, owner);
        }
    }

    /// <summary>Deletes the previous "Generated" child (if any) and returns a fresh empty one, owned by the scene.</summary>
    public static Node3D ResetGenerated(Node3D builder, Node owner)
    {
        Node? old = builder.GetNodeOrNull(GeneratedNodeName);
        if (old != null)
        {
            builder.RemoveChild(old);
            old.QueueFree();
        }
        var generated = new Node3D { Name = GeneratedNodeName };
        builder.AddChild(generated);
        generated.Owner = owner;
        return generated;
    }

    public static void ClearGenerated(Node3D builder)
    {
        Node? old = builder.GetNodeOrNull(GeneratedNodeName);
        if (old == null) return;
        builder.RemoveChild(old);
        old.QueueFree();
    }

    /// <summary>Adds a child node and marks it (not its children) as owned by the scene.</summary>
    public static T AddOwned<T>(Node parent, T child, string name, Node owner) where T : Node
    {
        child.Name = name;
        parent.AddChild(child);
        child.Owner = owner;
        return child;
    }

    // ------------------------------------------------------------ external resource files

    /// <summary>"res://scenes/maps/Foo.tscn" -> "res://scenes/maps/Foo.gen/". Empty when the scene was never saved.</summary>
    public static string GenFolderFor(Node ownerRoot)
    {
        string scenePath = ownerRoot.SceneFilePath;
        if (string.IsNullOrEmpty(scenePath)) return "";
        int dot = scenePath.LastIndexOf('.');
        string stem = dot > 0 ? scenePath.Substring(0, dot) : scenePath;
        return stem + ".gen/";
    }

    /// <summary>Creates the folder and clears .res files left by the previous bake.</summary>
    public static void PrepareGenFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        DirAccess.MakeDirRecursiveAbsolute(folder);
        using DirAccess? dir = DirAccess.Open(folder);
        if (dir == null) return;
        foreach (string file in dir.GetFiles())
        {
            if (file.EndsWith(".res")) dir.Remove(file);
        }
    }

    /// <summary>
    /// Saves a big resource (mesh, collision shape) as a binary .res next to the scene, so the .tscn
    /// stays small and diffable. Falls back to leaving it embedded when the scene has no path yet.
    /// </summary>
    public static void SaveExternal(Resource resource, string folder, string name)
    {
        if (string.IsNullOrEmpty(folder)) return;
        string path = folder + name + ".res";
        Error err = ResourceSaver.Save(resource, path);
        if (err != Error.Ok)
        {
            Warn($"Could not save {path} ({err}); the resource stays embedded in the scene.");
            return;
        }
        resource.TakeOverPath(path);
    }

    public static string SafeName(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
        }
        return new string(chars);
    }

    // ------------------------------------------------------------ shared node builders

    /// <summary>Turns a MeshBatcher's output into MeshInstance3D nodes + one StaticBody3D of chunk collision.</summary>
    public static void EmitBatch(MeshBatcher batch, System.Func<object, Material?> resolveMaterial,
        Node3D visualParent, Node3D collisionParent, Node owner, string genFolder, string prefix,
        bool castShadows, float visibleRange, bool collision = true)
    {
        foreach (MeshBatcher.BuiltMesh built in batch.Build())
        {
            string name = $"{prefix}_{built.Chunk.X}_{built.Chunk.Y}_{SafeName(built.Material.ToString() ?? "mat")}";
            SaveExternal(built.Mesh, genFolder, "mesh_" + name);

            var mi = new MeshInstance3D { Mesh = built.Mesh };
            Material? material = resolveMaterial(built.Material);
            if (material != null) mi.MaterialOverride = material;
            mi.CastShadow = castShadows
                ? GeometryInstance3D.ShadowCastingSetting.On
                : GeometryInstance3D.ShadowCastingSetting.Off;
            if (visibleRange > 0.0f)
            {
                mi.VisibilityRangeEnd = visibleRange;
                mi.VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
            }
            AddOwned(visualParent, mi, name, owner);
        }

        if (!collision) return;
        var body = AddOwned(collisionParent, new StaticBody3D(), prefix + "_Collision", owner);
        foreach ((Vector2I chunk, ConcavePolygonShape3D shape) in batch.BuildCollision())
        {
            string name = $"{prefix}_col_{chunk.X}_{chunk.Y}";
            SaveExternal(shape, genFolder, "shape_" + name);
            AddOwned(body, new CollisionShape3D { Shape = shape }, name, owner);
        }
    }

    /// <summary>Spawn marker in the group GameWorld.cs uses. yaw makes the marker's -Z axis (forward) point at 'lookAt'.</summary>
    public static Marker3D AddSpawn(Node parent, string name, Vector3 position, Vector3 lookAt, Node owner)
    {
        var marker = new Marker3D();
        Vector3 dir = lookAt - position;
        dir.Y = 0.0f;
        float yaw = dir.LengthSquared() > 0.0001f ? Mathf.Atan2(-dir.X, -dir.Z) : 0.0f;
        marker.Rotation = new Vector3(0.0f, yaw, 0.0f);
        marker.Position = position;
        AddOwned(parent, marker, name, owner);
        marker.AddToGroup(SpawnGroup, true);
        return marker;
    }

    public static float SmoothStep(float edge0, float edge1, float x)
    {
        if (Mathf.IsEqualApprox(edge0, edge1)) return x < edge0 ? 0.0f : 1.0f;
        float t = Mathf.Clamp((x - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}

/// <summary>Distance/height helpers over a 2D polyline (used by roads and trenches).</summary>
public sealed class Polyline2D
{
    public readonly List<Vector2> Points = new();
    public readonly List<float> Heights = new();
    public readonly List<float> Cumulative = new();

    public int Count => Points.Count;
    public float Length => Cumulative.Count > 0 ? Cumulative[Cumulative.Count - 1] : 0.0f;

    public void Add(Vector2 xz, float height)
    {
        if (Points.Count == 0) Cumulative.Add(0.0f);
        else Cumulative.Add(Cumulative[Cumulative.Count - 1] + Points[Points.Count - 1].DistanceTo(xz));
        Points.Add(xz);
        Heights.Add(height);
    }

    /// <summary>Nearest point on the line: horizontal distance, interpolated height and distance along the line.</summary>
    public void Nearest(Vector2 p, out float distance, out float height, out float along)
    {
        distance = float.MaxValue;
        height = 0.0f;
        along = 0.0f;
        for (int i = 0; i + 1 < Points.Count; i++)
        {
            Vector2 a = Points[i];
            Vector2 ab = Points[i + 1] - a;
            float len2 = ab.LengthSquared();
            float t = len2 > 1e-8f ? Mathf.Clamp((p - a).Dot(ab) / len2, 0.0f, 1.0f) : 0.0f;
            float d = p.DistanceTo(a + ab * t);
            if (d < distance)
            {
                distance = d;
                height = Mathf.Lerp(Heights[i], Heights[i + 1], t);
                along = Mathf.Lerp(Cumulative[i], Cumulative[i + 1], t);
            }
        }
    }
}
