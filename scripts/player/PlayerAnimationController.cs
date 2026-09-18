using Godot;
using System.Collections.Generic;

/// <summary>
/// Builds the player's animation graph from three consolidated Blender/Mixamo exports
/// (locomotion, combat, death), each carrying many named takes on the same skeleton as
/// art/characters/manny.glb.
///
/// The graph is a blend tree, not a bare state machine:
///
///     Locomotion (state machine)  ->  Fire (one-shot)  ->  Reload (one-shot)  ->  output
///
/// Both one-shots are filtered to the upper body, so firing and reloading play over whatever the
/// legs are already doing instead of replacing it. That is the difference between "shooting while
/// running" and "freezing mid-stride to shoot", and it is why aiming used to cancel the walk cycle.
///
/// If any clip the graph needs is missing, it falls back to the plain locomotion state machine -
/// a missing combat clip should cost the fire animation, not the whole rig.
/// </summary>
public partial class PlayerAnimationController : Node
{
	// 0.15 s was short enough to expose the very different first poses of Mixamo's clips.
	private const float CrossfadeSeconds = 0.24f;
	private const string DefaultDeathClip = "Death From The Front";
	private const string FireClip = "combat_fire_rifle";
	private const string ReloadClip = "combat_reload_rifle";
	private const string LocomotionNodeName = "Locomotion";
	private const string FireNodeName = "Fire";
	private const string ReloadNodeName = "Reload";

	/// <summary>
	/// Bones the fire/reload one-shots are allowed to drive. Substrings, matched case-insensitively
	/// against the real bone names, because Godot's glTF importer sanitizes them on import and the
	/// exact form is not predictable from the source file.
	/// </summary>
	private static readonly string[] UpperBodyBoneFragments =
	{
		"spine", "neck", "head", "shoulder", "arm", "forearm", "hand", "thumb", "index",
		"middle", "ring", "pinky",
	};

	private static readonly Dictionary<PlayerMovement.MovementState, string> StateClips = new()
	{
		{ PlayerMovement.MovementState.Idle, "locomotion_idle" },
		{ PlayerMovement.MovementState.Walk, "locomotion_walk_fwd" },
		{ PlayerMovement.MovementState.Sprint, "locomotion_sprint_fwd" },
		{ PlayerMovement.MovementState.Crouch, "locomotion_crouch_idle" },
		{ PlayerMovement.MovementState.CrouchWalk, "locomotion_crouch_walk" },
		// No dedicated slide clip was supplied; crouch-walk remains the closest stand-in.
		{ PlayerMovement.MovementState.Slide, "locomotion_crouch_walk" },
		// "combat_jump", not "combat_jump_loop". The glb really does contain a take called
		// combat_jump_loop, but Godot's importer reads a trailing "_loop" as the loop-mode name
		// suffix: it turns looping on and strips the suffix. Reading the raw file name was what
		// produced the wrong key here; the runtime clip dump is the source of truth.
		{ PlayerMovement.MovementState.Jump, "combat_jump" },
		{ PlayerMovement.MovementState.Fall, "combat_jump" },
		{ PlayerMovement.MovementState.Aim, "combat_ads_idle" },
	};

	private static readonly string[] ClipSourceScenes =
	{
		"res://art/animations/player/locomotion/player animation locomotion.glb",
		"res://art/animations/player/combat/player animation combat.glb",
		"res://art/animations/player/death/player animation death.glb",
	};

	private AnimationPlayer _animationPlayer = null!;
	private AnimationTree _animationTree = null!;
	private AnimationNodeStateMachinePlayback _playback = null!;
	private PlayerMovement.MovementState? _currentState;
	private ulong _jumpAnimationHoldUntilMs;
	private bool _isDead;
	private bool _hasFireLayer;
	private bool _hasReloadLayer;

	public override void _Ready()
	{
		_animationPlayer = GetNode<AnimationPlayer>("../AnimationPlayer");
		_animationTree = GetNode<AnimationTree>("../AnimationTree");
		var player = GetParent();
		var skeleton = FindSkeleton(player);
		if (skeleton == null)
		{
			GD.PushError("Player animation setup could not find a Skeleton3D under the player.");
			return;
		}
		var skeletonPath = player.GetPathTo(skeleton);

		var library = new AnimationLibrary();
		foreach (string scenePath in ClipSourceScenes)
			ImportClipsFrom(scenePath, library, skeletonPath);
		ForceLocomotionClipsToLoop(library);
		_animationPlayer.AddAnimationLibrary("", library);

		var machine = BuildLocomotionMachine(library);
		_animationTree.TreeRoot = BuildGraph(machine, library, skeleton, skeletonPath);
		_animationTree.Active = true;
		_playback = (AnimationNodeStateMachinePlayback)_animationTree.Get(PlaybackParameter());
	}

	private string PlaybackParameter() =>
		_hasFireLayer || _hasReloadLayer
			? $"parameters/{LocomotionNodeName}/playback"
			: "parameters/playback";

	/// <summary>
	/// Every state in StateClips is a POSE HELD for as long as its state is active, not a one-shot -
	/// Walk/Sprint/Crouch/Aim/etc. are meant to cycle for however long the player keeps moving. Godot's
	/// glTF importer only turns looping on for a take whose Blender name ends in "_loop"; none of
	/// these do (measured net Hips translation across a full cycle of each is ~0, confirming they are
	/// authored as in-place cycles, not one-shot arcs), so every one of them imported as LOOP_NONE.
	///
	/// A state machine only calls Travel() when the STATE changes. While the state stays the same -
	/// which is most of the time you are walking or sprinting - nothing tells the node to restart, so
	/// a non-looping clip plays its ~0.5-2.1s cycle once and then holds its last frame while the
	/// character keeps moving. That freeze-then-hold, repeating every time the state is re-entered, is
	/// the "stepping pace looks glitchy/unstable" symptom. This overrides LoopMode on every clip the
	/// locomotion state machine references, regardless of what the importer decided.
	/// </summary>
	private static void ForceLocomotionClipsToLoop(AnimationLibrary library)
	{
		foreach (string clipName in new HashSet<string>(StateClips.Values))
		{
			if (!library.HasAnimation(clipName)) continue;
			library.GetAnimation(clipName).LoopMode = Animation.LoopModeEnum.Linear;
		}
	}

	private AnimationNodeStateMachine BuildLocomotionMachine(AnimationLibrary library)
	{
		var machine = new AnimationNodeStateMachine();
		var addedStates = new HashSet<PlayerMovement.MovementState>();
		Godot.Collections.Array<StringName>? availableClipsForError = null;
		foreach (var (state, clipName) in StateClips)
		{
			if (!library.HasAnimation(clipName))
			{
				// Godot's importer can rename things in ways that are not predictable from the
				// source file, so log what is actually in the library instead of guessing again.
				availableClipsForError ??= library.GetAnimationList();
				GD.PushError($"Animation clip '{clipName}' for state {state} was not found. "
					+ $"Animations actually present: {string.Join(", ", availableClipsForError)}");
				continue;
			}
			machine.AddNode(state.ToString(), new AnimationNodeAnimation { Animation = new StringName(clipName) }, Vector2.Zero);
			addedStates.Add(state);
		}

		foreach (var from in addedStates)
		foreach (var to in addedStates)
		{
			if (from == to) continue;
			machine.AddTransition(from.ToString(), to.ToString(), new AnimationNodeStateMachineTransition
			{
				XfadeTime = CrossfadeSeconds,
				Reset = false,
			});
		}
		return machine;
	}

	/// <summary>
	/// Wraps the locomotion machine in the two combat one-shots. Returns the bare machine when
	/// neither combat clip is available, which keeps the old behaviour as the fallback.
	/// </summary>
	private AnimationRootNode BuildGraph(AnimationNodeStateMachine machine, AnimationLibrary library,
		Skeleton3D skeleton, NodePath skeletonPath)
	{
		_hasFireLayer = library.HasAnimation(FireClip);
		_hasReloadLayer = library.HasAnimation(ReloadClip);
		if (!_hasFireLayer && !_hasReloadLayer)
		{
			GD.PushWarning($"Neither '{FireClip}' nor '{ReloadClip}' is present; weapon animation "
				+ "layers are disabled and locomotion runs on its own.");
			return machine;
		}

		var tree = new AnimationNodeBlendTree();
		tree.AddNode(LocomotionNodeName, machine, new Vector2(0, 0));
		string previous = LocomotionNodeName;

		if (_hasFireLayer)
			previous = AddOneShotLayer(tree, FireNodeName, FireClip, previous, skeleton, skeletonPath,
				fadeIn: 0.03f, fadeOut: 0.12f, column: 1);
		if (_hasReloadLayer)
			previous = AddOneShotLayer(tree, ReloadNodeName, ReloadClip, previous, skeleton, skeletonPath,
				fadeIn: 0.15f, fadeOut: 0.25f, column: 2);

		tree.ConnectNode("output", 0, previous);
		return tree;
	}

	/// <summary>
	/// Inserts one filtered one-shot layer into the blend tree and returns its node name, so layers
	/// can be chained. A OneShot has two inputs - input 0 is the pass-through (whatever was already
	/// playing) and input 1 is the clip to fire - so it needs a separate AnimationNodeAnimation
	/// node wired into that second input; the one-shot does not hold the clip itself.
	/// </summary>
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
			// Blend rather than Add: the clip is a full pose for the arms, not a delta on top of
			// one. Adding it would double up whatever the locomotion clip already has the arms
			// doing. The filter below is what keeps it off the legs.
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
				// "UpLeg"/"LowerLeg" would match "leg", not any of the fragments above, but
				// "forearm" contains "arm" and that is fine - both are upper body.
				yield return name;
				break;
			}
		}
	}

	public void SetMovementState(PlayerMovement.MovementState state)
	{
		if (_isDead) return;
		if (state == PlayerMovement.MovementState.Fall && _currentState == PlayerMovement.MovementState.Jump
			&& Time.GetTicksMsec() < _jumpAnimationHoldUntilMs)
			state = PlayerMovement.MovementState.Jump;
		if (!IsInstanceValid(_playback) || _currentState == state) return;
		_currentState = state;
		if (state == PlayerMovement.MovementState.Jump)
			_jumpAnimationHoldUntilMs = Time.GetTicksMsec() + 730;
		// Do not use world movement speed to scale authored animation: it was making sprint 53%
		// faster and compressing jump to 0.73 s. The source clips carry their own cadence.
		_animationPlayer.SpeedScale = 1.0f;
		_playback.Travel(state.ToString());
	}

	/// <summary>Plays the upper-body fire animation once. Safe to call every shot of a burst - the
	/// one-shot restarts rather than queueing.</summary>
	public void PlayFire()
	{
		if (_isDead || !_hasFireLayer) return;
		_animationTree.Set($"parameters/{FireNodeName}/request",
			(int)AnimationNodeOneShot.OneShotRequest.Fire);
	}

	/// <summary>Plays the upper-body reload animation once.</summary>
	public void PlayReload()
	{
		if (_isDead || !_hasReloadLayer) return;
		_animationTree.Set($"parameters/{ReloadNodeName}/request",
			(int)AnimationNodeOneShot.OneShotRequest.Fire);
	}

	/// <summary>Cancels a running reload animation - used when a shell-by-shell reload is
	/// interrupted by the trigger.</summary>
	public void CancelReload()
	{
		if (!_hasReloadLayer) return;
		_animationTree.Set($"parameters/{ReloadNodeName}/request",
			(int)AnimationNodeOneShot.OneShotRequest.Abort);
	}

	/// <summary>
	/// Leaves the locomotion graph and plays a fixed death pose once. Call from Health.
	/// Picking the clip by hit direction/headshot is a later refinement - it needs
	/// attacker-relative data that the damage RPC does not carry yet.
	/// </summary>
	public void PlayDeath()
	{
		_isDead = true;
		_animationTree.Active = false;
		_animationPlayer.Play(DefaultDeathClip);
	}

	/// <summary>
	/// Hands control back to the locomotion graph after a respawn. Clears the cached state so the
	/// next SetMovementState call always takes effect, even if it matches whatever state was active
	/// at the moment of death.
	/// </summary>
	public void ResetAfterRespawn()
	{
		_isDead = false;
		_currentState = null;
		// PlayDeath drove the death clip through the AnimationPlayer directly, with the tree
		// switched off. Re-enabling the tree does not cancel that - the AnimationPlayer keeps
		// holding the corpse pose and fights the tree for the same bones. Stop it first.
		_animationPlayer.Stop();
		_animationTree.Active = true;
		CancelReload();
		SetMovementState(PlayerMovement.MovementState.Idle);
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
