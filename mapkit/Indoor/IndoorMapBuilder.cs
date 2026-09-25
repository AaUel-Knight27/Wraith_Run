#nullable enable
using System.Collections.Generic;
using Godot;

namespace WraithRun.MapKit;

/// <summary>
/// Builds an indoor space from a RoomLayout ASCII grid: solid cells become wall blocks, open cells get
/// a floor and (optionally) a ceiling. The same builder makes a train station, a museum, a lab or a
/// row of classrooms — only the Layout grid and the four SurfaceLayers change. Add FurnishAreas to
/// auto-fill rectangles with desks/benches/display cases.
///
/// Internal faces between two touching wall cells are skipped automatically (nothing is ever rendered
/// that a player could not possibly see), which keeps a big blocky building just as cheap as a small one.
///
/// Click "Build Map" in the inspector to (re)generate.
/// </summary>
[Tool]
[GlobalClass]
public partial class IndoorMapBuilder : Node3D
{
    [ExportGroup("Layout")]
    [Export] public RoomLayout? Layout { get; set; }

    /// <summary>Batching chunk size; 0 keeps the whole building as one draw call per material, which suits most single buildings.</summary>
    [Export(PropertyHint.Range, "0,64,1")] public float ChunkSize { get; set; }

    [ExportGroup("Surfaces")]
    [Export] public SurfaceLayer? WallSurface { get; set; }
    [Export] public SurfaceLayer? WindowSurface { get; set; }
    [Export] public SurfaceLayer? FloorSurface { get; set; }
    [Export] public SurfaceLayer? CeilingSurface { get; set; }
    [Export] public bool BuildCeiling { get; set; } = true;

    [ExportGroup("Furnishing")]
    [Export] public FurnishArea[] FurnishAreas { get; set; } = System.Array.Empty<FurnishArea>();

    [ExportGroup("Spawns")]
    [Export] public Godot.Collections.Array<Vector2I> SpawnCells { get; set; } = new();

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
        RoomLayout? layout = Layout;
        if (layout == null || layout.Rows.Length == 0)
        {
            MapKitUtil.Warn("Assign a Layout (RoomLayout with at least one row) before building.");
            return;
        }
        if (WallSurface == null || FloorSurface == null)
        {
            MapKitUtil.Warn("Assign at least WallSurface and FloorSurface before building.");
            return;
        }

        Node owner = MapKitUtil.GetOwnerRoot(this);
        string genFolder = MapKitUtil.GenFolderFor(owner);
        MapKitUtil.PrepareGenFolder(genFolder);
        Node3D root = MapKitUtil.ResetGenerated(this, owner);

        var visual = MapKitUtil.AddOwned(root, new Node3D(), "Visuals", owner);
        var collision = MapKitUtil.AddOwned(root, new Node3D(), "Collision", owner);
        var doors = MapKitUtil.AddOwned(root, new Node3D(), "Doors", owner);
        var spawns = MapKitUtil.AddOwned(root, new Node3D(), "Spawns", owner);

        var batch = new MeshBatcher(ChunkSize);
        float cs = layout.CellSize;
        float wh = layout.WallHeight;
        int w = layout.Width, d = layout.Depth;

        for (int row = 0; row < d; row++)
        {
            for (int col = 0; col < w; col++)
            {
                char c = layout.CellAt(col, row);
                if (c == ' ') continue;

                float x0 = col * cs, x1 = x0 + cs;
                float z0 = row * cs, z1 = z0 + cs;

                if (RoomLayout.IsSolid(c))
                {
                    BoxFaces faces = ExposedFaces(layout, col, row);
                    if (faces == BoxFaces.None) continue;
                    SurfaceLayer material = c == 'W' && WindowSurface != null ? WindowSurface : WallSurface;
                    batch.AddBoxMinMax(material, new Vector3(x0, 0.0f, z0), new Vector3(x1, wh, z1), faces, collide: true);
                }
                else
                {
                    batch.AddFlatQuad(FloorSurface, new Vector3(x0, 0.0f, z0), new Vector3(x1, 0.0f, z0),
                        new Vector3(x1, 0.0f, z1), new Vector3(x0, 0.0f, z1), Vector3.Up, collide: true);

                    if (BuildCeiling && CeilingSurface != null)
                    {
                        batch.AddFlatQuad(CeilingSurface, new Vector3(x0, wh, z0), new Vector3(x0, wh, z1),
                            new Vector3(x1, wh, z1), new Vector3(x1, wh, z0), Vector3.Down, collide: false);
                    }

                    if (c == 'D')
                    {
                        var marker = new Marker3D { Position = new Vector3(x0 + cs * 0.5f, 0.0f, z0 + cs * 0.5f) };
                        MapKitUtil.AddOwned(doors, marker, $"Door_{col}_{row}", owner);
                        marker.AddToGroup("door", true);
                    }
                }
            }
        }

        BuildFurnishing(batch, layout, visual, collision, owner, genFolder);

        foreach (Vector2I cell in SpawnCells)
        {
            Vector3 p = new(cell.X * cs + cs * 0.5f, 0.1f, cell.Y * cs + cs * 0.5f);
            MapKitUtil.AddSpawn(spawns, $"Spawn_{cell.X}_{cell.Y}", p, p + Vector3.Forward, owner);
        }

        System.Func<object, Material?> resolve = mat => mat is SurfaceLayer sl ? sl.CreateMaterial() : null;
        MapKitUtil.EmitBatch(batch, resolve, visual, collision, owner, genFolder, "Room", castShadows: true, visibleRange: 0.0f);

        MapKitUtil.Log($"IndoorMapBuilder: {batch.TotalTriangles} triangles, {batch.BucketCount} draw calls before furnishing props.");
    }

    private static BoxFaces ExposedFaces(RoomLayout layout, int col, int row)
    {
        BoxFaces faces = BoxFaces.None;
        if (!RoomLayout.IsSolid(layout.CellAt(col - 1, row))) faces |= BoxFaces.NegX;
        if (!RoomLayout.IsSolid(layout.CellAt(col + 1, row))) faces |= BoxFaces.PosX;
        if (!RoomLayout.IsSolid(layout.CellAt(col, row - 1))) faces |= BoxFaces.NegZ;
        if (!RoomLayout.IsSolid(layout.CellAt(col, row + 1))) faces |= BoxFaces.PosZ;
        return faces;
    }

    private void BuildFurnishing(MeshBatcher batch, RoomLayout layout, Node3D visual, Node3D collision,
        Node owner, string genFolder)
    {
        if (FurnishAreas.Length == 0) return;
        float cs = layout.CellSize;
        int index = 0;

        foreach (FurnishArea area in FurnishAreas)
        {
            if (area.Prop == null) continue;
            Vector2I lo = new(Mathf.Min(area.CellStart.X, area.CellEnd.X), Mathf.Min(area.CellStart.Y, area.CellEnd.Y));
            Vector2I hi = new(Mathf.Max(area.CellStart.X, area.CellEnd.X), Mathf.Max(area.CellStart.Y, area.CellEnd.Y));

            float x0 = lo.X * cs + area.MarginFromWalls;
            float z0 = lo.Y * cs + area.MarginFromWalls;
            float x1 = (hi.X + 1) * cs - area.MarginFromWalls;
            float z1 = (hi.Y + 1) * cs - area.MarginFromWalls;
            if (x1 <= x0 || z1 <= z0) continue;

            var rect = new Rect2(new Vector2(x0, z0), new Vector2(x1 - x0, z1 - z0));
            List<ScatterInstance> instances = ScatterField.GenerateGrid(
                area.Prop, rect, area.Spacing, Mathf.DegToRad(area.FacingDegrees), _ => 0.0f);

            ScatterField.Emit(instances, batch, visual, collision, owner, genFolder, $"Furnish{index}");
            index++;
        }
    }
}
