using Godot;

/// <summary>
/// Chooses where a dead player re-enters the match (FR-PL-06: "at least 50 metres from the death
/// location").
///
/// Random candidates are sampled across the match area and then validated with a downward raycast,
/// so a respawn only lands on real, collidable ground. A candidate at or beyond the required
/// distance wins outright. If the map cannot offer 50m at all - the current 40x40 test floor in
/// GameWorld.tscn cannot - the farthest validated point is used instead of dropping the player into
/// the void, and the shortfall is reported with a warning so it is visible while the world is still
/// a test box rather than a real map.
/// </summary>
public static class RespawnPointSelector
{
	/// <summary>FR-PL-06 minimum separation between the death location and the respawn point.</summary>
	public const float MinimumDistanceMetres = 50.0f;

	private const float GroundRayStartHeight = 50.0f;
	private const float GroundRayLength = 100.0f;
	private const float SpawnGroundClearance = 0.1f;
	private const int DefaultCandidateCount = 32;

	public static Vector3 Select(CharacterBody3D player, Vector3 deathPosition, float halfExtent,
		int candidateCount = DefaultCandidateCount)
	{
		var rng = new RandomNumberGenerator();
		rng.Randomize();

		Vector3 farthestCandidate = deathPosition;
		float farthestDistance = -1.0f;

		Vector3 bestGrounded = deathPosition;
		float bestGroundedDistance = -1.0f;
		bool hasGrounded = false;

		Vector3 bestQualifying = deathPosition;
		float bestQualifyingDistance = -1.0f;
		bool hasQualifying = false;

		for (int i = 0; i < candidateCount; i++)
		{
			var candidate = new Vector3(rng.RandfRange(-halfExtent, halfExtent), 0.0f,
				rng.RandfRange(-halfExtent, halfExtent));
			float distance = HorizontalDistance(candidate, deathPosition);

			if (distance > farthestDistance)
			{
				farthestDistance = distance;
				farthestCandidate = candidate;
			}

			if (!TryFindGround(player, candidate, out Vector3 grounded)) continue;

			if (distance > bestGroundedDistance)
			{
				bestGroundedDistance = distance;
				bestGrounded = grounded;
				hasGrounded = true;
			}

			if (distance >= MinimumDistanceMetres && distance > bestQualifyingDistance)
			{
				bestQualifyingDistance = distance;
				bestQualifying = grounded;
				hasQualifying = true;
			}
		}

		if (hasQualifying) return bestQualifying;
		if (hasGrounded)
		{
			GD.PushWarning($"Respawn point {bestGroundedDistance:F1}m from the death location is below the " +
				$"{MinimumDistanceMetres:F0}m FR-PL-06 minimum - the match area is too small to satisfy it.");
			return bestGrounded;
		}

		// No collidable ground anywhere under the sampled area: fall back to the old behaviour rather
		// than leaving the player suspended, and say so loudly.
		GD.PushWarning("Respawn point search found no collidable ground; falling back to a flat spawn position.");
		return new Vector3(farthestCandidate.X, SpawnGroundClearance, farthestCandidate.Z);
	}

	private static float HorizontalDistance(Vector3 a, Vector3 b) =>
		new Vector2(a.X - b.X, a.Z - b.Z).Length();

	private static bool TryFindGround(CharacterBody3D player, Vector3 candidate, out Vector3 grounded)
	{
		grounded = candidate;
		World3D? world = player.GetWorld3D();
		if (world == null) return false;

		Vector3 from = candidate + Vector3.Up * GroundRayStartHeight;
		Vector3 to = candidate - Vector3.Up * GroundRayLength;
		var query = PhysicsRayQueryParameters3D.Create(from, to,
			exclude: new Godot.Collections.Array<Rid> { player.GetRid() });

		var result = world.DirectSpaceState.IntersectRay(query);
		if (result.Count == 0) return false;

		var hit = (Vector3)result["position"];
		grounded = new Vector3(hit.X, hit.Y + SpawnGroundClearance, hit.Z);
		return true;
	}
}
