#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace WraithRun.MapKit;

public readonly struct ScatterInstance
{
    public readonly Transform3D Transform;
    public readonly PropDefinition Prop;
    public ScatterInstance(Transform3D t, PropDefinition prop) { Transform = t; Prop = prop; }
}

/// <summary>
/// Places props over an area without overlap, and emits them the fast way: primitives merge straight
/// into a MeshBatcher (zero extra draw calls), and anything with a real mesh becomes one
/// MultiMeshInstance3D per prop type (one draw call for every copy of that prop, however many there are).
/// This -- batching + MultiMesh instead of one node per object -- is what keeps a debris-covered
/// wasteland cheap on a weak iGPU.
/// </summary>
public static class ScatterField
{
    /// <summary>
    /// Deterministic dart-throwing (rejection sampling) placement. Good enough for debris/rubble/foliage;
    /// not true Poisson-disc, but even and seed-stable, which is what repeatable map baking needs.
    /// heightAt/normalAt let placement follow uneven ground; insideMask excludes roads, trenches, water...
    /// </summary>
    public static List<ScatterInstance> Generate(ScatterLayer layer, Rect2 areaXZ,
        Func<Vector2, bool> insideMask, Func<Vector2, float> heightAt, Func<Vector2, Vector3> normalAt,
        int maxAttemptsMultiplier = 12)
    {
        var result = new List<ScatterInstance>();
        if (layer.Entries.Length == 0 || layer.DensityPerSqm <= 0.0f) return result;

        float area = Mathf.Max(areaXZ.Size.X * areaXZ.Size.Y, 0.0f);
        int target = Mathf.CeilToInt(area * layer.DensityPerSqm);
        if (target <= 0) return result;

        var rng = new RandomNumberGenerator();
        rng.Seed = layer.Seed;

        float cell = Mathf.Max(0.5f, ExpectedSpacing(layer));
        var grid = new Dictionary<(int, int), List<Vector2>>();

        void Add(Vector2 p)
        {
            var key = (Mathf.FloorToInt(p.X / cell), Mathf.FloorToInt(p.Y / cell));
            if (!grid.TryGetValue(key, out List<Vector2>? list))
            {
                list = new List<Vector2>();
                grid[key] = list;
            }
            list.Add(p);
        }

        bool TooClose(Vector2 p, float minDist)
        {
            int cx = Mathf.FloorToInt(p.X / cell);
            int cy = Mathf.FloorToInt(p.Y / cell);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy), out List<Vector2>? list)) continue;
                    foreach (Vector2 q in list)
                    {
                        if (p.DistanceSquaredTo(q) < minDist * minDist) return true;
                    }
                }
            }
            return false;
        }

        int maxAttempts = target * Mathf.Max(1, maxAttemptsMultiplier);
        int placed = 0;
        for (int attempt = 0; attempt < maxAttempts && placed < target; attempt++)
        {
            var p = new Vector2(
                rng.RandfRange(areaXZ.Position.X, areaXZ.Position.X + areaXZ.Size.X),
                rng.RandfRange(areaXZ.Position.Y, areaXZ.Position.Y + areaXZ.Size.Y));

            if (!insideMask(p)) continue;

            PropDefinition? prop = layer.Pick(rng);
            if (prop == null) continue;

            float minDist = prop.Footprint + layer.ExtraSpacing;
            if (TooClose(p, minDist)) continue;

            Add(p);
            placed++;

            float y = heightAt(p) - prop.SinkIntoGround;
            var origin = new Vector3(p.X, y, p.Y);

            Basis basis = Basis.Identity;
            if (prop.AlignToGroundNormal)
            {
                Vector3 n = normalAt(p);
                basis = AlignUpTo(n);
            }
            if (prop.RandomYaw)
            {
                basis = basis.Rotated(Vector3.Up, rng.RandfRange(0.0f, Mathf.Tau));
            }
            if (prop.UniformScaleJitter > 0.0f)
            {
                float s = 1.0f + rng.RandfRange(-prop.UniformScaleJitter, prop.UniformScaleJitter);
                basis = basis.Scaled(new Vector3(s, s, s));
            }

            result.Add(new ScatterInstance(new Transform3D(basis, origin), prop));
        }

        return result;
    }

    /// <summary>
    /// Regular grid placement (not random) filling 'areaXZ' with copies of one prop, all facing the
    /// same way. Used for furniture rows — desks, display cases, benches — where the point is order,
    /// not organic scatter.
    /// </summary>
    public static List<ScatterInstance> GenerateGrid(PropDefinition prop, Rect2 areaXZ, float spacing,
        float yawRadians, Func<Vector2, float> heightAt)
    {
        var result = new List<ScatterInstance>();
        if (prop == null || spacing <= 0.01f || areaXZ.Size.X <= 0.0f || areaXZ.Size.Y <= 0.0f) return result;

        int cols = Mathf.Max(1, Mathf.FloorToInt(areaXZ.Size.X / spacing) + 1);
        int rows = Mathf.Max(1, Mathf.FloorToInt(areaXZ.Size.Y / spacing) + 1);
        // Centre the grid within the area rather than hugging the min corner.
        float usedW = (cols - 1) * spacing;
        float usedD = (rows - 1) * spacing;
        Vector2 start = areaXZ.Position + (areaXZ.Size - new Vector2(usedW, usedD)) * 0.5f;

        Basis basis = Basis.Identity.Rotated(Vector3.Up, yawRadians);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var p = start + new Vector2(c * spacing, r * spacing);
                float y = heightAt(p) - prop.SinkIntoGround;
                result.Add(new ScatterInstance(new Transform3D(basis, new Vector3(p.X, y, p.Y)), prop));
            }
        }
        return result;
    }

    private static float ExpectedSpacing(ScatterLayer layer)
    {
        float max = 1.0f;
        foreach (ScatterEntry e in layer.Entries)
        {
            if (e.Prop != null) max = Mathf.Max(max, e.Prop.Footprint);
        }
        return max + layer.ExtraSpacing;
    }

    private static Basis AlignUpTo(Vector3 normal)
    {
        normal = normal.Normalized();
        Vector3 tangent = Mathf.Abs(normal.Y) < 0.99f ? Vector3.Up.Cross(normal) : Vector3.Right.Cross(normal);
        tangent = tangent.Normalized();
        Vector3 binormal = normal.Cross(tangent).Normalized();
        return new Basis(tangent, normal, binormal);
    }

    /// <summary>
    /// Emits every instance. Primitives merge into 'batch' (visual + collision, zero extra draw calls).
    /// Everything else becomes one MultiMeshInstance3D per prop type under 'visualParent', with a merged
    /// approximate box collider per type under 'collisionParent' when the prop asks for collision.
    /// </summary>
    public static void Emit(List<ScatterInstance> instances, MeshBatcher batch, Node3D visualParent,
        Node3D collisionParent, Node owner, string genFolder, string prefix)
    {
        var byProp = new Dictionary<PropDefinition, List<Transform3D>>();
        foreach (ScatterInstance inst in instances)
        {
            if (inst.Prop.HasArt)
            {
                if (!byProp.TryGetValue(inst.Prop, out List<Transform3D>? list))
                {
                    list = new List<Transform3D>();
                    byProp[inst.Prop] = list;
                }
                list.Add(inst.Transform);
            }
            else
            {
                inst.Prop.AddPrimitiveTo(batch, inst.Transform);
            }
        }

        int index = 0;
        foreach ((PropDefinition prop, List<Transform3D> transforms) in byProp)
        {
            EmitMultiMesh(prop, transforms, visualParent, collisionParent, owner, genFolder, $"{prefix}_{index}");
            index++;
        }
    }

    private static void EmitMultiMesh(PropDefinition prop, List<Transform3D> transforms, Node3D visualParent,
        Node3D collisionParent, Node owner, string genFolder, string name)
    {
        if (prop.Scene == null) return;
        Node instance = prop.Scene.Instantiate();
        Mesh? mesh = FindFirstMesh(instance, out Vector3 meshOffset, out Aabb aabb);
        instance.QueueFree();

        if (mesh == null)
        {
            MapKitUtil.Warn($"Prop '{prop.ResourcePath}' has a Scene but no MeshInstance3D was found in it; skipping {transforms.Count} instance(s).");
            return;
        }

        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = transforms.Count,
        };
        for (int i = 0; i < transforms.Count; i++)
        {
            mm.SetInstanceTransform(i, transforms[i] * new Transform3D(Basis.Identity, meshOffset));
        }
        MapKitUtil.SaveExternal(mm, genFolder, "mm_" + name);

        var mmi = new MultiMeshInstance3D { Multimesh = mm };
        MapKitUtil.AddOwned(visualParent, mmi, name, owner);

        if (!prop.Collide) return;
        Vector3 half = aabb.Size * 0.5f;
        if (half.X <= 0.0f || half.Y <= 0.0f || half.Z <= 0.0f) return;
        var body = MapKitUtil.AddOwned(collisionParent, new StaticBody3D(), name + "_Collision", owner);
        var box = new BoxShape3D { Size = aabb.Size };
        for (int i = 0; i < transforms.Count; i++)
        {
            Transform3D t = transforms[i] * new Transform3D(Basis.Identity, meshOffset + aabb.GetCenter());
            var cs = new CollisionShape3D { Shape = box, Transform = t };
            MapKitUtil.AddOwned(body, cs, name + "_c" + i, owner);
        }
    }

    private static Mesh? FindFirstMesh(Node node, out Vector3 offset, out Aabb aabb)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            offset = mi.Position;
            aabb = mi.Mesh.GetAabb();
            return mi.Mesh;
        }
        foreach (Node child in node.GetChildren())
        {
            Mesh? found = FindFirstMesh(child, out offset, out aabb);
            if (found != null) return found;
        }
        offset = Vector3.Zero;
        aabb = new Aabb();
        return null;
    }
}
