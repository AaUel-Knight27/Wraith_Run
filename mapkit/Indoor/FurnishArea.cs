#nullable enable
using Godot;

namespace WraithRun.MapKit;

/// <summary>
/// Fills a rectangle of a RoomLayout's grid with evenly spaced copies of one prop — classroom desk
/// rows, museum display cases, lab benches, station seating. CellStart/CellEnd are inclusive grid
/// coordinates (col, row), so a 4-wide by 3-deep block of desks is CellStart (1,1), CellEnd (4,3).
/// </summary>
[Tool]
[GlobalClass]
public partial class FurnishArea : Resource
{
    [Export] public PropDefinition? Prop { get; set; }
    [Export] public Vector2I CellStart { get; set; }
    [Export] public Vector2I CellEnd { get; set; } = Vector2I.One;

    [Export(PropertyHint.Range, "0.3,10,0.05")]
    public float Spacing { get; set; } = 1.5f;

    [Export(PropertyHint.Range, "0,3,0.05")]
    public float MarginFromWalls { get; set; } = 0.4f;

    [Export(PropertyHint.Range, "0,360,1,degrees")]
    public float FacingDegrees { get; set; }
}
