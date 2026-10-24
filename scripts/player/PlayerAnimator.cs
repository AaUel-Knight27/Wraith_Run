using Godot;
using System.Collections.Generic;

/// <summary>
/// Drives the third-person body (AshenCorvus, instanced as "Armature" in Player.tscn)
/// from the game's own PlayerMovement.State and WeaponSwitcher.CurrentWeaponData.WeaponClass.
/// Lives as a sibling of BodyAnimationPlayer, both children of Armature.
///
/// The clips are in-place, and the CharacterBody3D remains the only thing that moves the player
/// through the world; this script only chooses which pose is shown.
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
	private string _lastClip = "";
	private bool _dead;
	private bool _oneShotActive;
	private readonly HashSet<string> _warnedOnce = new();

	private const string AnimLocomotionIdle = "locomotion_lib/locomotion_idle";
	private const string AnimLocomotionWalkFwd = "locomotion_lib/locomotion_walk_fwd";
	private const string AnimLocomotionSprintFwd = "locomotion_lib/locomotion_sprint_fwd";
	private const string AnimLocomotionCrouchIdle = "locomotion_lib/locomotion_crouch_idle";
	private const string AnimLocomotionCrouchWalk = "locomotion_lib/locomotion_crouch_walk";
	private const string AnimLocomotionWalkBack = "locomotion_lib/locomotion_walk_back";
	private const string AnimLocomotionWalkBackRight = "locomotion_lib/locomotion_walk_back_right";
	private const string AnimLocomotionWalkFwdLeft = "locomotion_lib/locomotion_walk_fwd_left";
	private const string AnimLocomotionWalkFwdRight = "locomotion_lib/locomotion_walk_fwd_right";
	private const string AnimLocomotionStrafeRight = "locomotion_lib/locomotion_strafe_right";

	private const string AnimCrouchWalkBackLeft = "locomotion_lib/locomotion_crouch_walk_back_left";
	private const string AnimCrouchWalkBackRight = "locomotion_lib/locomotion_crouch_walk_back_right";
	private const string AnimCrouchWalkBack = "locomotion_lib/locomotion_crouch_walk_back";
	private const string AnimCrouchWalkFwdLeft = "locomotion_lib/locomotion_crouch_walk_fwd_left";
	private const string AnimCrouchWalkFwdRight = "locomotion_lib/locomotion_crouch_walk_fwd_right";
	private const string AnimCrouchStrafeLeft = "locomotion_lib/locomotion_crouch_strafe_left";
	private const string AnimCrouchStrafeRight = "locomotion_lib/locomotion_crouch_strafe_right";

	private const string AnimSprintBackLeft = "locomotion_lib/locomotion_sprint_back_left";
	private const string AnimSprintBackRight = "locomotion_lib/locomotion_sprint_back_right";
	private const string AnimSprintBack = "locomotion_lib/locomotion_sprint_back";
	private const string AnimSprintFwdLeft = "locomotion_lib/locomotion_sprint_fwd_left";
	private const string AnimSprintFwdRight = "locomotion_lib/locomotion_sprint_fwd_right";

	private const string AnimPistolIdle = "pistol_lib/pistol_idle";
	private const string AnimPistolKneelIdle = "pistol_lib/pistol_kneel_idle";
	private const string AnimPistolJump = "pistol_lib/pistol_jump";
	private const string AnimPistolWalk = "pistol_lib/pistol_walk";
	private const string AnimPistolRun = "pistol_lib/pistol_run";
	private const string AnimPistolWalkBack = "pistol_lib/pistol_walk_back";
	private const string AnimPistolRunBack = "pistol_lib/pistol_run_back";
	private const string AnimPistolWalkTurn = "pistol_lib/pistol_walk_turn";
	private const string AnimPistolRunTurn = "pistol_lib/pistol_run_turn";

	private const string AnimRifleAdsIdle = "rifle_lib/combat_ads_idle";
	private const string AnimRifleJump = "rifle_lib/combat_jump";

	// ---- Base looped movement, keyed by MovementState. Works for every weapon class and
	// is also what Melee uses outright (no melee-specific body pack was uploaded). ----
	private static readonly Dictionary<PlayerMovement.MovementState, string> LocomotionMap = new()
	{
		{ PlayerMovement.MovementState.Idle, AnimLocomotionIdle },
		{ PlayerMovement.MovementState.Walk, AnimLocomotionWalkFwd },
		{ PlayerMovement.MovementState.Sprint, AnimLocomotionSprintFwd },
		{ PlayerMovement.MovementState.Crouch, AnimLocomotionCrouchIdle },
		{ PlayerMovement.MovementState.CrouchWalk, AnimLocomotionCrouchWalk },
		// No dedicated slide clip was in any of the uploads - closest available pose.
		{ PlayerMovement.MovementState.Slide, AnimLocomotionCrouchWalk },
		{ PlayerMovement.MovementState.Jump, AnimLocomotionIdle },
		{ PlayerMovement.MovementState.Fall, AnimLocomotionIdle },
		{ PlayerMovement.MovementState.Aim, AnimLocomotionIdle },
	};

	// ---- Overrides for AssaultRifle / SMG / Shotgun / Heavy (two-handed long guns). ----
	private static readonly Dictionary<PlayerMovement.MovementState, string> RifleMap = new()
	{
		{ PlayerMovement.MovementState.Aim, AnimRifleAdsIdle },
		{ PlayerMovement.MovementState.Jump, AnimRifleJump },
		// Combat.fbx has no distinct falling loop; its jump pose is the closest stand-in.
		{ PlayerMovement.MovementState.Fall, AnimRifleJump },
	};

	// ---- Overrides for Sidearm. pistol_lib's own walk/run/turn set is richer than
	// locomotion_lib's, so it replaces the base map wholesale rather than only overriding Aim. ----
	private static readonly Dictionary<PlayerMovement.MovementState, string> PistolMap = new()
	{
		{ PlayerMovement.MovementState.Idle, AnimPistolIdle },
		{ PlayerMovement.MovementState.Crouch, AnimPistolKneelIdle },
		// No crouched-walk clip in the pack - kneel idle is the closest available pose.
		{ PlayerMovement.MovementState.CrouchWalk, AnimPistolKneelIdle },
		{ PlayerMovement.MovementState.Slide, AnimPistolKneelIdle },
		{ PlayerMovement.MovementState.Jump, AnimPistolJump },
		{ PlayerMovement.MovementState.Fall, AnimPistolJump },
		// The pack has no sidearm aim-down-sights pose - flagged in Warn() the first time
		// a player holding a Sidearm enters Aim state.
		{ PlayerMovement.MovementState.Aim, AnimPistolIdle },
	};

	// Body fire and reload clips are one-shots. AnimationFinished returns the controller to the
	// current movement clip once each action has completed.
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
		_anim.AnimationFinished += OnAnimationFinished;
		if (_movement == null) GD.PushWarning("PlayerAnimator: no PlayerMovement at " + PlayerMovementPath);
		// WeaponSwitcher calls the body one-shots from its broadcast fire/reload RPCs so each
		// peer sees the same action on the correct character.
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
		PlayerMovement.MovementState state = CurrentMovementState();
		if (_oneShotActive) return;

		string clip = ClipFor(category, state);
		if (clip == _lastClip) return; // nothing to change
		_lastClip = clip;
		_lastState = state;
		_lastCategory = category;

		_anim.Play(clip, CrossfadeSeconds);
	}

	private PlayerMovement.MovementState CurrentMovementState()
	{
		if (_movement == null || _movement.IsMultiplayerAuthority())
			return _movement?.State ?? PlayerMovement.MovementState.Idle;
		return System.Enum.TryParse(_movement.ReplicatedMovementState, out PlayerMovement.MovementState state)
			? state : PlayerMovement.MovementState.Idle;
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
		if (state is PlayerMovement.MovementState.Walk or PlayerMovement.MovementState.Sprint or
			PlayerMovement.MovementState.CrouchWalk)
			return DirectionalMovementClip(category, state);
		if (LocomotionMap.TryGetValue(state, out string? baseClip))
			return baseClip;
		Warn($"no clip mapped for state {state} (category {category})");
		return AnimLocomotionIdle;
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
		_oneShotActive = true;
		_anim.Play(clip, CrossfadeSeconds);
	}

	private string DirectionalMovementClip(string category, PlayerMovement.MovementState state)
	{
		if (_movement == null) return AnimLocomotionWalkFwd;
		Vector3 localVelocity = _movement.GlobalTransform.Basis.Transposed() * _movement.Velocity;
		float x = localVelocity.X;
		float z = localVelocity.Z;
		bool backward = z > Mathf.Abs(x) * 0.55f;
		bool diagonal = Mathf.Abs(x) > Mathf.Abs(z) * 0.45f;
		bool left = x < 0.0f;

		if (category == "pistol")
		{
			if (backward) return state == PlayerMovement.MovementState.Sprint
				? AnimPistolRunBack : AnimPistolWalkBack;
			if (diagonal || Mathf.Abs(x) > 0.1f) return state == PlayerMovement.MovementState.Sprint
				? AnimPistolRunTurn : AnimPistolWalkTurn;
			return state == PlayerMovement.MovementState.Sprint
				? AnimPistolRun : AnimPistolWalk;
		}

		if (state == PlayerMovement.MovementState.CrouchWalk)
		{
			if (backward && diagonal) return left
				? AnimCrouchWalkBackLeft
				: AnimCrouchWalkBackRight;
			if (backward) return AnimCrouchWalkBack;
			if (diagonal) return left
				? AnimCrouchWalkFwdLeft
				: AnimCrouchWalkFwdRight;
			if (Mathf.Abs(x) > 0.1f) return left
				? AnimCrouchStrafeLeft
				: AnimCrouchStrafeRight;
			return AnimLocomotionCrouchWalk;
		}

		if (state == PlayerMovement.MovementState.Sprint)
		{
			if (backward && diagonal) return left
				? AnimSprintBackLeft
				: AnimSprintBackRight;
			if (backward) return AnimSprintBack;
			if (diagonal) return left
				? AnimSprintFwdLeft
				: AnimSprintFwdRight;
			return AnimLocomotionSprintFwd;
		}

		if (backward && diagonal)
		{
			if (left)
			{
				Warn("No dedicated walk_back_left animation; falling back to walk_back.");
				return AnimLocomotionWalkBack;
			}
			return AnimLocomotionWalkBackRight;
		}
		if (backward) return AnimLocomotionWalkBack;
		if (diagonal) return left
			? AnimLocomotionWalkFwdLeft
			: AnimLocomotionWalkFwdRight;
		if (Mathf.Abs(x) > 0.1f)
		{
			if (left)
			{
				Warn("No dedicated strafe_left animation in locomotion_lib; falling back to walk_fwd_left.");
				return AnimLocomotionWalkFwdLeft;
			}
			return AnimLocomotionStrafeRight;
		}
		return AnimLocomotionWalkFwd;
	}

	private void OnAnimationFinished(StringName animationName)
	{
		if (!_oneShotActive) return;
		_oneShotActive = false;
		_lastClip = "";
		_lastState = (PlayerMovement.MovementState)(-1);
	}

	private void OnDied()
	{
		_dead = true;
		_oneShotActive = false;
		_lastClip = "";
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
		_oneShotActive = false;
		_lastClip = "";
		_lastState = (PlayerMovement.MovementState)(-1);
		_lastCategory = "";
	}

	private void Warn(string message)
	{
		if (_warnedOnce.Add(message)) GD.PushWarning("PlayerAnimator: " + message);
	}
}
