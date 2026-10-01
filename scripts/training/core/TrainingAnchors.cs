using Godot;
using System.Collections.Generic;

public static class TrainingAnchors
{
    public const string Group = "training_anchor";
    public const string ZoneGroup = "training_zone";

    public const string Hub = "hub";
    public const string AimRange = "aim_range";
    public const string MoveCourse = "move_course";
    public const string CombatYard = "combat_yard";
    public const string DrivingCourse = "driving_course";
    public const string Road = "road";
    public const string BreachSite = "breach_site";
    public const string MapZones = "map_zones";

    public static readonly string[] Ids =
    {
        Hub, AimRange, MoveCourse, CombatYard, DrivingCourse, Road, BreachSite, MapZones,
    };

    /// <summary>Placeholder layout used when the map provides no anchor. Spread out so props from one drill never touch another.</summary>
    public static readonly Dictionary<string, Vector3> DefaultPositions = new()
    {
        { Hub, new Vector3(0, 0, 0) },
        { AimRange, new Vector3(-110, 0, 20) },
        { MoveCourse, new Vector3(110, 0, 20) },
        { CombatYard, new Vector3(0, 0, -120) },
        { DrivingCourse, new Vector3(0, 0, 130) },
        { Road, new Vector3(-150, 0, 100) },
        { BreachSite, new Vector3(150, 0, -120) },
        { MapZones, new Vector3(150, 0, 110) },
    };

    /// <summary>World transform of an anchor: the map's TrainingAnchor if present, else the placeholder position (facing -Z).</summary>
    public static Transform3D Get(SceneTree tree, string anchorId)
    {
        foreach (Node node in tree.GetNodesInGroup(Group))
            if (node is TrainingAnchor anchor && anchor.AnchorId == anchorId && anchor.IsInsideTree())
                return anchor.GlobalTransform;

        Vector3 fallback = DefaultPositions.TryGetValue(anchorId, out Vector3 pos) ? pos : Vector3.Zero;
        return new Transform3D(Basis.Identity, fallback);
    }

    public static bool HasMapAnchor(SceneTree tree, string anchorId)
    {
        foreach (Node node in tree.GetNodesInGroup(Group))
            if (node is TrainingAnchor anchor && anchor.AnchorId == anchorId) return true;
        return false;
    }

    public static List<TrainingZone> GetZones(SceneTree tree)
    {
        var zones = new List<TrainingZone>();
        foreach (Node node in tree.GetNodesInGroup(ZoneGroup))
            if (node is TrainingZone zone && zone.IsInsideTree()) zones.Add(zone);
        return zones;
    }
}
