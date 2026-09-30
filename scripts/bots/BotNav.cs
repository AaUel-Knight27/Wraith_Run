using Godot;
using System.Collections.Generic;

/// <summary>Holds the current match's NavGrid so every bot (and Health, for respawns) can reach it.</summary>
public static class BotNav
{
	public static NavGrid? Grid { get; set; }

	public static void Clear() => Grid = null;

	/// <summary>
	/// Where a bot comes back after dying: a random walkable spot far from everyone still alive and out of
	/// their sight, so a respawn is never an instant free kill for either side. Falls back to the normal
	/// respawn selector if there is no grid.
	/// </summary>
	public static Vector3 PickRespawnPosition(CharacterBody3D bot, Vector3 deathPosition, float fallbackHalfExtent, RandomNumberGenerator rng)
	{
		NavGrid? grid = Grid;
		if (grid == null || grid.WalkableCount == 0)
			return RespawnPointSelector.Select(bot, deathPosition, fallbackHalfExtent);

		var others = new List<Vector3>();
		foreach (Node n in bot.GetTree().GetNodesInGroup("players"))
		{
			if (n == bot || n is not Node3D other) continue;
			var hp = n.GetNodeOrNull<Health>("Health");
			if (hp != null && hp.IsDead) continue;
			others.Add(other.GlobalPosition);
		}

		Vector3 best = grid.RandomWalkable(rng);
		float bestScore = float.MinValue;
		for (int i = 0; i < 30; i++)
		{
			Vector3 c = grid.RandomWalkable(rng);
			float nearest = 1000.0f;
			bool seen = false;
			for (int k = 0; k < others.Count; k++)
			{
				float dx = c.X - others[k].X, dz = c.Z - others[k].Z;
				float d = Mathf.Sqrt(dx * dx + dz * dz);
				nearest = Mathf.Min(nearest, d);
				if (d < 45.0f && grid.HasLos(c, 1.5f, others[k], 1.5f)) seen = true;
			}
			float fromDeath = Mathf.Sqrt((c.X - deathPosition.X) * (c.X - deathPosition.X) + (c.Z - deathPosition.Z) * (c.Z - deathPosition.Z));
			float score = Mathf.Min(nearest, 40.0f) + 0.4f * Mathf.Min(fromDeath, 50.0f) - (seen ? 30.0f : 0.0f) + rng.RandfRange(0.0f, 6.0f);
			if (score > bestScore) { bestScore = score; best = c; }
		}
		best.Y += 0.1f;
		return best;
	}
}
