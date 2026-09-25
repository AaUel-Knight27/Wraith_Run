#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace WraithRun.MapKit;

public struct BatchVertex
{
    public Vector3 P;
    public Vector3 N;
    public Vector2 UV;
    public Color C;

    public BatchVertex(Vector3 p, Vector3 n, Vector2 uv, Color c)
    {
        P = p; N = n; UV = uv; C = c;
    }
}

[Flags]
public enum BoxFaces
{
    None = 0,
    PosX = 1, NegX = 2, PosY = 4, NegY = 8, PosZ = 16, NegZ = 32,
    Sides = PosX | NegX | PosZ | NegZ,
    All = PosX | NegX | PosY | NegY | PosZ | NegZ,
}

/// <summary>
/// Collects triangles and emits one ArrayMesh per (chunk, material). This is the kit's main
/// performance tool: hundreds of separate props become a few chunk meshes, which on a weak CPU/iGPU
/// (draw calls are the bottleneck, not triangles) is the single biggest win.
/// Collision triangles are merged per chunk into one ConcavePolygonShape3D.
///
/// Winding note: Godot treats CLOCKWISE triangles as front faces. Every Add* method takes the
/// intended outward normal and fixes the winding itself, so callers never have to think about it.
/// </summary>
public sealed class MeshBatcher
{
    public sealed class Bucket
    {
        public Vector2I Chunk;
        public object Material = null!;
        public readonly List<Vector3> Verts = new();
        public readonly List<Vector3> Normals = new();
        public readonly List<Vector2> UVs = new();
        public readonly List<Color> Colors = new();
        public bool HasColor;
        public int TriangleCount => Verts.Count / 3;
    }

    public readonly struct BuiltMesh
    {
        public readonly Vector2I Chunk;
        public readonly object Material;
        public readonly ArrayMesh Mesh;
        public BuiltMesh(Vector2I chunk, object material, ArrayMesh mesh)
        {
            Chunk = chunk; Material = material; Mesh = mesh;
        }
    }

    private readonly Dictionary<(int, int, object), Bucket> _buckets = new();
    private readonly Dictionary<Vector2I, List<Vector3>> _collision = new();

    /// <summary>Chunk edge length in metres. 0 or less puts everything in one chunk.</summary>
    public float ChunkSize { get; }

    public int TotalTriangles { get; private set; }
    public int BucketCount => _buckets.Count;

    public MeshBatcher(float chunkSize)
    {
        ChunkSize = chunkSize;
    }

    // ---------------------------------------------------------------- UV helpers

    /// <summary>World-aligned planar projection: walls/floors keep a constant texel density and line up across pieces.</summary>
    public static Vector2 ProjectUV(Vector3 p, Vector3 n)
    {
        Vector3 a = n.Abs();
        if (a.X >= a.Y && a.X >= a.Z) return new Vector2(p.Z, -p.Y);
        if (a.Z >= a.Y) return new Vector2(p.X, -p.Y);
        return new Vector2(p.X, p.Z);
    }

    // ---------------------------------------------------------------- core adds

    private Vector2I ChunkOf(Vector3 p)
    {
        if (ChunkSize <= 0.0f) return Vector2I.Zero;
        return new Vector2I(Mathf.FloorToInt(p.X / ChunkSize), Mathf.FloorToInt(p.Z / ChunkSize));
    }

    private Bucket GetBucket(Vector2I chunk, object material)
    {
        var key = (chunk.X, chunk.Y, material);
        if (!_buckets.TryGetValue(key, out Bucket? b))
        {
            b = new Bucket { Chunk = chunk, Material = material };
            _buckets[key] = b;
        }
        return b;
    }

    public void AddTriangle(object material, BatchVertex a, BatchVertex b, BatchVertex c, bool collide)
    {
        // Godot front face = clockwise: the front normal is (c-a) x (b-a).
        Vector3 geometric = (c.P - a.P).Cross(b.P - a.P);
        if (geometric.LengthSquared() < 1e-12f) return; // degenerate

        Vector3 wanted = a.N + b.N + c.N;
        if (wanted.LengthSquared() > 1e-8f && geometric.Dot(wanted) < 0.0f)
        {
            (b, c) = (c, b);
        }

        Vector2I chunk = ChunkOf((a.P + b.P + c.P) / 3.0f);
        Bucket bucket = GetBucket(chunk, material);
        foreach (BatchVertex v in new[] { a, b, c })
        {
            bucket.Verts.Add(v.P);
            bucket.Normals.Add(v.N);
            bucket.UVs.Add(v.UV);
            bucket.Colors.Add(v.C);
            if (v.C != Colors.White) bucket.HasColor = true;
        }
        TotalTriangles++;

        if (collide)
        {
            if (!_collision.TryGetValue(chunk, out List<Vector3>? faces))
            {
                faces = new List<Vector3>();
                _collision[chunk] = faces;
            }
            faces.Add(a.P);
            faces.Add(b.P);
            faces.Add(c.P);
        }
    }

    /// <summary>A quad given as four points going around its perimeter (either direction).</summary>
    public void AddQuad(object material, BatchVertex a, BatchVertex b, BatchVertex c, BatchVertex d, bool collide)
    {
        AddTriangle(material, a, b, c, collide);
        AddTriangle(material, a, c, d, collide);
    }

    /// <summary>Flat quad facing 'normal' with world-projected UVs.</summary>
    public void AddFlatQuad(object material, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
        Vector3 normal, bool collide, Color? colour = null)
    {
        Color col = colour ?? Colors.White;
        AddQuad(material,
            new BatchVertex(p0, normal, ProjectUV(p0, normal), col),
            new BatchVertex(p1, normal, ProjectUV(p1, normal), col),
            new BatchVertex(p2, normal, ProjectUV(p2, normal), col),
            new BatchVertex(p3, normal, ProjectUV(p3, normal), col),
            collide);
    }

    // ---------------------------------------------------------------- primitives

    private static readonly Vector3[] FaceNormals =
    {
        Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Back, Vector3.Forward,
    };
    private static readonly BoxFaces[] FaceFlags =
    {
        BoxFaces.PosX, BoxFaces.NegX, BoxFaces.PosY, BoxFaces.NegY, BoxFaces.PosZ, BoxFaces.NegZ,
    };

    /// <summary>Box of the given size centred on the transform's origin. UVs are projected in world space.</summary>
    public void AddBox(object material, Transform3D t, Vector3 size, BoxFaces faces = BoxFaces.All,
        bool collide = true, Color? colour = null)
    {
        Vector3 h = size * 0.5f;
        Basis normalBasis = t.Basis.Inverse().Transposed();
        Color col = colour ?? Colors.White;

        for (int f = 0; f < 6; f++)
        {
            if ((faces & FaceFlags[f]) == 0) continue;

            Vector3 n = FaceNormals[f];
            // Two axes spanning the face.
            Vector3 u, v;
            if (f < 2) { u = Vector3.Up; v = Vector3.Back; }
            else if (f < 4) { u = Vector3.Right; v = Vector3.Back; }
            else { u = Vector3.Right; v = Vector3.Up; }

            Vector3 centre = n * h;
            Vector3 du = u * h;
            Vector3 dv = v * h;
            Vector3 p0 = t * (centre - du - dv);
            Vector3 p1 = t * (centre + du - dv);
            Vector3 p2 = t * (centre + du + dv);
            Vector3 p3 = t * (centre - du + dv);

            Vector3 worldN = (normalBasis * n).Normalized();
            AddFlatQuad(material, p0, p1, p2, p3, worldN, collide, col);
        }
    }

    /// <summary>Axis-aligned box from min/max corners.</summary>
    public void AddBoxMinMax(object material, Vector3 min, Vector3 max, BoxFaces faces = BoxFaces.All,
        bool collide = true, Color? colour = null)
    {
        Vector3 size = max - min;
        if (size.X <= 0.0f || size.Y <= 0.0f || size.Z <= 0.0f) return;
        AddBox(material, new Transform3D(Basis.Identity, (min + max) * 0.5f), size, faces, collide, colour);
    }

    /// <summary>Copies one surface of an existing mesh into the batch (used by prop merging and MapOptimizer).</summary>
    public void AddMesh(object material, Mesh mesh, int surface, Transform3D t, bool collide, Color? tint = null)
    {
        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
        Vector3[]? verts = arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]?>();
        if (verts == null || verts.Length == 0) return;

        Vector3[]? normals = arrays[(int)Mesh.ArrayType.Normal].As<Vector3[]?>();
        Vector2[]? uvs = arrays[(int)Mesh.ArrayType.TexUV].As<Vector2[]?>();
        Color[]? colours = arrays[(int)Mesh.ArrayType.Color].As<Color[]?>();
        int[]? index = arrays[(int)Mesh.ArrayType.Index].As<int[]?>();

        Basis nb = t.Basis.Inverse().Transposed();
        Color mul = tint ?? Colors.White;

        int count = index != null && index.Length > 0 ? index.Length : verts.Length;
        BatchVertex Fetch(int i)
        {
            int vi = index != null && index.Length > 0 ? index[i] : i;
            Vector3 p = t * verts[vi];
            Vector3 n = normals != null && vi < normals.Length ? (nb * normals[vi]).Normalized() : Vector3.Up;
            Vector2 uv = uvs != null && vi < uvs.Length ? uvs[vi] : Vector2.Zero;
            Color c = colours != null && vi < colours.Length ? colours[vi] * mul : mul;
            return new BatchVertex(p, n, uv, c);
        }

        for (int i = 0; i + 2 < count; i += 3)
        {
            AddTriangle(material, Fetch(i), Fetch(i + 1), Fetch(i + 2), collide);
        }
    }

    // ---------------------------------------------------------------- output

    public List<BuiltMesh> Build()
    {
        var result = new List<BuiltMesh>(_buckets.Count);
        foreach (Bucket b in _buckets.Values)
        {
            result.Add(new BuiltMesh(b.Chunk, b.Material, BuildMesh(b)));
        }
        // Stable order so re-baking a map produces the same node/file names every time.
        result.Sort((x, y) =>
        {
            int c = x.Chunk.X.CompareTo(y.Chunk.X);
            if (c != 0) return c;
            c = x.Chunk.Y.CompareTo(y.Chunk.Y);
            if (c != 0) return c;
            return string.CompareOrdinal(x.Material.ToString(), y.Material.ToString());
        });
        return result;
    }

    private static ArrayMesh BuildMesh(Bucket b)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = b.Verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = b.Normals.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = b.UVs.ToArray();
        if (b.HasColor) arrays[(int)Mesh.ArrayType.Color] = b.Colors.ToArray();

        var raw = new ArrayMesh();
        raw.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // Index (de-duplicate vertices) and add MikkTSpace tangents so normal maps shade correctly.
        var st = new SurfaceTool();
        st.CreateFrom(raw, 0);
        st.Index();
        st.GenerateTangents();
        return st.Commit();
    }

    public List<(Vector2I Chunk, ConcavePolygonShape3D Shape)> BuildCollision()
    {
        var result = new List<(Vector2I, ConcavePolygonShape3D)>();
        var keys = new List<Vector2I>(_collision.Keys);
        keys.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        foreach (Vector2I chunk in keys)
        {
            var shape = new ConcavePolygonShape3D { Data = _collision[chunk].ToArray() };
            result.Add((chunk, shape));
        }
        return result;
    }
}
