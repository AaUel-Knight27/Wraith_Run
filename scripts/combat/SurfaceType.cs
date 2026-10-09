using Godot;

/// <summary>
/// What a bullet's geometry impact point is made of, used to pick a materially-appropriate impact
/// sound (and whether a ricochet layer makes sense) instead of every wall, crate and shipping
/// container thudding with the exact same six pistol-impact clips.
///
/// Honest limitation: the map kit has no per-material physics tagging today.
/// MeshBatcher.BuildCollision (mapkit/Core/MeshBatcher.cs) merges every triangle sharing a map
/// chunk - wall, floor, ruin, regardless of material - into one ConcavePolygonShape3D, so most of
/// the map's collision geometry has no material identity left to read at runtime. Classify() below
/// is a best-effort guess, not a guaranteed-accurate material read:
///  - The exact, reliable route is a Godot group. Add a prop (or any of its ancestors) to
///    "surface_metal", "surface_wood" or "surface_glass" in the editor's Groups panel and this
///    always wins.
///  - Failing that, it falls back to guessing from the collider's own node name, which already
///    can classify a discrete prop when its name includes a material clue (for example, a steel
///    door or barrel), since those are separate scene nodes rather than batched geometry.
///  - Everything else - the procedurally-built walls/floors/ruins in all three maps, which carry no
///    material identity at all once MeshBatcher merges them - falls through to Generic. That's
///    still a real upgrade over the single shared pistol-impact thud every surface used before this
///    pass, just not a "the game knows it's concrete" upgrade.
/// </summary>
public enum SurfaceType
{
	Generic,
	Metal,
	Wood,
	Glass,
}

public static class SurfaceClassifier
{
	private const string MetalGroup = "surface_metal";
	private const string WoodGroup = "surface_wood";
	private const string GlassGroup = "surface_glass";

	public static SurfaceType Classify(Node? collider)
	{
		for (Node? n = collider; n != null; n = n.GetParent())
		{
			if (n.IsInGroup(MetalGroup)) return SurfaceType.Metal;
			if (n.IsInGroup(WoodGroup)) return SurfaceType.Wood;
			if (n.IsInGroup(GlassGroup)) return SurfaceType.Glass;
		}

		string name = collider?.Name.ToString().ToLowerInvariant() ?? string.Empty;
		if (ContainsAny(name, "container", "drum", "pipe", "barrel", "metal", "steel", "tank"))
			return SurfaceType.Metal;
		if (ContainsAny(name, "crate", "wood", "pallet", "plank", "timber", "fence"))
			return SurfaceType.Wood;
		if (ContainsAny(name, "glass", "window"))
			return SurfaceType.Glass;
		return SurfaceType.Generic;
	}

	private static bool ContainsAny(string haystack, params string[] needles)
	{
		foreach (string needle in needles)
			if (haystack.Contains(needle)) return true;
		return false;
	}
}
