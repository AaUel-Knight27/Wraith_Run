using Godot;

/// <summary>
/// Autoload. Everything about how a gunshot actually reaches a listener that is NOT "which clip
/// plays": real inverse-square range falloff, a directional muzzle blast, wall occlusion, and the
/// crack-then-boom delay real gunfire has past close range. WeaponAttachment.PlayFire() calls
/// PlayWeaponFire() here instead of AudioStreamPlayer3D.Play() directly - that is the only
/// integration point (see the README's WeaponAttachment.cs patch).
///
/// What this deliberately does NOT do: fake "priority" by scripting one sound over another when a
/// gun fires. The reason a nearby gunshot should mask a footstep is a native AudioEffectCompressor
/// on the Movement bus, sidechained from the Weapons bus (see default_bus_layout.tres) - it reacts
/// to the actual mixed, distance-attenuated signal reaching the listener, so a shot far away barely
/// ducks anything and a shot next to you ducks hard. That is real auditory masking, and it needs no
/// code here at all - only the right sounds on the right bus (also the WeaponAttachment patch).
/// </summary>
public partial class SoundManager : Node
{
	public static SoundManager Instance { get; private set; } = null!;

	// ---------------------------------------------------------------------------------------
	// Range. A real rifle is audible for kilometres - unusable directly on a match-sized map.
	// MaxDistanceMultiplier compresses every weapon's audible range by the same factor, which
	// keeps their RELATIVE reach intact (a Bazooka still carries ~8x further than a Glock,
	// because that is what the real dB gap between them implies - see the README's inverse-
	// square derivation for where each weapon's FireAudioUnitSize actually comes from). Raise
	// this one constant if your finished map is bigger than a few hundred metres across.
	// ---------------------------------------------------------------------------------------
	public const float MaxDistanceMultiplier = 12.0f;

	/// <summary>Metres at which MaxDistanceMultiplier suggests capping a weapon with this
	/// UnitSize. Exposed so the sound tuner (and anyone re-deriving numbers by hand) does not
	/// have to hardcode the multiplier a second time.</summary>
	public static float RecommendedMaxDistance(float unitSize) => unitSize * MaxDistanceMultiplier;

	// A shot fired within this many metres of the listener is heard the instant it fires - real
	// near-field propagation delay is a few milliseconds, not worth modelling and it would only
	// ever fight the shooter's own instinctive sense that their gun fired NOW. Past this radius,
	// ApplyReportDelay takes over. 343 m/s is the speed of sound in dry air at ~20C.
	private const float NearFieldInstantRadius = 15.0f;
	private const float SpeedOfSoundMps = 343.0f;

	// Occlusion. One raycast per shot, not per frame - a one-shot sample does not need updating
	// after it has already started playing, so there is no reason to pay for it continuously.
	// If the muzzle-to-listener line is blocked by world geometry, the shot plays quieter and
	// duller - the way a shot through a wall actually sounds, rather than identical-but-fainter.
	private const float OccludedVolumeDb = -13.0f;
	private const float OccludedFilterMultiplier = 0.4f;

	public override void _Ready() => Instance = this;

	/// <summary>Single entry point for a weapon's fire sound. Applies the weapon's live-tuned
	/// acoustics, checks occlusion once, and - past NearFieldInstantRadius - delays playback by
	/// distance / speed-of-sound so a shot heard from 150 m away arrives about 0.4 s after its
	/// muzzle flash, same as a real one. Safe to call every shot; re-applying the weapon's
	/// numbers each time is what makes the sound tuner's live edits audible on the very next
	/// shot with no re-equip.</summary>
	public void PlayWeaponFire(AudioStreamPlayer3D player, WeaponData weapon, Vector3 muzzleWorldPosition)
	{
		if (player == null || weapon == null) return;

		ApplyAcoustics(player, weapon);

		Camera3D? listener = player.GetViewport()?.GetCamera3D();
		float distance = listener == null ? 0.0f : listener.GlobalPosition.DistanceTo(muzzleWorldPosition);
		ApplyOcclusion(player, muzzleWorldPosition, listener, distance);

		if (!weapon.FireReportDelayEnabled || listener == null || distance <= NearFieldInstantRadius)
		{
			player.Play();
			return;
		}

		float delaySeconds = (distance - NearFieldInstantRadius) / SpeedOfSoundMps;
		var timer = player.GetTree().CreateTimer(delaySeconds);
		timer.Timeout += () =>
		{
			if (GodotObject.IsInstanceValid(player)) player.Play();
		};
	}

	/// <summary>Pushes the weapon's tuned numbers onto the node. Cheap (a handful of float/bool
	/// assignments), so it is fine to redo on every shot rather than caching.</summary>
	private static void ApplyAcoustics(AudioStreamPlayer3D player, WeaponData weapon)
	{
		player.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseSquareDistance;
		player.UnitSize = weapon.FireAudioUnitSize;
		player.MaxDistance = weapon.FireMaxDistanceMeters;
		player.VolumeDb = weapon.FireVolumeDb;

		// Baseline air-absorption roll-off (Godot's own distance low-pass). Occlusion below
		// multiplies this down further when the shot is behind geometry.
		player.AttenuationFilterCutoffHz = 5000.0f;
		player.AttenuationFilterDb = -24.0f;

		// 360 is the sentinel for "no cone / omnidirectional" - see WeaponData's field doc for
		// why the Bazooka deliberately leaves this off (backblast means it is not actually
		// quieter behind the tube, so a forward-only cone would be a *less* real approximation).
		bool directional = weapon.FireEmissionAngleDegrees < 360.0f;
		player.EmissionAngleEnabled = directional;
		if (directional)
		{
			player.EmissionAngleDegrees = weapon.FireEmissionAngleDegrees;
			player.EmissionAngleFilterAttenuationDb = weapon.FireEmissionOffAxisAttenuationDb;
		}
	}

	private static void ApplyOcclusion(AudioStreamPlayer3D player, Vector3 muzzleWorldPosition, Camera3D? listener, float distance)
	{
		if (listener == null || distance < 1.0f) return; // the shooter hearing their own gun is never occluded

		var spaceState = player.GetWorld3D().DirectSpaceState;
		var query = PhysicsRayQueryParameters3D.Create(muzzleWorldPosition, listener.GlobalPosition);
		var result = spaceState.IntersectRay(query);
		if (result.Count == 0) return; // clear line of sight

		// A CharacterBody3D in the way is another player's body, not a wall - real gunfire is not
		// meaningfully muffled by a person standing near the line, only world geometry occludes.
		if (result["collider"].As<Node>() is CharacterBody3D) return;

		player.VolumeDb += OccludedVolumeDb;
		player.AttenuationFilterCutoffHz *= OccludedFilterMultiplier;
	}
}
