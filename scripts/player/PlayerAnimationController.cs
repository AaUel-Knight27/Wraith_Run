using Godot;
using System.Collections.Generic;

/// <summary>
/// Builds the player's animation graph from three consolidated Blender/Mixamo exports
/// (locomotion, combat, death), each carrying many named takes on the same skeleton as
/// art/characters/manny.glb.
///
/// The graph is a blend tree:
///
///   Locomotion (state machine)  ->  LocoTime (time scale)  ->  Fire (one-shot)  ->  Reload (one-shot)  ->  output
///
/// Locomotion has a DIRECTIONAL blend space for running and another for crouch-walking: the
/// character's actual heading relative to where it faces (forward, back, strafe, diagonals) picks and
/// mixes the clip. Before this, only the *_fwd clips were used, so strafing and backing up played the
/// forward walk cycle and the feet skated sideways/backwards at the full walking speed.
///
/// LocoTime scales the whole locomotion output so the feet keep pace with the ground. The scale is
/// measured speed / the ground speed the clip was authored for (AnimationTuning), so a 7.2 m/s sprint
/// and a 4.7 m/s walk both use the same run cycle at different playback speeds without sliding.
/// Speed and heading are measured from how the body ACTUALLY moved, not from input, which also makes
/// the same code correct for the remote copies of other players and stops the legs running in place
/// when the player is pressed against a wall.
///
/// Both one-shots are filtered to the upper body, so firing and reloading play over whatever the legs
/// are already doing. The left hand is pinned to the weapon by LeftHandIk.
///
/// If any clip the graph needs is missing it degrades instead of failing: a missing directional clip
/// falls back to the forward one, a missing combat clip costs the fire animation, not the whole rig.
/// </summary>
public partial class PlayerAnimationController : Node
{
	private const string DefaultDeathClip = "Death From The Front";
	private const string FireClip = "combat_fire_rifle";
	private const string ReloadClip = "combat_reload_rifle";
	private const string LocomotionNodeName = "Locomotion";
	private const string TimeNodeName = "LocoTime";
	private const string FireNodeName = "Fire";
	private const string ReloadNodeName = "Reload";

	// Graph states. Walk and Sprint are the SAME graph state ("Run"): the speed difference is handled
	// by playback speed, and keeping one state means no crossfade between two copies of the same
	// cycle every time Shift is pressed.
	private const string IdleState = "Idle";
	private const string RunState = "Run";
	private const string CrouchState = "Crouch";
	private const string CrouchWalkState = "CrouchWalk";
	private const string SlideState = "Slide";
	private const string AirState = "Air";
	private const string AimState = "Aim";

	private const float Diagonal = 0.70710678f;

	/// <summary>Blend-space layout for a family of directional clips: X = strafe right, Y = forward.
	/// Positions are on the unit circle; the pure left/right points are left out on purpose - the
	/// standing set has no strafe clips at all and the crouch strafes were authored at half the speed of
	/// everything else, so the sideways case is a blend of the two diagonals.</summary>
	private static readonly (string Clip, Vector2 Position)[] RunSet =
	{
		("locomotion_sprint_fwd", new Vector2(0, 1)),
		("locomotion_sprint_fwd_right", new Vector2(Diagonal, Diagonal)),
		("locomotion_sprint_back_right", new Vector2(Diagonal, -Diagonal)),
		("locomotion_sprint_back", new Vector2(0, -1)),
		("locomotion_sprint_back_left", new Vector2(-Diagonal, -Diagonal)),
		("locomotion_sprint_fwd_left", new Vector2(-Diagonal, Diagonal)),
	};

	private static readonly (string Clip, Vector2 Position)[] CrouchSet =
	{
		("locomotion_crouch_walk", new Vector2(0, 1)),
		("locomotion_crouch_walk_fwd_right", new Vector2(Diagonal, Diagonal)),
		("locomotion_crouch_walk_back_right", new Vector2(Diagonal, -Diagonal)),
		("locomotion_crouch_walk_back", new Vector2(0, -1)),
		("locomotion_crouch_walk_back_left", new Vector2(-Diagonal, -Diagonal)),
		("locomotion_crouch_walk_fwd_left", new Vector2(-Diagonal, Diagonal)),
	};

	private static readonly Dictionary<string, string> SingleClips = new()
	{
		{ IdleState, "locomotion_idle" },
		{ CrouchState, "locomotion_crouch_idle" },
		// No dedicated slide clip was supplied. A held crouch pose gliding along the floor reads as a
		// slide; the old stand-in (crouch-walk cycling at up to 9 m/s) looked like sprinting on the spot.
		{ SlideState, "locomotion_crouch_idle" },
		// "combat_jump", not "combat_jump_loop". The glb really does contain a take called
		// combat_jump_loop, but Godot's importer reads a trailing "_loop" as the loop-mode name
		// suffix: it turns looping on and strips the suffix. The runtime clip dump is the source of truth.
		{ AirState, "combat_jump" },
		{ AimState, "combat_ads_idle" },
	};

	private static readonly string[] ClipSourceScenes =
	{
		"res://art/animations/player/locomotion/player animation locomotion.glb",
		"res://art/animations/player/combat/player animation combat.glb",
		"res://art/animations/player/death/player animation death.glb",
	};

	/// <summary>Bones the fire/reload one-shots are allowed to drive. Substrings, matched
	/// case-insensitively against the real bone names, because Godot's glTF importer sanitizes them on
	/// import and the exact form is not predictable from the source file.</summary>
	private static readonly string[] UpperBodyBoneFragments =
	{
		"spine", "neck", "head", "shoulder", "arm", "forearm", "hand", "thumb", "index",
		"middle", "ring", "pinky",
	};

	private AnimationPlayer _animationPlayer = null!;
	private AnimationTree _animationTree = null!;
	private AnimationNodeStateMachinePlayback _playback = null!;
	private Node3D _body = null!;
	private LeftHandIk? _handIk;
	private readonly List<AnimationNodeStateMachineTransition> _transitions = new();

	private PlayerMovement.MovementState _requestedState = PlayerMovement.MovementState.Idle;
	private PlayerMovement.MovementState? _lastRequestedForJump;
	private string? _currentGraphState;
	private ulong _jumpAnimationHoldUntilMs;
	private bool _isDead;
	private bool _graphBuilt;
	private bool _hasFireLayer;
	private bool _hasReloadLayer;

	// Measured motion (see TrackMotion).
	private Vector3 _lastPosition;
	private bool _hasLastPosition;
	private Vector3 _velocity;
	private Vector2 _heading = new(0, 1);
	private float _headingAngle;   // radians from straight ahead, + = right; _heading is derived from it
	private bool _considersMoving;
	private float _timeScale = 1.0f;

	// Leg heading (see LegHeadingTwist): which clip angle is playing and how far the legs are turned.
	private LegHeadingTwist? _legTwist;
	private bool _legsForward = true;
	private float _clipAngle;      // radians, the blend-space angle the clips are being played at
	private float _twist;          // radians, legs turned towards the right by this much
	public float ClipAngleDegrees => Mathf.RadToDeg(_clipAngle);
	public float TwistDegrees => Mathf.RadToDeg(_twist);

	/// <summary>Test/preview hook: when true, the controller behaves as if the body were moving at
	/// DebugLocalVelocityValue in its own frame (X = right, Y = forward, m/s) instead of measuring it.
	/// A plain bool+Vector2 pair rather than a nullable, because a Nullable<Vector2> does not marshal
	/// back from a GDScript .set() call - it silently stays null, which cost real debugging time.</summary>
	public bool DebugVelocityOverride { get; set; }
	public Vector2 DebugLocalVelocityValue { get; set; }

	// Read-only view for the tuner and the IK.
	public PlayerMovement.MovementState RequestedState => _requestedState;
	public string CurrentGraphState => _currentGraphState ?? "-";
	public float MeasuredSpeed => _velocity.Length();
	/// <summary>Heading in the character's own frame: X = right, Y = forward.</summary>
	public Vector2 Heading => _heading;
	public float TimeScale => _timeScale;
	public bool IsDead => _isDead;
	public LeftHandIk? HandIk => _handIk;
	public AnimationTree Tree => _animationTree;
	public AnimationPlayer Player => _animationPlayer;

	public bool IsReloadPlaying =>
		_hasReloadLayer && IsInstanceValid(_animationTree)
		&& _animationTree.Get($"parameters/{ReloadNodeName}/active").AsBool();

	public override void _Ready()
	{
		AnimationTuning.EnsureLoaded();
		_animationPlayer = GetNode<AnimationPlayer>("../AnimationPlayer");
		_animationTree = GetNode<AnimationTree>("../AnimationTree");
		_body = (Node3D)GetParent();
		var skeleton = FindSkeleton(_body);
		if (skeleton == null)
		{
			GD.PushError("Player animation setup could not find a Skeleton3D under the player.");
			return;
		}
		var skeletonPath = _body.GetPathTo(skeleton);

		var library = new AnimationLibrary();
		foreach (string scenePath in ClipSourceScenes)
			ImportClipsFrom(scenePath, library, skeletonPath);
		ForceLocomotionClipsToLoop(library);
		_animationPlayer.AddAnimationLibrary("", library);

		_animationTree.TreeRoot = BuildGraph(BuildLocomotionMachine(library), library, skeleton, skeletonPath);
		_animationTree.Active = true;
		_playback = (AnimationNodeStateMachinePlayback)_animationTree.Get($"parameters/{LocomotionNodeName}/playback");
		_graphBuilt = true;

		// Order matters: legs are turned first, then the hand IK works on the final upper body.
		_legTwist = new LegHeadingTwist { Name = "LegHeadingTwist" };
		skeleton.AddChild(_legTwist);

		_handIk = new LeftHandIk { Name = "LeftHandIk" };
		AddChild(_handIk);
		_handIk.Setup(skeleton, _body, this);

		// Debug-only live tuner (F8). It spawns itself away in release exports and on remote players.
		if (OS.IsDebugBuild()) AddChild(new AnimationTuner { Name = "AnimationTuner" });
	}

	// -------------------------------------------------------------------------------------------
	// Per-frame: measure the body, pick the effective state, feed the graph
	// -------------------------------------------------------------------------------------------

	public override void _PhysicsProcess(double deltaValue)
	{
		if (!_graphBuilt) return;
		float delta = (float)deltaValue;
		TrackMotion(delta);
		if (_isDead) return;

		ApplyGraphState(ResolveGraphState());
		FeedBlendParameters(delta);
		ApplyCrossfade();
	}

	/// <summary>Speed and heading from how the body actually moved between physics frames. The same
	/// path serves the local player and remote copies (whose position arrives from the network in
	/// steps - the smoothing hides that), and it reports what the feet must keep up with even when
	/// the player is pushing into a wall.</summary>
	private void TrackMotion(float delta)
	{
		if (DebugVelocityOverride)
		{
			Vector2 forced = DebugLocalVelocityValue;
			// Same pipeline, fed by hand: convert to world so the rest of the maths is untouched.
			Vector3 worldVelocity = _body.GlobalTransform.Basis.Orthonormalized() * new Vector3(forced.X, 0.0f, -forced.Y);
			float snap = 1.0f - Mathf.Exp(-delta / Mathf.Max(AnimationTuning.VelocitySmoothingSeconds, 0.001f));
			_velocity = _velocity.Lerp(worldVelocity, snap);
			_lastPosition = _body.GlobalPosition;
			_hasLastPosition = true;
			if (forced.Length() > 0.15f)
			{
				float k2 = 1.0f - Mathf.Exp(-delta / Mathf.Max(AnimationTuning.DirectionSmoothingSeconds, 0.001f));
				_headingAngle = Mathf.LerpAngle(_headingAngle, Mathf.Atan2(forced.X, forced.Y), k2);
				_headingAngle = Mathf.Wrap(_headingAngle, -Mathf.Pi, Mathf.Pi);
				_heading = new Vector2(Mathf.Sin(_headingAngle), Mathf.Cos(_headingAngle));
			}
			return;
		}

		Vector3 position = _body.GlobalPosition;
		if (_hasLastPosition && delta > 0.0f)
		{
			Vector3 measured = (position - _lastPosition) / delta;
			measured.Y = 0.0f;
			// A respawn teleports the body; that is not a 3000 m/s run.
			if (measured.LengthSquared() > 40.0f * 40.0f) measured = Vector3.Zero;
			float k = 1.0f - Mathf.Exp(-delta / Mathf.Max(AnimationTuning.VelocitySmoothingSeconds, 0.001f));
			_velocity = _velocity.Lerp(measured, k);
		}
		_lastPosition = position;
		_hasLastPosition = true;

		// Into the character's frame. Godot's forward is -Z, so forward = -local.Z.
		Vector3 local = _body.GlobalTransform.Basis.Orthonormalized().Inverse() * _velocity;
		var planar = new Vector2(local.X, -local.Z);
		if (planar.Length() > 0.15f)
		{
			// Smooth the ANGLE, not the vector: lerping two opposite vectors (walking forward then
			// straight back) never leaves the axis and the heading would stay stuck pointing forward.
			float k = 1.0f - Mathf.Exp(-delta / Mathf.Max(AnimationTuning.DirectionSmoothingSeconds, 0.001f));
			_headingAngle = Mathf.LerpAngle(_headingAngle, Mathf.Atan2(planar.X, planar.Y), k);
			_headingAngle = Mathf.Wrap(_headingAngle, -Mathf.Pi, Mathf.Pi);
			_heading = new Vector2(Mathf.Sin(_headingAngle), Mathf.Cos(_headingAngle));
		}
	}

	private string ResolveGraphState()
	{
		float speed = _velocity.Length();
		float threshold = _considersMoving ? AnimationTuning.StopSpeed : AnimationTuning.StartSpeed;
		bool moving = speed > threshold;
		_considersMoving = moving;

		return _requestedState switch
		{
			PlayerMovement.MovementState.Walk or PlayerMovement.MovementState.Sprint => moving ? RunState : IdleState,
			PlayerMovement.MovementState.CrouchWalk => moving ? CrouchWalkState : CrouchState,
			PlayerMovement.MovementState.Crouch => CrouchState,
			PlayerMovement.MovementState.Slide => SlideState,
			PlayerMovement.MovementState.Jump or PlayerMovement.MovementState.Fall => AirState,
			PlayerMovement.MovementState.Aim => AimState,
			_ => IdleState,
		};
	}

	private void ApplyGraphState(string graphState)
	{
		if (!IsInstanceValid(_playback) || _currentGraphState == graphState) return;
		_currentGraphState = graphState;
		_playback.Travel(graphState);
	}

	private void FeedBlendParameters(float delta)
	{
		bool run = _currentGraphState == RunState;
		bool crouchWalk = _currentGraphState == CrouchWalkState;
		float speed = _velocity.Length();

		// Where the character is heading, as an angle from straight ahead (+ = right), and which pair of
		// clips plays it. Forward-facing legs cover -45..45 degrees on their own and the pelvis is
		// yawed for the rest of the forward half (up to 90 = pure strafe); the backward clips take over
		// beyond 90 with the same trick. A dead band stops the choice flickering while you strafe.
		float phi = Mathf.RadToDeg(_headingAngle);
		float abs = Mathf.Abs(phi);
		if (_legsForward && abs > AnimationTuning.BackwardSwitchAngle) _legsForward = false;
		else if (!_legsForward && abs < AnimationTuning.ForwardSwitchAngle) _legsForward = true;

		float clipAngle = _legsForward
			? Mathf.Clamp(phi, -45.0f, 45.0f)
			: Mathf.Sign(phi) * Mathf.Clamp(abs, 135.0f, 180.0f);
		float twistGoal = (run || crouchWalk) && AnimationTuning.LegTwistEnabled
			? Mathf.DegToRad(Mathf.Wrap(phi - clipAngle, -180.0f, 180.0f))
			: 0.0f;
		float clipGoal = Mathf.DegToRad(clipAngle);
		if (!(run || crouchWalk)) clipGoal = _clipAngle;

		float follow = 1.0f - Mathf.Exp(-delta / Mathf.Max(AnimationTuning.TwistSmoothingSeconds, 0.001f));
		_clipAngle = Mathf.Wrap(Mathf.LerpAngle(_clipAngle, clipGoal, follow), -Mathf.Pi, Mathf.Pi);
		_twist = Mathf.Lerp(_twist, twistGoal, follow);
		if (_legTwist != null) _legTwist.TwistRadians = _twist;

		// Only the moving cycles are speed-matched. Idle, aim, air and slide poses play at 1x.
		float goal = 1.0f;
		if (run || crouchWalk)
			goal = AnimationTuning.TimeScaleFor(crouchWalk, speed, Mathf.Abs(Mathf.RadToDeg(_clipAngle)));
		// A short ease so a change of state does not snap the cadence.
		_timeScale = Mathf.Lerp(_timeScale, goal, 1.0f - Mathf.Exp(-delta / 0.05f));

		var position = new Vector2(Mathf.Sin(_clipAngle), Mathf.Cos(_clipAngle));
		_animationTree.Set($"parameters/{TimeNodeName}/scale", _timeScale);
		_animationTree.Set($"parameters/{LocomotionNodeName}/{RunState}/blend_position", position);
		_animationTree.Set($"parameters/{LocomotionNodeName}/{CrouchWalkState}/blend_position", position);
	}

	private void ApplyCrossfade()
	{
		foreach (var transition in _transitions)
			transition.XfadeTime = AnimationTuning.CrossfadeSeconds;
	}

	// -------------------------------------------------------------------------------------------
	// Graph construction
	// -------------------------------------------------------------------------------------------

	/// <summary>Every clip the locomotion graph plays is a POSE HELD for as long as its state is active,
	/// not a one-shot. Godot's glTF importer only turns looping on for a take whose Blender name ends in
	/// "_loop", so all of them imported as LOOP_NONE and would freeze on their last frame. Force them.</summary>
	private static void ForceLocomotionClipsToLoop(AnimationLibrary library)
	{
		var names = new HashSet<string>(SingleClips.Values);
		foreach (var (clip, _) in RunSet) names.Add(clip);
		foreach (var (clip, _) in CrouchSet) names.Add(clip);
		foreach (string clipName in names)
		{
			if (!library.HasAnimation(clipName)) continue;
			library.GetAnimation(clipName).LoopMode = Animation.LoopModeEnum.Linear;
		}
	}

	private AnimationNodeStateMachine BuildLocomotionMachine(AnimationLibrary library)
	{
		var machine = new AnimationNodeStateMachine();
		var added = new List<string>();

		foreach (var (state, clip) in SingleClips)
		{
			if (!RequireClip(library, clip, state)) continue;
			machine.AddNode(state, new AnimationNodeAnimation { Animation = new StringName(clip) }, Vector2.Zero);
			added.Add(state);
		}

		AddDirectionalState(machine, added, library, RunState, RunSet, "locomotion_sprint_fwd");
		AddDirectionalState(machine, added, library, CrouchWalkState, CrouchSet, "locomotion_crouch_walk");

		foreach (string from in added)
		foreach (string to in added)
		{
			if (from == to) continue;
			var transition = new AnimationNodeStateMachineTransition
			{
				XfadeTime = AnimationTuning.CrossfadeSeconds,
				Reset = false,
			};
			_transitions.Add(transition);
			machine.AddTransition(from, to, transition);
		}
		return machine;
	}

	private void AddDirectionalState(AnimationNodeStateMachine machine, List<string> added, AnimationLibrary library,
		string state, (string Clip, Vector2 Position)[] set, string fallbackClip)
	{
		var space = new AnimationNodeBlendSpace2D
		{
			MinSpace = new Vector2(-1.0f, -1.0f),
			MaxSpace = new Vector2(1.0f, 1.0f),
			BlendMode = AnimationNodeBlendSpace2D.BlendModeEnum.Interpolated,
			// Every point keeps advancing even at weight 0, so the cycles stay in phase and moving
			// the stick from forward to strafe does not pop a leg to a stale frame.
			Sync = true,
			XLabel = "strafe (right +)",
			YLabel = "forward",
		};
		int points = 0;
		foreach (var (clip, position) in set)
		{
			if (!RequireClip(library, clip, state)) continue;
			space.AddBlendPoint(new AnimationNodeAnimation { Animation = new StringName(clip) }, position, -1, new StringName(clip));
			points++;
		}

		if (points >= 3)
		{
			machine.AddNode(state, space, Vector2.Zero);
			added.Add(state);
		}
		else if (RequireClip(library, fallbackClip, state))
		{
			// Directional clips are missing: play the forward one so the character still moves.
			machine.AddNode(state, new AnimationNodeAnimation { Animation = new StringName(fallbackClip) }, Vector2.Zero);
			added.Add(state);
		}
	}

	private static bool RequireClip(AnimationLibrary library, string clip, string state)
	{
		if (library.HasAnimation(clip)) return true;
		// Godot's importer can rename things in ways that are not predictable from the source file,
		// so log what is actually in the library instead of guessing again.
		GD.PushError($"Animation clip '{clip}' for state {state} was not found. "
			+ $"Animations actually present: {string.Join(", ", library.GetAnimationList())}");
		return false;
	}

	private AnimationRootNode BuildGraph(AnimationNodeStateMachine machine, AnimationLibrary library,
		Skeleton3D skeleton, NodePath skeletonPath)
	{
		_hasFireLayer = library.HasAnimation(FireClip);
		_hasReloadLayer = library.HasAnimation(ReloadClip);
		if (!_hasFireLayer && !_hasReloadLayer)
			GD.PushWarning($"Neither '{FireClip}' nor '{ReloadClip}' is present; weapon animation layers are disabled.");

		var tree = new AnimationNodeBlendTree();
		tree.AddNode(LocomotionNodeName, machine, new Vector2(0, 0));
		tree.AddNode(TimeNodeName, new AnimationNodeTimeScale(), new Vector2(240, 0));
		tree.ConnectNode(TimeNodeName, 0, LocomotionNodeName);
		string previous = TimeNodeName;

		if (_hasFireLayer)
			previous = AddOneShotLayer(tree, FireNodeName, FireClip, previous, skeleton, skeletonPath,
				fadeIn: 0.03f, fadeOut: 0.12f, column: 2);
		if (_hasReloadLayer)
			previous = AddOneShotLayer(tree, ReloadNodeName, ReloadClip, previous, skeleton, skeletonPath,
				fadeIn: 0.15f, fadeOut: 0.25f, column: 3);

		tree.ConnectNode("output", 0, previous);
		return tree;
	}

	/// <summary>Inserts one filtered one-shot layer into the blend tree and returns its node name, so
	/// layers can be chained. A OneShot has two inputs - input 0 is the pass-through (whatever was
	/// already playing) and input 1 is the clip to fire - so it needs a separate AnimationNodeAnimation
	/// node wired into that second input; the one-shot does not hold the clip itself.</summary>
	private static string AddOneShotLayer(AnimationNodeBlendTree tree, string nodeName, string clip,
		string inputNodeName, Skeleton3D skeleton, NodePath skeletonPath, float fadeIn, float fadeOut,
		int column)
	{
		string clipNodeName = nodeName + "Clip";
		tree.AddNode(clipNodeName, new AnimationNodeAnimation { Animation = new StringName(clip) },
			new Vector2(column * 240, 160));

		var shot = new AnimationNodeOneShot
		{
			FadeInTime = fadeIn,
			FadeOutTime = fadeOut,
			// Blend rather than Add: the clip is a full pose for the arms, not a delta on top of one.
			// The filter below is what keeps it off the legs.
			MixMode = AnimationNodeOneShot.MixModeEnum.Blend,
			FilterEnabled = true,
		};
		foreach (string bone in UpperBodyBones(skeleton))
			shot.SetFilterPath(new NodePath($"{skeletonPath}:{bone}"), true);

		tree.AddNode(nodeName, shot, new Vector2(column * 240, 0));
		tree.ConnectNode(nodeName, 0, inputNodeName);
		tree.ConnectNode(nodeName, 1, clipNodeName);
		return nodeName;
	}

	private static IEnumerable<string> UpperBodyBones(Skeleton3D skeleton)
	{
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
		{
			string name = skeleton.GetBoneName(i);
			string lower = name.ToLowerInvariant();
			foreach (string fragment in UpperBodyBoneFragments)
			{
				if (!lower.Contains(fragment)) continue;
				yield return name;
				break;
			}
		}
	}

	// -------------------------------------------------------------------------------------------
	// Public API (unchanged signatures)
	// -------------------------------------------------------------------------------------------

	public void SetMovementState(PlayerMovement.MovementState state)
	{
		if (_isDead) return;
		if (state == PlayerMovement.MovementState.Fall && _requestedState == PlayerMovement.MovementState.Jump
			&& Time.GetTicksMsec() < _jumpAnimationHoldUntilMs)
			state = PlayerMovement.MovementState.Jump;
		if (_requestedState == state) return;
		_requestedState = state;
		if (state == PlayerMovement.MovementState.Jump)
			_jumpAnimationHoldUntilMs = Time.GetTicksMsec() + 730;
	}

	/// <summary>Plays the upper-body fire animation once. Safe to call every shot of a burst - the
	/// one-shot restarts rather than queueing.</summary>
	public void PlayFire()
	{
		if (_isDead || !_hasFireLayer) return;
		_animationTree.Set($"parameters/{FireNodeName}/request", (int)AnimationNodeOneShot.OneShotRequest.Fire);
	}

	/// <summary>Plays the upper-body reload animation once.</summary>
	public void PlayReload()
	{
		if (_isDead || !_hasReloadLayer) return;
		_animationTree.Set($"parameters/{ReloadNodeName}/request", (int)AnimationNodeOneShot.OneShotRequest.Fire);
	}

	/// <summary>Cancels a running reload animation - used when a shell-by-shell reload is
	/// interrupted by the trigger.</summary>
	public void CancelReload()
	{
		if (!_hasReloadLayer) return;
		_animationTree.Set($"parameters/{ReloadNodeName}/request", (int)AnimationNodeOneShot.OneShotRequest.Abort);
	}

	/// <summary>Leaves the locomotion graph and plays a fixed death pose once. Call from Health.
	/// Picking the clip by hit direction/headshot is a later refinement - it needs attacker-relative
	/// data that the damage RPC does not carry yet.</summary>
	public void PlayDeath()
	{
		_isDead = true;
		_animationTree.Active = false;
		_animationPlayer.Play(DefaultDeathClip);
	}

	/// <summary>Hands control back to the locomotion graph after a respawn. Clears the cached state so
	/// the next frame always takes effect, even if it matches whatever state was active at death.</summary>
	public void ResetAfterRespawn()
	{
		_isDead = false;
		_currentGraphState = null;
		_requestedState = PlayerMovement.MovementState.Idle;
		_velocity = Vector3.Zero;
		_hasLastPosition = false;
		_twist = 0.0f;
		// PlayDeath drove the death clip through the AnimationPlayer directly, with the tree switched
		// off. Re-enabling the tree does not cancel that - the AnimationPlayer keeps holding the corpse
		// pose and fights the tree for the same bones. Stop it first.
		_animationPlayer.Stop();
		_animationTree.Active = true;
		CancelReload();
	}

	private static Skeleton3D? FindSkeleton(Node root)
	{
		if (root is Skeleton3D skeleton) return skeleton;
		foreach (Node child in root.GetChildren())
		{
			var found = FindSkeleton(child);
			if (found != null) return found;
		}
		return null;
	}

	private static void ImportClipsFrom(string scenePath, AnimationLibrary library, NodePath targetSkeletonPath)
	{
		var scene = ResourceLoader.Load<PackedScene>(scenePath);
		var importedRoot = scene?.Instantiate();
		var sourcePlayer = importedRoot?.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		if (sourcePlayer == null)
		{
			GD.PushError($"No AnimationPlayer found in {scenePath}.");
			importedRoot?.QueueFree();
			return;
		}

		// Pull from every library the file has rather than assuming the default "" one, since that
		// assumption (and a hardcoded resource path) have both broken this project before.
		foreach (StringName libraryName in sourcePlayer.GetAnimationLibraryList())
		{
			var sourceLibrary = sourcePlayer.GetAnimationLibrary(libraryName);
			foreach (StringName clipName in sourceLibrary.GetAnimationList())
			{
				var animation = sourceLibrary.GetAnimation(clipName);
				if (animation == null) continue;
				var retargeted = (Animation)animation.Duplicate();
				RebindTracksToSkeleton(retargeted, targetSkeletonPath);
				if (library.HasAnimation(clipName)) library.RemoveAnimation(clipName);
				library.AddAnimation(clipName, retargeted);
			}
		}
		importedRoot?.QueueFree();
	}

	private static void RebindTracksToSkeleton(Animation animation, NodePath targetSkeletonPath)
	{
		for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
		{
			string sourcePath = animation.TrackGetPath(trackIndex).ToString();
			int boneSeparator = sourcePath.IndexOf(':');
			if (boneSeparator < 0) continue;

			// Bone names themselves contain a colon ("mixamorig:Hips"), so only the FIRST colon is
			// a separator; everything after it is the real bone name, embedded colon and all.
			string boneName = sourcePath[(boneSeparator + 1)..];
			animation.TrackSetPath(trackIndex, new NodePath($"{targetSkeletonPath}:{boneName}"));
		}
	}
}
