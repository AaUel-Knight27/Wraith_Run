#nullable enable
using Godot;

namespace WraithRun.MapKit;

/// <summary>
/// A floorplan drawn as ASCII art, one string per row, read top-to-bottom. This is the one authoring
/// format for every indoor space the kit builds — a classroom, a lab, a museum wing, a ship's
/// corridors, a train station hall — you just draw a different grid and pick different SurfaceLayers.
///
/// Legend (fixed):
///   '#'  solid wall block, fills the whole cell floor-to-ceiling
///   'W'  solid block like '#', but textured with WindowSurface instead of WallSurface
///   '.'  open floor — walkable interior
///   'D'  open floor, same as '.', but marked as a doorway (added to the "door" group)
///   ' '  (space) void — nothing is built here, so buildings can be L-shaped or split into wings
///
/// Rows don't need to be the same length; short rows are treated as void past their end.
/// </summary>
[Tool]
[GlobalClass]
public partial class RoomLayout : Resource
{
    [Export] public string[] Rows { get; set; } = System.Array.Empty<string>();

    [Export(PropertyHint.Range, "0.5,10,0.1")]
    public float CellSize { get; set; } = 3.0f;

    [Export(PropertyHint.Range, "1.5,12,0.1")]
    public float WallHeight { get; set; } = 3.0f;

    public int Width
    {
        get
        {
            int w = 0;
            foreach (string row in Rows) w = Mathf.Max(w, row.Length);
            return w;
        }
    }

    public int Depth => Rows.Length;

    public char CellAt(int col, int row)
    {
        if (row < 0 || row >= Rows.Length) return ' ';
        string r = Rows[row];
        return col < 0 || col >= r.Length ? ' ' : r[col];
    }

    public static bool IsSolid(char c) => c is '#' or 'W';
    public static bool IsOpen(char c) => c is '.' or 'D';
}
