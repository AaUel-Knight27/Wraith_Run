using Godot;

/// <summary>
/// Chooses where a player re-enters the match. Two policies live here side by side:
///
///  - <see cref="Select"/>: the original FR-PL-06 rule ("at least 50 metres from the death
///    location"). Kept as-is; BotNav still falls back to it when no nav grid exists yet.
///  - <see cref="SelectAnywhere"/> / <see cref="SelectInArea"/>: the v1.1 policy used by human
///    players. Free-For-All Deathmatch now respawns completely at random anywhere in the match
///    area - no minimum distance from the death location at all, by design (the player can come
///    back in the middle of a fight). Team Deathmatch uses the same random-point logic but
///    restricted to the respawning player's own <see cref="TeamSpawnArea"/> instead of the whole
///    map. Both still validate every candidate with a downward raycast (real, collidable ground,
///    not the top of a wall or crate - see MaxGroundHeight), reject any spot that overlaps world
///    geometry (so nobody spawns inside a wall or prop), and reject any spot too close to another
///    living player (so nobody spawns stacked on top of someone). If nothing in the sample
///    satisfies all of that, the best partial match is used instead of dropping the player into
///    the void, and it is reported with a warning.
///
///    Both take a plain <see cref="Rid"/> to exclude from the physics queries instead of a live
///    player node, on purpose: GameWorld picks a joining player's spot on the server before that
///    player's node exists anywhere at all (see GameWorld.SpawnForPeer), so there is nothing to
///    exclude yet - pass <c>default</c> (the parameter's default) for that case. Health.Respawn, by
///    contrast, already has a live, in-tree player to exclude when it calls these mid-match.
/// </summary>
public static class RespawnPointSelector
{
	/// <summary>FR-PL-06 minimum separation between the death location and the respawn point. Only
	/// <see cref="Select"/> (the original policy) still enforces this.</summary>
	public const float MinimumDistanceMetres = 50.0f;

	/// <summary>Half-extent of the square area a Free-For-All pick is drawn from when nothing more
	/// specific is available (used by both Health.Respawn's SpawnAreaHalfExtent default and
	/// GameWorld's initial join spawn), matched to the current Prison interior spawn footprint. Keep it inside the floor bounds
	/// when the map dimensions change.</summary>
	public const float DefaultMapHalfExtent = 26.0f;

	/// <summary>How far apart two players' respawn points must be so a random pick never lands one
	/// player inside or touching another (player capsule radius is 0.35m, so 2m leaves plenty of room).</summary>
	private const float MinimumPlayerSeparation = 2.0f;

	/// <summary>Capsule used to test a candidate for overlap with world geometry. Slightly smaller
	/// than the real player capsule (radius 0.35, height 1.8 in Player.tscn) and lifted a little off
	/// the ground so the check never flags the flat floor immediately underfoot as an obstruction.</summary>
	private const float ObstructionCapsuleRadius = 0.3f;
	private const float ObstructionCapsuleHeight = 1.6f;
	private const float ObstructionCapsuleLift = 0.05f;

	private const float GroundRayStartHeight = 50.0f;
	private const float GroundRayLength = 100.0f;
	private const float SpawnGroundClearance = 0.1f;
	private const float MinGroundHeight = -5.0f;

	/// <summary>The ray comes from high above, so over a wall, pillar or container it lands on the TOP
	/// of it. Playable ground in every map is above -5m and within about a metre of y = 0; anything
	/// outside that band is not somewhere to respawn. The lower bound is essential on maps with a deep
	/// safety floor: without it, the ray mistakes that catch floor for valid ground below the level.</summary>
	private const float MaxGroundHeight = 1.0f;

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

			if (!TryFindGround(player, player.GetRid(), candidate, out Vector3 grounded)) continue;

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

	/// <summary>
	/// Free-For-All Deathmatch: a fully random point anywhere in a square area centred on the world
	/// origin, no minimum distance from the death location. See the class summary for the validation
	/// every candidate still gets and what <paramref name="excludeBody"/> is for.
	/// </summary>
	public static Vector3 SelectAnywhere(Node3D context, float halfExtent, Rid excludeBody = default,
		int candidateCount = DefaultCandidateCount) =>
		SelectInArea(context, Vector3.Zero, new Vector2(halfExtent, halfExtent), excludeBody, candidateCount);

	/// <summary>
	/// Team Deathmatch (and the initial join spawn in both modes): a random point inside one
	/// rectangular area - typically a <see cref="TeamSpawnArea"/> - instead of anywhere on the map.
	/// See the class summary for the validation every candidate still gets and what
	/// <paramref name="excludeBody"/> is for.
	/// </summary>
	public static Vector3 SelectInArea(Node3D context, Vector3 areaCenter, Vector2 areaHalfExtent,
		Rid excludeBody = default, int candidateCount = DefaultCandidateCount)
	{
		var rng = new RandomNumberGenerator();
		rng.Randomize();

		Vector3 bestGrounded = areaCenter;
		bool hasGrounded = false;

		Vector3 bestUnobstructed = areaCenter;
		bool hasUnobstructed = false;

		Vector3 bestClear = areaCenter;
		float bestClearSeparation = -1.0f;
		bool hasClear = false;

		for (int i = 0; i < candidateCount; i++)
		{
			var candidate = new Vector3(
				areaCenter.X + rng.RandfRange(-areaHalfExtent.X, areaHalfExtent.X),
				areaCenter.Y,
				areaCenter.Z + rng.RandfRange(-areaHalfExtent.Y, areaHalfExtent.Y));

			if (!TryFindGround(context, excludeBody, candidate, out Vector3 grounded)) continue;
			if (!hasGrounded) { bestGrounded = grounded; hasGrounded = true; }

			if (IsObstructed(context, excludeBody, grounded)) continue;
			if (!hasUnobstructed) { bestUnobstructed = grounded; hasUnobstructed = true; }

			float separation = NearestAlivePlayerDistance(context, excludeBody, grounded);
			if (separation < MinimumPlayerSeparation) continue;

			if (separation > bestClearSeparation)
			{
				bestClearSeparation = separation;
				bestClear = grounded;
				hasClear = true;
			}
		}

		if (hasClear) return bestClear;
		if (hasUnobstructed)
		{
			GD.PushWarning("Respawn point search found no spot clear of other players; using the " +
				"nearest unobstructed one instead.");
			return bestUnobstructed;
		}
		if (hasGrounded)
		{
			GD.PushWarning("Respawn point search found no spot clear of world geometry; using the " +
				"nearest grounded one instead - the area may be too cluttered.");
			return bestGrounded;
		}
		GD.PushWarning("Respawn point search found no collidable ground in the area; falling back to " +
			"the area centre.");
		return new Vector3(areaCenter.X, SpawnGroundClearance, areaCenter.Z);
	}

	private static Godot.Collections.Array<Rid> ExcludeArray(Rid excludeBody) =>
		excludeBody.Id != 0 ? new Godot.Collections.Array<Rid> { excludeBody } : new Godot.Collections.Array<Rid>();

	/// <summary>True if a player-sized capsule at this (already grounded) spot would overlap world
	/// geometry - a wall, a prop, anything solid other than the ground itself.</summary>
	private static bool IsObstructed(Node3D context, Rid excludeBody, Vector3 groundedFeetPosition)
	{
		World3D? world = context.GetWorld3D();
		if (world == null) return false;

		using var shape = new CapsuleShape3D { Radius = ObstructionCapsuleRadius, Height = ObstructionCapsuleHeight };
		var query = new PhysicsShapeQueryParameters3D
		{
			Shape = shape,
			Transform = new Transform3D(Basis.Identity, groundedFeetPosition +
				Vector3.Up * (ObstructionCapsuleHeight * 0.5f + ObstructionCapsuleLift)),
			CollideWithBodies = true,
			CollideWithAreas = false,
			Exclude = ExcludeArray(excludeBody),
		};
		return world.DirectSpaceState.IntersectShape(query, 1).Count > 0;
	}

	/// <summary>Horizontal distance from this spot to the nearest currently-alive player other than
	/// whichever one owns <paramref name="excludeBody"/> (if any - see the class summary). Dead players
	/// are ignored: their collision is already off, so respawning near a corpse is not a glitch.</summary>
	private static float NearestAlivePlayerDistance(Node3D context, Rid excludeBody, Vector3 position)
	{
		float nearest = float.MaxValue;
		foreach (Node n in context.GetTree().GetNodesInGroup("players"))
		{
			// CollisionObject3D covers CharacterBody3D and is itself a Node3D, so one cast gives
			// both the Rid (to skip the excluded body) and the position we actually need.
			if (n is not CollisionObject3D body) continue;
			if (excludeBody.Id != 0 && body.GetRid() == excludeBody) continue;
			Health? health = n.GetNodeOrNull<Health>("Health");
			if (health != null && health.IsDead) continue;
			float distance = HorizontalDistance(position, body.GlobalPosition);
			if (distance < nearest) nearest = distance;
		}
		return nearest;
	}

	private static float HorizontalDistance(Vector3 a, Vector3 b) =>
		new Vector2(a.X - b.X, a.Z - b.Z).Length();

	private static bool TryFindGround(Node3D context, Rid excludeBody, Vector3 candidate, out Vector3 grounded)
	{
		grounded = candidate;
		World3D? world = context.GetWorld3D();
		if (world == null) return false;

		Vector3 from = candidate + Vector3.Up * GroundRayStartHeight;
		Vector3 to = candidate - Vector3.Up * GroundRayLength;
		var query = PhysicsRayQueryParameters3D.Create(from, to, exclude: ExcludeArray(excludeBody));

		var result = world.DirectSpaceState.IntersectRay(query);
		if (result.Count == 0) return false;

		var hit = (Vector3)result["position"];
		if (hit.Y > MaxGroundHeight || hit.Y < MinGroundHeight) return false;
		grounded = new Vector3(hit.X, hit.Y + SpawnGroundClearance, hit.Z);
		return true;
	}
}
