#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace WraithRun.MapKit;

/// <summary>
/// Builds outdoor terrain: war land, trench lines, a paved road with a border, and scatter (rubble,
/// dead trees, roadside barrels). Point RoadPath / TrenchPaths at Path3D children you draw by hand in
/// the 3D viewport — their curve points ARE the road/trench elevation profile, so the ground blends
/// to match whatever height you draw them at. Everything bakes into a handful of merged meshes per
/// chunk (see MeshBatcher) instead of one node per triangle, which is what keeps this fast on a weak
/// iGPU: draw calls, not triangle count, are what an Intel HD 520-class chip chokes on.
///
/// Click "Build Map" in the inspector (bottom of this node's properties) to (re)generate. Re-running
/// it always replaces the previous "Generated" child cleanly, so tweak a value and rebuild as often as
/// you like without piling up leftover nodes.
/// </summary>
[Tool]
[GlobalClass]
public partial class OutdoorMapBuilder : Node3D
{
    [ExportGroup("Bounds")]
    [Export] public Vector2 SizeMeters { get; set; } = new(96.0f, 96.0f);

    /// <summary>Distance between terrain grid vertices. Smaller = smoother but heavier; 1.5-3m suits this hardware.</summary>
    [Export(PropertyHint.Range, "0.5,8,0.1")] public float CellSize { get; set; } = 2.0f;

    /// <summary>Chunk edge length for draw-call batching (see MeshBatcher). 16-32m is a good range.</summary>
    [Export(PropertyHint.Range, "4,64,1")] public float ChunkSize { get; set; } = 16.0f;

    [ExportGroup("Ground")]
    [Export] public SurfaceLayer? Ground { get; set; }
    [Export] public int NoiseSeed { get; set; } = 1;
    [Export(PropertyHint.Range, "0.001,0.2,0.001")] public float NoiseFrequency { get; set; } = 0.02f;
    [Export(PropertyHint.Range, "0,15,0.05")] public float NoiseAmplitude { get; set; } = 1.5f;
    [Export(PropertyHint.Range, "1,6,1")] public int NoiseOctaves { get; set; } = 3;

    [ExportGroup("Road")]
    [Export] public NodePath RoadPath { get; set; } = new();
    [Export(PropertyHint.Range, "1,12,0.1")] public float RoadWidth { get; set; } = 6.0f;
    [Export(PropertyHint.Range, "0,20,0.1")] public float RoadFlattenWidth { get; set; } = 6.0f;
    [Export] public SurfaceLayer? RoadSurface { get; set; }
    [Export(PropertyHint.Range, "0,4,0.05")] public float BorderWidth { get; set; } = 1.0f;
    [Export] public SurfaceLayer? BorderSurface { get; set; }

    [ExportSubgroup("Roadside props")]
    [Export] public PropDefinition? RoadsideProp { get; set; }
    [Export(PropertyHint.Range, "1,30,0.5")] public float RoadsidePropSpacing { get; set; } = 8.0f;
    [Export(PropertyHint.Range, "0,5,0.1")] public float RoadsidePropOffset { get; set; } = 1.5f;
    [Export] public bool RoadsidePropBothSides { get; set; } = true;

    [ExportGroup("Trenches")]
    [Export] public NodePath[] TrenchPaths { get; set; } = Array.Empty<NodePath>();
    [Export(PropertyHint.Range, "1,8,0.1")] public float TrenchWidth { get; set; } = 2.2f;
    [Export(PropertyHint.Range, "0.3,4,0.05")] public float TrenchDepth { get; set; } = 1.8f;
    [Export(PropertyHint.Range, "0.2,6,0.1")] public float TrenchWallSpan { get; set; } = 1.2f;
    [Export] public SurfaceLayer? TrenchFloorSurface { get; set; }
    [Export] public SurfaceLayer? TrenchWallSurface { get; set; }

    [ExportGroup("Scatter")]
    [Export] public ScatterLayer[] ScatterLayers { get; set; } = Array.Empty<ScatterLayer>();
    [Export(PropertyHint.Range, "0,15,0.5")] public float ScatterRoadClearance { get; set; } = 1.5f;
    [Export] public bool ScatterCastsShadows { get; set; }

    [ExportGroup("Spawns")]
    [Export(PropertyHint.Range, "2,64,1")] public int SpawnCount { get; set; } = 8;
    [Export(PropertyHint.Range, "1,10,0.1")] public float SpawnRingFraction { get; set; } = 0.8f;

    private Polyline2D? _road;
    private readonly List<Polyline2D> _trenches = new();

    [ExportToolButton("Build Map")]
    public Callable BuildMapButton => Callable.From(BuildMap);

    [ExportToolButton("Clear")]
    public Callable ClearButton => Callable.From(() => MapKitUtil.ClearGenerated(this));

    public void BuildMap()
    {
        if (!Engine.IsEditorHint())
        {
            MapKitUtil.Warn("BuildMap is an editor-time tool; it does not run in an exported game.");
            return;
        }
        if (Ground == null)
        {
            MapKitUtil.Warn("Assign a Ground SurfaceLayer before building.");
            return;
        }

        Node owner = MapKitUtil.GetOwnerRoot(this);
        string genFolder = MapKitUtil.GenFolderFor(owner);
        MapKitUtil.PrepareGenFolder(genFolder);
        Node3D root = MapKitUtil.ResetGenerated(this, owner);

        var visual = MapKitUtil.AddOwned(root, new Node3D(), "Visuals", owner);
        var collision = MapKitUtil.AddOwned(root, new Node3D(), "Collision", owner);
        var spawns = MapKitUtil.AddOwned(root, new Node3D(), "Spawns", owner);

        LoadPaths();

        var noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Seed = NoiseSeed,
            Frequency = NoiseFrequency,
            FractalOctaves = NoiseOctaves,
        };

        var batch = new MeshBatcher(ChunkSize);
        BuildTerrain(batch, noise);
        if (_road != null && RoadSurface != null) BuildRoad(batch);
        BuildScatter(batch, visual, collision, owner, genFolder);
        BuildSpawns(spawns, owner);

        Func<object, Material?> resolve = mat => mat is SurfaceLayer sl ? sl.CreateMaterial() : null;
        MapKitUtil.EmitBatch(batch, resolve, visual, collision, owner, genFolder, "Terrain", castShadows: true, visibleRange: 0.0f);

        MapKitUtil.Log($"OutdoorMapBuilder: {batch.TotalTriangles} triangles, {batch.BucketCount} draw calls before scatter.");
    }

    private void LoadPaths()
    {
        _road = RoadPath.IsEmpty ? null : BakePath(GetNodeOrNull<Path3D>(RoadPath));
        _trenches.Clear();
        foreach (NodePath p in TrenchPaths)
        {
            Polyline2D? line = BakePath(GetNodeOrNull<Path3D>(p));
            if (line != null) _trenches.Add(line);
        }
    }

    private Polyline2D? BakePath(Path3D? path)
    {
        if (path?.Curve == null || path.Curve.PointCount < 2) return null;
        var line = new Polyline2D();
        // Baked (not raw control points) so long straight stretches don't get a phantom kink at each handle.
        Vector3[] baked = path.Curve.GetBakedPoints();
        Transform3D toLocal = GlobalTransform.AffineInverse() * path.GlobalTransform;
        foreach (Vector3 p in baked)
        {
            Vector3 local = toLocal * p;
            line.Add(new Vector2(local.X, local.Z), local.Y);
        }
        return line;
    }

    // ---------------------------------------------------------------- height field

    private enum Zone { Ground, TrenchWall, TrenchFloor }

    private readonly struct Sample
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Zone Zone;
        public Sample(Vector3 p, Vector3 n, Zone z) { Position = p; Normal = n; Zone = z; }
    }

    private float HeightAt(Vector2 p, FastNoiseLite noise, out Zone zone)
    {
        zone = Zone.Ground;
        float h = noise.GetNoise2D(p.X, p.Y) * NoiseAmplitude;

        if (_road != null)
        {
            _road.Nearest(p, out float dist, out float roadY, out _);
            float half = RoadWidth * 0.5f;
            float t = MapKitUtil.SmoothStep(half, half + Mathf.Max(RoadFlattenWidth, 0.01f), dist);
            h = Mathf.Lerp(roadY, h, t);
        }

        foreach (Polyline2D trench in _trenches)
        {
            trench.Nearest(p, out float dist, out float trenchY, out _);
            float half = TrenchWidth * 0.5f;
            float span = Mathf.Max(TrenchWallSpan, 0.05f);
            float t = MapKitUtil.SmoothStep(half, half + span, dist);
            float floorY = trenchY - TrenchDepth;
            float withTrench = Mathf.Lerp(floorY, h, t);
            if (withTrench < h)
            {
                h = withTrench;
                zone = t < 0.15f ? Zone.TrenchFloor : Zone.TrenchWall;
            }
        }

        return h;
    }

    private void BuildTerrain(MeshBatcher batch, FastNoiseLite noise)
    {
        int stepsX = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.X / CellSize));
        int stepsZ = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.Y / CellSize));
        float sizeX = stepsX * CellSize;
        float sizeZ = stepsZ * CellSize;
        Vector2 origin = new(-sizeX * 0.5f, -sizeZ * 0.5f);

        int w = stepsX + 1, d = stepsZ + 1;
        var grid = new Sample[w * d];
        const float eps = 0.35f;

        for (int iz = 0; iz < d; iz++)
        {
            for (int ix = 0; ix < w; ix++)
            {
                Vector2 p = origin + new Vector2(ix * CellSize, iz * CellSize);
                float h = HeightAt(p, noise, out Zone zone);
                float hL = HeightAt(p + new Vector2(-eps, 0.0f), noise, out _);
                float hR = HeightAt(p + new Vector2(eps, 0.0f), noise, out _);
                float hD = HeightAt(p + new Vector2(0.0f, -eps), noise, out _);
                float hU = HeightAt(p + new Vector2(0.0f, eps), noise, out _);
                Vector3 normal = new Vector3(hL - hR, 2.0f * eps, hD - hU).Normalized();
                grid[iz * w + ix] = new Sample(new Vector3(p.X, h, p.Y), normal, zone);
            }
        }

        SurfaceLayer groundLayer = Ground!;
        for (int iz = 0; iz < stepsZ; iz++)
        {
            for (int ix = 0; ix < stepsX; ix++)
            {
                Sample a = grid[iz * w + ix];
                Sample b = grid[iz * w + ix + 1];
                Sample c = grid[(iz + 1) * w + ix + 1];
                Sample e = grid[(iz + 1) * w + ix];

                Zone worst = Worst(Worst(a.Zone, b.Zone), Worst(c.Zone, e.Zone));
                object material = worst switch
                {
                    Zone.TrenchFloor when TrenchFloorSurface != null => TrenchFloorSurface,
                    Zone.TrenchWall when TrenchWallSurface != null => TrenchWallSurface,
                    Zone.TrenchFloor when TrenchWallSurface != null => TrenchWallSurface,
                    _ => groundLayer,
                };

                batch.AddQuad(material,
                    new BatchVertex(a.Position, a.Normal, MeshBatcher.ProjectUV(a.Position, a.Normal), Colors.White),
                    new BatchVertex(b.Position, b.Normal, MeshBatcher.ProjectUV(b.Position, b.Normal), Colors.White),
                    new BatchVertex(c.Position, c.Normal, MeshBatcher.ProjectUV(c.Position, c.Normal), Colors.White),
                    new BatchVertex(e.Position, e.Normal, MeshBatcher.ProjectUV(e.Position, e.Normal), Colors.White),
                    collide: true);
            }
        }
    }

    private static Zone Worst(Zone a, Zone b) => (Zone)Math.Max((int)a, (int)b);

    // ---------------------------------------------------------------- road

    private void BuildRoad(MeshBatcher batch)
    {
        Polyline2D road = _road!;
        float half = RoadWidth * 0.5f;
        int samples = Mathf.Max(2, Mathf.CeilToInt(road.Length / 2.0f) + 1);

        Vector3[] left = new Vector3[samples];
        Vector3[] right = new Vector3[samples];
        Vector3[] borderL = new Vector3[samples];
        Vector3[] borderR = new Vector3[samples];

        for (int i = 0; i < samples; i++)
        {
            float dist = road.Length * i / (samples - 1);
            SampleAlong(road, dist, out Vector2 pos, out Vector2 tangent, out float y);
            Vector2 normal = new(-tangent.Y, tangent.X);
            Vector2 lp = pos + normal * half;
            Vector2 rp = pos - normal * half;
            left[i] = new Vector3(lp.X, y, lp.Y);
            right[i] = new Vector3(rp.X, y, rp.Y);
            borderL[i] = new Vector3((pos + normal * (half + BorderWidth)).X, y, (pos + normal * (half + BorderWidth)).Y);
            borderR[i] = new Vector3((pos - normal * (half + BorderWidth)).X, y, (pos - normal * (half + BorderWidth)).Y);
        }

        for (int i = 0; i + 1 < samples; i++)
        {
            batch.AddFlatQuad(RoadSurface!, left[i], right[i], right[i + 1], left[i + 1], Vector3.Up, collide: false);
            if (BorderWidth > 0.0f && BorderSurface != null)
            {
                batch.AddFlatQuad(BorderSurface, borderL[i], left[i], left[i + 1], borderL[i + 1], Vector3.Up, collide: false);
                batch.AddFlatQuad(BorderSurface, right[i], borderR[i], borderR[i + 1], right[i + 1], Vector3.Up, collide: false);
            }
        }

        if (RoadsideProp != null && RoadsidePropSpacing > 0.1f)
        {
            var rng = new RandomNumberGenerator();
            for (float dist = 0.0f; dist < road.Length; dist += RoadsidePropSpacing)
            {
                SampleAlong(road, dist, out Vector2 pos, out Vector2 tangent, out float y);
                Vector2 normal = new(-tangent.Y, tangent.X);
                PlaceRoadsideProp(batch, pos + normal * (half + BorderWidth + RoadsidePropOffset), y, tangent, rng);
                if (RoadsidePropBothSides)
                {
                    PlaceRoadsideProp(batch, pos - normal * (half + BorderWidth + RoadsidePropOffset), y, -tangent, rng);
                }
            }
        }
    }

    private void PlaceRoadsideProp(MeshBatcher batch, Vector2 xz, float y, Vector2 facing, RandomNumberGenerator rng)
    {
        if (RoadsideProp!.HasArt) return; // scene-based roadside props are placed via the scatter path, not merged here.
        float yaw = Mathf.Atan2(facing.X, facing.Y);
        var t = new Transform3D(Basis.Identity.Rotated(Vector3.Up, yaw), new Vector3(xz.X, y - RoadsideProp.SinkIntoGround, xz.Y));
        RoadsideProp.AddPrimitiveTo(batch, t);
        _ = rng; // reserved for future per-instance jitter
    }

    private static void SampleAlong(Polyline2D line, float distance, out Vector2 pos, out Vector2 tangent, out float y)
    {
        distance = Mathf.Clamp(distance, 0.0f, line.Length);
        int i = 0;
        while (i + 1 < line.Count && line.Cumulative[i + 1] < distance) i++;
        i = Mathf.Min(i, line.Count - 2);
        float segLen = Mathf.Max(line.Cumulative[i + 1] - line.Cumulative[i], 0.0001f);
        float t = Mathf.Clamp((distance - line.Cumulative[i]) / segLen, 0.0f, 1.0f);
        pos = line.Points[i].Lerp(line.Points[i + 1], t);
        y = Mathf.Lerp(line.Heights[i], line.Heights[i + 1], t);
        tangent = (line.Points[i + 1] - line.Points[i]).Normalized();
    }

    // ---------------------------------------------------------------- scatter

    private void BuildScatter(MeshBatcher batch, Node3D visual, Node3D collision, Node owner, string genFolder)
    {
        if (ScatterLayers.Length == 0) return;

        float sizeX = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.X / CellSize)) * CellSize;
        float sizeZ = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.Y / CellSize)) * CellSize;
        var area = new Rect2(new Vector2(-sizeX * 0.5f, -sizeZ * 0.5f), new Vector2(sizeX, sizeZ));
        var noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Seed = NoiseSeed,
            Frequency = NoiseFrequency,
            FractalOctaves = NoiseOctaves,
        };

        float roadClear = _road != null ? RoadWidth * 0.5f + BorderWidth + ScatterRoadClearance : -1.0f;

        bool Mask(Vector2 p)
        {
            if (_road != null)
            {
                _road.Nearest(p, out float dist, out _, out _);
                if (dist < roadClear) return false;
            }
            foreach (Polyline2D trench in _trenches)
            {
                trench.Nearest(p, out float dist, out _, out _);
                if (dist < TrenchWidth * 0.5f + TrenchWallSpan) return false;
            }
            return true;
        }

        float HeightFn(Vector2 p) => HeightAt(p, noise, out _);
        Vector3 NormalFn(Vector2 p)
        {
            const float eps = 0.35f;
            float hL = HeightAt(p + new Vector2(-eps, 0.0f), noise, out _);
            float hR = HeightAt(p + new Vector2(eps, 0.0f), noise, out _);
            float hD = HeightAt(p + new Vector2(0.0f, -eps), noise, out _);
            float hU = HeightAt(p + new Vector2(0.0f, eps), noise, out _);
            return new Vector3(hL - hR, 2.0f * eps, hD - hU).Normalized();
        }

        int index = 0;
        foreach (ScatterLayer layer in ScatterLayers)
        {
            List<ScatterInstance> instances = ScatterField.Generate(layer, area, Mask, HeightFn, NormalFn);
            ScatterField.Emit(instances, batch, visual, collision, owner, genFolder, $"Scatter{index}");
            index++;
        }
    }

    // ---------------------------------------------------------------- spawns

    private void BuildSpawns(Node3D parent, Node owner)
    {
        if (SpawnCount <= 0) return;
        float sizeX = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.X / CellSize)) * CellSize;
        float sizeZ = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.Y / CellSize)) * CellSize;
        float radius = Mathf.Min(sizeX, sizeZ) * 0.5f * Mathf.Clamp(SpawnRingFraction, 0.1f, 1.0f);
        var noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Seed = NoiseSeed, Frequency = NoiseFrequency, FractalOctaves = NoiseOctaves };

        for (int i = 0; i < SpawnCount; i++)
        {
            float a = Mathf.Tau * i / SpawnCount;
            var p = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius;
            float y = HeightAt(p, noise, out _) + 1.0f;
            MapKitUtil.AddSpawn(parent, $"Spawn{i}", new Vector3(p.X, y, p.Y), Vector3.Zero, owner);
        }
    }
}
