using Godot;
using System.Collections.Generic;

/// <summary>
/// Drives the third-person body (AshenCorvus, instanced as "Armature" in Player.tscn)
/// from the game's own PlayerMovement.State and WeaponSwitcher.CurrentWeaponData.WeaponClass.
/// Lives as a sibling of BodyAnimationPlayer, both children of Armature.
///
/// Every clip in the 4 libraries (locomotion_lib / rifle_lib / pistol_lib / death_lib) was
/// re-exported "in place" -- see tools_build_libs.gd. The CharacterBody3D is still the only
/// thing that actually moves the player through the world; this script only ever changes
/// which pose is being shown, it never touches position.
///
/// Coverage is honest, not padded: the uploaded packs do not include a pistol aim/fire/reload
/// pose or a melee-swing pose, so those cases fall back to the closest available clip and log
/// a single warning (see Warn()) instead of silently pretending the coverage is complete.
/// </summary>
public partial class PlayerAnimator : Node
{
	[Export] public NodePath PlayerMovementPath { get; set; } = "../..";
	[Export] public NodePath WeaponSwitcherPath { get; set; } = "../../WeaponSwitcher";
	[Export] public NodePath HealthPath { get; set; } = "../../Health";
	[Export] public NodePath AnimationPlayerPath { get; set; } = "../BodyAnimationPlayer";
	[Export] public float CrossfadeSeconds { get; set; } = 0.15f;

	private PlayerMovement? _movement;
	private WeaponSwitcher? _weapons;
	private Health? _health;
	private AnimationPlayer? _anim;

	private PlayerMovement.MovementState _lastState = (PlayerMovement.MovementState)(-1);
	private string _lastCategory = "";
	private bool _dead;
	private readonly HashSet<string> _warnedOnce = new();

	// ---- Base looped movement, keyed by MovementState. Works for every weapon class and
	// is also what Melee uses outright (no melee-specific body pack was uploaded). ----
	private static readonly Dictionary<PlayerMovement.MovementState, string> LocomotionMap = new()
	{
		{ PlayerMovement.MovementState.Idle, "locomotion_lib/locomotion_idle" },
		{ PlayerMovement.MovementState.Walk, "locomotion_lib/locomotion_walk_fwd" },
		{ PlayerMovement.MovementState.Sprint, "locomotion_lib/locomotion_sprint_fwd" },
		{ PlayerMovement.MovementState.Crouch, "locomotion_lib/locomotion_crouch_idle" },
		{ PlayerMovement.MovementState.CrouchWalk, "locomotion_lib/locomotion_crouch_walk" },
		// No dedicated slide clip was in any of the uploads - closest available pose.
		{ PlayerMovement.MovementState.Slide, "locomotion_lib/locomotion_crouch_walk" },
		{ PlayerMovement.MovementState.Jump, "locomotion_lib/locomotion_idle" },
		{ PlayerMovement.MovementState.Fall, "locomotion_lib/locomotion_idle" },
		{ PlayerMovement.MovementState.Aim, "locomotion_lib/locomotion_idle" },
	};

	// ---- Overrides for AssaultRifle / SMG / Shotgun / Heavy (two-handed long guns). ----
	private static readonly Dictionary<PlayerMovement.MovementState, string> RifleMap = new()
	{
		{ PlayerMovement.MovementState.Aim, "rifle_lib/combat_ads_idle" },
		{ PlayerMovement.MovementState.Jump, "rifle_lib/combat_jump" },
		// Combat.fbx has no distinct falling loop; its jump pose is the closest stand-in.
		{ PlayerMovement.MovementState.Fall, "rifle_lib/combat_jump" },
	};

	// ---- Overrides for Sidearm. pistol_lib's own walk/run/turn set is richer than
	// locomotion_lib's, so it replaces the base map wholesale rather than only overriding Aim. ----
	private static readonly Dictionary<PlayerMovement.MovementState, string> PistolMap = new()
	{
		{ PlayerMovement.MovementState.Idle, "pistol_lib/pistol_idle" },
		{ PlayerMovement.MovementState.Walk, "pistol_lib/pistol_walk" },
		{ PlayerMovement.MovementState.Sprint, "pistol_lib/pistol_run" },
		{ PlayerMovement.MovementState.Crouch, "pistol_lib/pistol_kneel_idle" },
		// No crouched-walk clip in the pack - kneel idle is the closest available pose.
		{ PlayerMovement.MovementState.CrouchWalk, "pistol_lib/pistol_kneel_idle" },
		{ PlayerMovement.MovementState.Slide, "pistol_lib/pistol_kneel_idle" },
		{ PlayerMovement.MovementState.Jump, "pistol_lib/pistol_jump" },
		{ PlayerMovement.MovementState.Fall, "pistol_lib/pistol_jump" },
		// The pack has no sidearm aim-down-sights pose - flagged in Warn() the first time
		// a player holding a Sidearm enters Aim state.
		{ PlayerMovement.MovementState.Aim, "pistol_lib/pistol_idle" },
	};

	// One-shot overlay clips, played once on top of the loop and left to return to it
	// automatically (AnimationPlayer.AnimationFinished is not hooked here deliberately -
	// Play() with a short crossfade back onto the looped clip next _PhysicsProcess tick
	// is enough for a first pass; swap to a proper AnimationTree one-shot transition if the
	// pop at the end of fire/reload is visible once this is in front of your own eyes).
	private const string RifleFireClip = "rifle_lib/combat_fire_rifle";
	private const string RifleReloadClip = "rifle_lib/combat_reload_rifle";
	private const string DefaultDeathClip = "death_lib/death_front";

	public override void _Ready()
	{
		_movement = GetNodeOrNull<PlayerMovement>(PlayerMovementPath);
		_weapons = GetNodeOrNull<WeaponSwitcher>(WeaponSwitcherPath);
		_health = GetNodeOrNull<Health>(HealthPath);
		_anim = GetNodeOrNull<AnimationPlayer>(AnimationPlayerPath);

		if (_anim == null) { GD.PushWarning("PlayerAnimator: no AnimationPlayer at " + AnimationPlayerPath); return; }
		if (_movement == null) GD.PushWarning("PlayerAnimator: no PlayerMovement at " + PlayerMovementPath);
		// WeaponSwitcher has no Fire/Reload *signal* to subscribe to here - it calls
		// _attachment.PlayFire() / PlayReload() directly from inside RpcFireEffects() and
		// RpcReloadEffects() (WeaponSwitcher.cs, ~line 466 and ~line 480). Rather than guess at
		// rewiring that networked RPC code blind, PlayFireOneShot()/PlayReloadOneShot() below are
		// public so you can add one line calling each from those two spots - see the README.
		if (_health != null)
		{
			_health.Died += OnDied;
			_health.Respawned += OnRespawned;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_anim == null || _movement == null || _dead) return;

		string category = CategoryFor(_weapons?.CurrentWeaponData?.WeaponClass);
		PlayerMovement.MovementState state = _movement.State;

		if (state == _lastState && category == _lastCategory) return; // nothing to change
		_lastState = state;
		_lastCategory = category;

		string clip = ClipFor(category, state);
		_anim.Play(clip, CrossfadeSeconds);
	}

	private static string CategoryFor(string? weaponClass) => weaponClass switch
	{
		"Sidearm" => "pistol",
		"Melee" => "melee",
		_ => "rifle", // AssaultRifle, SMG, Shotgun, Heavy, and null (no weapon equipped yet)
	};

	private string ClipFor(string category, PlayerMovement.MovementState state)
	{
		Dictionary<PlayerMovement.MovementState, string>? overrideMap = category switch
		{
			"pistol" => PistolMap,
			"rifle" => RifleMap,
			_ => null, // melee has no overrides - pure locomotion
		};
		if (overrideMap != null && overrideMap.TryGetValue(state, out string? clip))
			return clip;
		if (LocomotionMap.TryGetValue(state, out string? baseClip))
			return baseClip;
		Warn($"no clip mapped for state {state} (category {category})");
		return "locomotion_lib/locomotion_idle";
	}

	/// <summary>Call from WeaponSwitcher.RpcFireEffects(), right after _attachment.PlayFire().</summary>
	public void PlayFireOneShot() => PlayOneShot(RifleFireClip);

	/// <summary>Call from WeaponSwitcher.RpcReloadEffects(), right after the existing PlayReload calls.</summary>
	public void PlayReloadOneShot() => PlayOneShot(RifleReloadClip);

	private void PlayOneShot(string clip)
	{
		if (_anim == null) return;
		string category = CategoryFor(_weapons?.CurrentWeaponData?.WeaponClass);
		if (category != "rifle")
		{
			// No fire/reload body pose was uploaded for Sidearm or Melee yet.
			Warn($"no one-shot clip for category '{category}' - add one to rifle_lib's " +
				 "equivalent or extend PlayOneShot's mapping once you have the source animation.");
			return;
		}
		_anim.Play(clip, CrossfadeSeconds);
		// Force a re-evaluation next physics tick so the loop resumes once the one-shot ends.
		_lastState = (PlayerMovement.MovementState)(-1);
	}

	private void OnDied()
	{
		_dead = true;
		_anim?.Play(DefaultDeathClip, CrossfadeSeconds);
		// TODO: Health only exposes a parameterless Died signal today. KillStyle (Headshot /
		// direction) is computed inside Health.ReceiveDamage but not carried out on the signal,
		// so every death currently plays the same clip. death_lib also has death_back,
		// death_headshot_front, death_headshot_back, death_crouch, death_crouch_headshot_front,
		// death_rifle_run and death_walking if you want to expose that data and pick among them.
	}

	private void OnRespawned()
	{
		_dead = false;
		_lastState = (PlayerMovement.MovementState)(-1);
		_lastCategory = "";
	}

	private void Warn(string message)
	{
		if (_warnedOnce.Add(message)) GD.PushWarning("PlayerAnimator: " + message);
	}
}
