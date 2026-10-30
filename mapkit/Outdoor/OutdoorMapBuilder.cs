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

    [ExportGroup("Ground blend")]
    // Only used when the Ground SurfaceLayer has BlendLayerB / BlendLayerC set. Otherwise the terrain keeps plain white vertex colours.
    [Export(PropertyHint.Range, "0,1,0.01")] public float BurntCoverage { get; set; } = 0.45f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float MossCoverage { get; set; } = 0.3f;
    [Export(PropertyHint.Range, "0.005,0.3,0.001")] public float BlendNoiseFrequency { get; set; } = 0.05f;

    [ExportGroup("Craters")]
    // 0 = none, so maps that do not opt in are unchanged. Sizes are WORLD metres: the builder's own scale is compensated for.
    [Export(PropertyHint.Range, "0,80,1")] public int CraterCount { get; set; }
    [Export(PropertyHint.Range, "1,20,0.1")] public float CraterMinRadius { get; set; } = 3.5f;
    [Export(PropertyHint.Range, "1,30,0.1")] public float CraterMaxRadius { get; set; } = 8.0f;
    /// <summary>Depth as a fraction of the radius (0.2 = a 5 m crater is 1 m deep). Keep under ~0.35 so the walls stay walkable.</summary>
    [Export(PropertyHint.Range, "0.05,0.5,0.01")] public float CraterDepthRatio { get; set; } = 0.22f;
    [Export] public int CraterSeed { get; set; } = 7;

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

    /// <summary>Nothing is scattered within this many metres of a spawn point, so nobody spawns inside a prop.</summary>
    [Export(PropertyHint.Range, "0,20,0.5")] public float ScatterSpawnClearance { get; set; } = 5.0f;

    [ExportGroup("Perimeter")]
    /// <summary>Solid wall round the map edge so nobody can walk off into the void. Empty = no wall.</summary>
    [Export] public SurfaceLayer? PerimeterSurface { get; set; }
    [Export(PropertyHint.Range, "0,20,0.5")] public float PerimeterHeight { get; set; } = 5.0f;
    [Export(PropertyHint.Range, "0.2,4,0.1")] public float PerimeterThickness { get; set; } = 1.0f;

    [ExportGroup("Spawns")]
    [Export(PropertyHint.Range, "2,64,1")] public int SpawnCount { get; set; } = 8;
    [Export(PropertyHint.Range, "1,10,0.1")] public float SpawnRingFraction { get; set; } = 0.8f;

    private const float RoadLift = 0.05f;

    private Polyline2D? _road;
    private readonly List<Polyline2D> _trenches = new();

    private readonly struct Crater
    {
        public readonly Vector2 Center; // builder-local metres
        public readonly float Radius;   // world metres
        public readonly float Depth;    // local Y metres
        public Crater(Vector2 c, float r, float d) { Center = c; Radius = r; Depth = d; }
    }

    private readonly List<Crater> _craters = new();
    // The builder node can be scaled (Ashfields is 1.25 x 0.83), which would stretch a round crater into an oval.
    private Vector2 _worldScaleXZ = Vector2.One;

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
        BuildCraters();

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
        BuildPerimeter(batch);
        BuildSpawns(spawns, owner);

        Func<object, Material?> resolve = mat => mat is SurfaceLayer sl ? (sl.HasBlend ? sl.CreateBlendMaterial() : sl.CreateMaterial()) : null;
        MapKitUtil.EmitBatch(batch, resolve, visual, collision, owner, genFolder, "Terrain", castShadows: true, visibleRange: 0.0f);

        (owner.GetNodeOrNull<RuinPass>("RuinPass") ?? GetNodeOrNull<RuinPass>("RuinPass"))?.ApplyTo(root);

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

    // Order matters: Worst() keeps the highest, so trench zones outrank a crater that happens to touch them.
    private enum Zone { Ground, Crater, TrenchWall, TrenchFloor }

    private readonly struct Sample
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Zone Zone;
        public readonly Color Splat; // R = look B (burnt), G = look C (moss)
        public Sample(Vector3 p, Vector3 n, Zone z, Color splat) { Position = p; Normal = n; Zone = z; Splat = splat; }
    }

    /// <summary>0 well outside every crater, 1 inside the bowl; used to scorch the ground around impacts.</summary>
    private float CraterInfluence(Vector2 p)
    {
        float best = 0.0f;
        foreach (Crater c in _craters)
        {
            float d = WorldDistance(p, c.Center);
            best = Mathf.Max(best, 1.0f - MapKitUtil.SmoothStep(c.Radius * 0.7f, c.Radius * 1.9f, d));
        }
        return best;
    }

    private float WorldDistance(Vector2 a, Vector2 b)
    {
        Vector2 d = (a - b) * _worldScaleXZ;
        return d.Length();
    }

    private void BuildCraters()
    {
        _craters.Clear();
        Vector3 s = GlobalTransform.Basis.Scale;
        _worldScaleXZ = new Vector2(Mathf.Max(Mathf.Abs(s.X), 0.01f), Mathf.Max(Mathf.Abs(s.Z), 0.01f));
        if (CraterCount <= 0) return;

        float minR = Mathf.Min(CraterMinRadius, CraterMaxRadius);
        float maxR = Mathf.Max(CraterMinRadius, CraterMaxRadius);
        // Local half-extents of the playable area (the heightfield is built in local space).
        float hx = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.X / CellSize)) * CellSize * 0.5f;
        float hz = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.Y / CellSize)) * CellSize * 0.5f;
        List<Vector2> spawns = SpawnPositions();

        // Team spawn rectangles (world space) are placed by hand in the map scene; keep craters off them so
        // nobody respawns on a crater rim or in a pit. Same group + "half_extent" metadata TeamSpawnArea reads.
        var teamAreas = new List<(Vector2 Centre, Vector2 Half)>();
        if (IsInsideTree())
        {
            for (int team = 0; team < 2; team++)
            {
                foreach (Node n in GetTree().GetNodesInGroup("team_spawn_area_" + team))
                {
                    if (n is not Node3D marker) continue;
                    Vector2 half = marker.HasMeta("half_extent") ? marker.GetMeta("half_extent").AsVector2() : new Vector2(6.0f, 6.0f);
                    teamAreas.Add((new Vector2(marker.GlobalPosition.X, marker.GlobalPosition.Z), half));
                }
            }
        }

        var rng = new RandomNumberGenerator { Seed = (ulong)Math.Max(1, CraterSeed) };
        int attempts = CraterCount * 40;
        for (int i = 0; i < attempts && _craters.Count < CraterCount; i++)
        {
            float r = rng.RandfRange(minR, maxR);
            // Keep the whole crater (plus its raised rim) inside the map.
            float marginX = r * 1.7f / _worldScaleXZ.X;
            float marginZ = r * 1.7f / _worldScaleXZ.Y;
            if (hx - marginX <= 0.0f || hz - marginZ <= 0.0f) continue;
            var c = new Vector2(rng.RandfRange(-hx + marginX, hx - marginX), rng.RandfRange(-hz + marginZ, hz - marginZ));

            bool ok = true;
            foreach (Vector2 sp in spawns)
            {
                if (WorldDistance(c, sp) < r * 1.7f + ScatterSpawnClearance) { ok = false; break; }
            }
            if (ok && teamAreas.Count > 0)
            {
                Vector3 wp = GlobalTransform * new Vector3(c.X, 0.0f, c.Y);
                float reach = r * 1.7f + 2.0f;
                foreach ((Vector2 centre, Vector2 half) in teamAreas)
                {
                    float dx = Mathf.Max(Mathf.Abs(wp.X - centre.X) - half.X, 0.0f);
                    float dz = Mathf.Max(Mathf.Abs(wp.Z - centre.Y) - half.Y, 0.0f);
                    if (dx * dx + dz * dz < reach * reach) { ok = false; break; }
                }
            }
            if (ok && _road != null)
            {
                _road.Nearest(c, out float dist, out _, out _);
                // dist is in local metres; the road width is too, so compare in the same unit with a small safety factor.
                if (dist * Mathf.Max(_worldScaleXZ.X, _worldScaleXZ.Y) < RoadWidth * 0.5f + BorderWidth + r * 1.7f + ScatterRoadClearance) ok = false;
            }
            if (ok)
            {
                foreach (Polyline2D trench in _trenches)
                {
                    trench.Nearest(c, out float dist, out _, out _);
                    if (dist * Mathf.Max(_worldScaleXZ.X, _worldScaleXZ.Y) < TrenchWidth * 0.5f + TrenchWallSpan + r * 1.7f) { ok = false; break; }
                }
            }
            if (ok)
            {
                foreach (Crater other in _craters)
                {
                    if (WorldDistance(c, other.Center) < (r + other.Radius) * 1.1f) { ok = false; break; }
                }
            }
            if (!ok) continue;

            _craters.Add(new Crater(c, r, r * CraterDepthRatio));
        }

        MapKitUtil.Log($"OutdoorMapBuilder: placed {_craters.Count}/{CraterCount} craters.");
    }

    private float HeightAt(Vector2 p, FastNoiseLite noise, out Zone zone)
    {
        zone = Zone.Ground;
        float h = noise.GetNoise2D(p.X, p.Y) * NoiseAmplitude;

        // Craters sit on the noise ground but are placed clear of the road and trenches, so the later
        // road/trench passes never fight them. Bowl is a parabola; the rim is a soft raised lip of thrown earth.
        foreach (Crater c in _craters)
        {
            float u = WorldDistance(p, c.Center) / c.Radius;
            if (u >= 1.7f) continue;
            float bowl = u < 1.0f ? -c.Depth * (1.0f - u * u) : 0.0f;
            float rimU = (u - 1.08f) / 0.25f;
            h += bowl + c.Depth * 0.28f * Mathf.Exp(-rimU * rimU);
            if (u < 1.15f) zone = Zone.Crater;
        }

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

        bool blend = Ground != null && Ground.HasBlend;
        var burnNoise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Seed = NoiseSeed + 101,
            Frequency = BlendNoiseFrequency,
            FractalOctaves = 3,
        };
        var mossNoise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Seed = NoiseSeed + 202,
            Frequency = BlendNoiseFrequency * 1.3f,
            FractalOctaves = 3,
        };
        // Noise is roughly -1..1. Coverage slides the cut-off: 0 = almost none, 1 = almost everywhere.
        float burnLo = Mathf.Lerp(0.55f, -0.45f, Mathf.Clamp(BurntCoverage, 0.0f, 1.0f));
        float mossLo = Mathf.Lerp(0.55f, -0.45f, Mathf.Clamp(MossCoverage, 0.0f, 1.0f));

        for (int iz = 0; iz < d; iz++)
        {
            for (int ix = 0; ix < w; ix++)
            {
                Vector2 p = origin + new Vector2(ix * CellSize, iz * CellSize);
                float h = HeightAt(p, noise, out Zone zone);

                Color splat = Colors.White;
                if (blend)
                {
                    // Burnt earth: big noise patches, plus a guaranteed scorched halo around every crater.
                    float burn = MapKitUtil.SmoothStep(burnLo, burnLo + 0.4f, burnNoise.GetNoise2D(p.X, p.Y));
                    burn = Mathf.Clamp(burn + CraterInfluence(p) * 0.9f, 0.0f, 1.0f);
                    // Moss: only where it has not burnt, and never on trench walls/floors.
                    float moss = MapKitUtil.SmoothStep(mossLo, mossLo + 0.4f, mossNoise.GetNoise2D(p.X, p.Y));
                    moss *= 1.0f - burn * 0.85f;
                    if (zone == Zone.TrenchFloor || zone == Zone.TrenchWall) moss = 0.0f;
                    splat = new Color(burn, Mathf.Min(moss, 1.0f - burn), 0.0f, 1.0f);
                }

                float hL = HeightAt(p + new Vector2(-eps, 0.0f), noise, out _);
                float hR = HeightAt(p + new Vector2(eps, 0.0f), noise, out _);
                float hD = HeightAt(p + new Vector2(0.0f, -eps), noise, out _);
                float hU = HeightAt(p + new Vector2(0.0f, eps), noise, out _);
                Vector3 normal = new Vector3(hL - hR, 2.0f * eps, hD - hU).Normalized();
                grid[iz * w + ix] = new Sample(new Vector3(p.X, h, p.Y), normal, zone, splat);
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
                    new BatchVertex(a.Position, a.Normal, MeshBatcher.ProjectUV(a.Position, a.Normal), a.Splat),
                    new BatchVertex(b.Position, b.Normal, MeshBatcher.ProjectUV(b.Position, b.Normal), b.Splat),
                    new BatchVertex(c.Position, c.Normal, MeshBatcher.ProjectUV(c.Position, c.Normal), c.Splat),
                    new BatchVertex(e.Position, e.Normal, MeshBatcher.ProjectUV(e.Position, e.Normal), e.Splat),
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
            // The terrain is flattened to exactly the road's height under it, so the road quads sit a
            // hair above it. Coplanar surfaces flicker (z-fighting); 5 cm is invisible but stable.
            y += RoadLift;
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
        List<Vector2> spawnPoints = SpawnPositions();

        bool Mask(Vector2 p)
        {
            foreach (Vector2 sp in spawnPoints)
            {
                if (p.DistanceTo(sp) < ScatterSpawnClearance) return false;
            }
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
            // Nothing is planted in a crater bowl: a tree growing out of a blast pit reads wrong.
            foreach (Crater c in _craters)
            {
                if (WorldDistance(p, c.Center) < c.Radius * 1.0f) return false;
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

    // ---------------------------------------------------------------- perimeter

    private void BuildPerimeter(MeshBatcher batch)
    {
        if (PerimeterSurface == null || PerimeterHeight <= 0.0f) return;

        float sizeX = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.X / CellSize)) * CellSize;
        float sizeZ = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.Y / CellSize)) * CellSize;
        float hx = sizeX * 0.5f;
        float hz = sizeZ * 0.5f;
        float t = PerimeterThickness;
        float bottom = -3.0f;
        float top = PerimeterHeight;
        SurfaceLayer s = PerimeterSurface;

        // North and south strips include the corners; east and west fit between them.
        AddWallStrip(batch, s, new Vector3(-hx - t, bottom, -hz - t), new Vector3(hx + t, top, -hz));
        AddWallStrip(batch, s, new Vector3(-hx - t, bottom, hz), new Vector3(hx + t, top, hz + t));
        AddWallStrip(batch, s, new Vector3(-hx - t, bottom, -hz), new Vector3(-hx, top, hz));
        AddWallStrip(batch, s, new Vector3(hx, bottom, -hz), new Vector3(hx + t, top, hz));
    }

    /// <summary>A long box cut into chunk-sized pieces, so each piece lands in the chunk it sits in (keeps culling effective).</summary>
    private void AddWallStrip(MeshBatcher batch, SurfaceLayer surface, Vector3 min, Vector3 max)
    {
        bool alongX = (max.X - min.X) >= (max.Z - min.Z);
        float length = alongX ? max.X - min.X : max.Z - min.Z;
        float step = Mathf.Max(ChunkSize, 4.0f);
        int pieces = Mathf.Max(1, Mathf.CeilToInt(length / step));
        for (int i = 0; i < pieces; i++)
        {
            float a = length * i / pieces;
            float b = length * (i + 1) / pieces;
            Vector3 lo = min;
            Vector3 hi = max;
            if (alongX)
            {
                lo.X = min.X + a;
                hi.X = min.X + b;
            }
            else
            {
                lo.Z = min.Z + a;
                hi.Z = min.Z + b;
            }
            batch.AddBoxMinMax(surface, lo, hi, BoxFaces.All, collide: true);
        }
    }

    // ---------------------------------------------------------------- spawns

    /// <summary>Ground-plane positions of the spawn ring. BuildSpawns uses the same maths.</summary>
    private List<Vector2> SpawnPositions()
    {
        var list = new List<Vector2>();
        if (SpawnCount <= 0) return list;
        float sizeX = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.X / CellSize)) * CellSize;
        float sizeZ = Mathf.Max(1, Mathf.RoundToInt(SizeMeters.Y / CellSize)) * CellSize;
        float radius = Mathf.Min(sizeX, sizeZ) * 0.5f * Mathf.Clamp(SpawnRingFraction, 0.1f, 1.0f);
        for (int i = 0; i < SpawnCount; i++)
        {
            float a = Mathf.Tau * i / SpawnCount;
            list.Add(new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
        }
        return list;
    }

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
