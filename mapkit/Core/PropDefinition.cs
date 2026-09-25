#nullable enable
using Godot;

namespace WraithRun.MapKit;

public enum PrimitiveShape
{
    Box,
    Cylinder,
    /// <summary>Flat-topped, tapered — sandbag/barrier silhouette.</summary>
    Barrier,
}

/// <summary>
/// One kind of placeable object: a crate, a barrier, a desk, a display case, a tree. Point it at a
/// PackedScene once you have real art (Hunyuan 3D output, Mixamo-style asset, etc.). Until then,
/// leave Scene empty and it builds a plain primitive shape instead, so maps are playable and readable
/// from day one and swap to real meshes later without changing any map layout.
/// </summary>
[Tool]
[GlobalClass]
public partial class PropDefinition : Resource
{
    [Export] public PackedScene? Scene { get; set; }

    [Export] public PrimitiveShape Primitive { get; set; } = PrimitiveShape.Box;
    [Export] public SurfaceLayer? PrimitiveSurface { get; set; }
    [Export] public Vector3 PrimitiveSize { get; set; } = new(1.0f, 1.0f, 1.0f);

    /// <summary>Roughly how much floor space this needs, for scatter spacing. Metres.</summary>
    [Export(PropertyHint.Range, "0.1,10,0.05,or_greater")]
    public float Footprint { get; set; } = 1.0f;

    [Export] public bool RandomYaw { get; set; } = true;
    [Export(PropertyHint.Range, "0,1,0.01")] public float UniformScaleJitter { get; set; }
    [Export] public bool AlignToGroundNormal { get; set; }

    /// <summary>Shifts the prop down into the ground slightly so its base never floats on uneven terrain.</summary>
    [Export(PropertyHint.Range, "-1,1,0.01")] public float SinkIntoGround { get; set; }

    [Export] public bool Collide { get; set; } = true;

    public bool HasArt => Scene != null;

    /// <summary>Builds a merge-ready primitive mesh into a batcher (used when Scene is empty).</summary>
    public void AddPrimitiveTo(MeshBatcher batch, Transform3D t)
    {
        object mat = PrimitiveSurface != null ? PrimitiveSurface : "prop_default";
        Vector3 s = PrimitiveSize;
        switch (Primitive)
        {
            case PrimitiveShape.Cylinder:
                AddCylinder(batch, mat, t, s.X * 0.5f, s.Y, 10);
                break;
            case PrimitiveShape.Barrier:
                AddBarrier(batch, mat, t, s);
                break;
            default:
                batch.AddBox(mat, t * new Transform3D(Basis.Identity, new Vector3(0.0f, s.Y * 0.5f, 0.0f)), s, collide: Collide);
                break;
        }
    }

    private static void AddCylinder(MeshBatcher batch, object mat, Transform3D t, float radius, float height, int sides)
    {
        for (int i = 0; i < sides; i++)
        {
            float a0 = Mathf.Tau * i / sides;
            float a1 = Mathf.Tau * (i + 1) / sides;
            Vector3 p0 = new(Mathf.Cos(a0) * radius, 0.0f, Mathf.Sin(a0) * radius);
            Vector3 p1 = new(Mathf.Cos(a1) * radius, 0.0f, Mathf.Sin(a1) * radius);
            Vector3 n = (p0 + p1).Normalized();

            batch.AddFlatQuad(mat, t * p0, t * p1, t * (p1 + Vector3.Up * height), t * (p0 + Vector3.Up * height),
                (t.Basis.Inverse().Transposed() * n).Normalized(), true);

            // Caps.
            batch.AddTriangle(mat,
                new BatchVertex(t * Vector3.Zero, t.Basis * Vector3.Down, Vector2.Zero, Colors.White),
                new BatchVertex(t * p1, t.Basis * Vector3.Down, Vector2.One, Colors.White),
                new BatchVertex(t * p0, t.Basis * Vector3.Down, Vector2.One, Colors.White), true);
            Vector3 top = Vector3.Up * height;
            batch.AddTriangle(mat,
                new BatchVertex(t * (top), t.Basis * Vector3.Up, Vector2.Zero, Colors.White),
                new BatchVertex(t * (p0 + top), t.Basis * Vector3.Up, Vector2.One, Colors.White),
                new BatchVertex(t * (p1 + top), t.Basis * Vector3.Up, Vector2.One, Colors.White), true);
        }
    }

    /// <summary>Tapered box — a believable sandbag/concrete-barrier silhouette from six quads.</summary>
    private static void AddBarrier(MeshBatcher batch, object mat, Transform3D t, Vector3 s)
    {
        float taper = 0.7f;
        Vector3 hx = new(s.X * 0.5f, 0.0f, 0.0f);
        Vector3 hz = new(0.0f, 0.0f, s.Z * 0.5f);
        Vector3 hxTop = hx * taper;
        Vector3 hzTop = hz * taper;
        Vector3 up = Vector3.Up * s.Y;

        Vector3[] bottom = { -hx - hz, hx - hz, hx + hz, -hx + hz };
        Vector3[] top = { up - hxTop - hzTop, up + hxTop - hzTop, up + hxTop + hzTop, up - hxTop + hzTop };

        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            Vector3 outward = ((bottom[i] + bottom[j]) * 0.5f).Normalized();
            batch.AddFlatQuad(mat, t * bottom[i], t * bottom[j], t * top[j], t * top[i],
                (t.Basis.Inverse().Transposed() * outward).Normalized(), true);
        }
        batch.AddFlatQuad(mat, t * top[0], t * top[1], t * top[2], t * top[3], t.Basis * Vector3.Up, true);
    }
}
