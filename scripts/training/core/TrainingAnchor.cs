using Godot;
using System.Collections.Generic;

/// <summary>
/// THE MAP CONTRACT. Drop these into your own training map and the drills use your layout instead of the
/// placeholder one - no code changes:
///
///  * TrainingAnchor (Marker3D) with AnchorId set to one of TrainingAnchors.Ids. The marker's position
///    and its facing (-Z is "forward", the direction targets/courses run) are used.
///  * TrainingZone (Node3D) for map reading: ZoneName, half-size, and colour. If you place no zones the
///    map-reading drill builds a 3x3 grid of coloured, named zones around the "map_zones" anchor.
///
/// Anything you do not provide falls back to DefaultPositions, so a half-finished map still trains.
/// </summary>
[GlobalClass]
public partial class TrainingAnchor : Marker3D
{
    [Export] public string AnchorId { get; set; } = "hub";

    public override void _Ready() => AddToGroup(TrainingAnchors.Group);
}
