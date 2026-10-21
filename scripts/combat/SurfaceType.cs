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
	// Added for the footstep/landing surface pack (scripts/player/PlayerAudio.cs). Appended after
	// Glass rather than reordered, so the existing 0-3 values (and anything that already persisted
	// or replicated them) do not shift. See WeaponSwitcher.RpcGeometryImpact for the one place that
	// bounds-checks this enum's range over the network - it was widened alongside this change.
	Concrete,
	Dirt,
	Tile,
	Water,
}

public static class SurfaceClassifier
{
	private const string MetalGroup = "surface_metal";
	private const string WoodGroup = "surface_wood";
	private const string GlassGroup = "surface_glass";
	private const string ConcreteGroup = "surface_concrete";
	private const string DirtGroup = "surface_dirt";
	private const string TileGroup = "surface_tile";
	private const string WaterGroup = "surface_water";

	public static SurfaceType Classify(Node? collider)
	{
		for (Node? n = collider; n != null; n = n.GetParent())
		{
			if (n.IsInGroup(MetalGroup)) return SurfaceType.Metal;
			if (n.IsInGroup(WoodGroup)) return SurfaceType.Wood;
			if (n.IsInGroup(GlassGroup)) return SurfaceType.Glass;
			if (n.IsInGroup(ConcreteGroup)) return SurfaceType.Concrete;
			if (n.IsInGroup(DirtGroup)) return SurfaceType.Dirt;
			if (n.IsInGroup(TileGroup)) return SurfaceType.Tile;
			if (n.IsInGroup(WaterGroup)) return SurfaceType.Water;
		}

		string name = collider?.Name.ToString().ToLowerInvariant() ?? string.Empty;
		if (ContainsAny(name, "container", "drum", "pipe", "barrel", "metal", "steel", "tank"))
			return SurfaceType.Metal;
		if (ContainsAny(name, "crate", "wood", "pallet", "plank", "timber", "fence"))
			return SurfaceType.Wood;
		if (ContainsAny(name, "glass", "window"))
			return SurfaceType.Glass;
		if (ContainsAny(name, "dirt", "mud", "sand", "gravel", "rubble", "debris"))
			return SurfaceType.Dirt;
		if (ContainsAny(name, "tile", "ceramic", "bathroom"))
			return SurfaceType.Tile;
		if (ContainsAny(name, "water", "puddle", "flood", "pool", "lake", "river"))
			return SurfaceType.Water;
		if (ContainsAny(name, "concrete", "pavement", "asphalt", "road", "sidewalk"))
			return SurfaceType.Concrete;
		return SurfaceType.Generic;
	}

	private static bool ContainsAny(string haystack, params string[] needles)
	{
		foreach (string needle in needles)
			if (haystack.Contains(needle)) return true;
		return false;
	}
}
