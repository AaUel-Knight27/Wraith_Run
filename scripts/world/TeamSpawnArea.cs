using Godot;

/// <summary>
/// Team Deathmatch spawn zones. Unlike Free-For-All (fully random, see RespawnPointSelector.
/// SelectAnywhere), a team match needs each side to come back on its own side of the map - so this
/// looks up a rectangular area placed by hand in the map scene instead of picking a point anywhere.
///
/// A map opts in by adding one Node3D per team, positioned at the zone's centre, in the group
/// "team_spawn_area_0" (Alpha) or "team_spawn_area_1" (Bravo) - matching MatchManager's team indices
/// so no name lookup is needed. The zone's half-width/half-depth on the X/Z plane is read from that
/// node's "half_extent" metadata (a Vector2); if a map has the group but no metadata, a small default
/// area is used instead of failing outright. See Prison.tscn's "TeamSpawnAreas" node for the pattern
/// to copy onto a new map - two of these, one on each side, is all a map needs to support the mode.
///
/// If a map has no areas for the current team at all (an older or Free-For-All-only map loaded into a
/// Team Deathmatch match), <see cref="TryGet"/> returns false and the caller is expected to fall back
/// to RespawnPointSelector.SelectAnywhere rather than leaving the player stranded.
/// </summary>
public static class TeamSpawnArea
{
	private const string GroupPrefix = "team_spawn_area_";
	private const string HalfExtentMetaKey = "half_extent";

	/// <summary>Used only when a team's area node exists but has no "half_extent" metadata set.</summary>
	private static readonly Vector2 DefaultHalfExtent = new(6.0f, 6.0f);

	/// <summary>
	/// Looks up the spawn area for <paramref name="team"/> (MatchManager's 0 = Alpha, 1 = Bravo) using
	/// <paramref name="context"/> to reach the scene tree. Returns false if the current map defines no
	/// area for that team.
	/// </summary>
	public static bool TryGet(Node context, int team, out Vector3 center, out Vector2 halfExtent)
	{
		center = Vector3.Zero;
		halfExtent = DefaultHalfExtent;

		if (team < 0) return false;
		var nodes = context.GetTree().GetNodesInGroup(GroupPrefix + team);
		if (nodes.Count == 0 || nodes[0] is not Node3D marker) return false;

		center = marker.GlobalPosition;
		if (marker.HasMeta(HalfExtentMetaKey)) halfExtent = marker.GetMeta(HalfExtentMetaKey).AsVector2();
		return true;
	}
}
