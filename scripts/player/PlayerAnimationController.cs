using Godot;
using System.Collections.Generic;

/// <summary>
/// Builds the player's locomotion AnimationLibrary and state machine from three consolidated
/// Blender/Mixamo exports (locomotion, combat, death), each carrying many named takes on the
/// same skeleton as art/characters/manny.glb. Unlike the old per-clip FBX pipeline, clip names
/// already match the state-machine keys used below - no per-clip renaming needed here.
/// </summary>
public partial class PlayerAnimationController : Node
{
    // 0.15 s was short enough to expose the very different first poses of Mixamo's clips.
    private const float CrossfadeSeconds = 0.24f;
    private const string DefaultDeathClip = "Death From The Front";

    private static readonly Dictionary<PlayerMovement.MovementState, string> StateClips = new()
    {
        { PlayerMovement.MovementState.Idle, "locomotion_idle" },
        { PlayerMovement.MovementState.Walk, "locomotion_walk_fwd" },
        { PlayerMovement.MovementState.Sprint, "locomotion_sprint_fwd" },
        { PlayerMovement.MovementState.Crouch, "locomotion_crouch_idle" },
        { PlayerMovement.MovementState.CrouchWalk, "locomotion_crouch_walk" },
        // No dedicated slide clip was supplied; crouch-walk remains the closest stand-in.
        { PlayerMovement.MovementState.Slide, "locomotion_crouch_walk" },
        // combat_jump_loop is the sustained airborne pose, reused for both states since no
        // separate fall clip exists. combat_jump_down looks like a landing clip - worth trying
        // there instead once you can actually see these play back to back.
        { PlayerMovement.MovementState.Jump, "combat_jump_loop" },
        { PlayerMovement.MovementState.Fall, "combat_jump_loop" },
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
        _animationPlayer.AddAnimationLibrary("", library);

        var machine = new AnimationNodeStateMachine();
        foreach (var (state, clipName) in StateClips)
        {
            if (!library.HasAnimation(clipName))
            {
                GD.PushError($"Animation clip '{clipName}' for state {state} was not found in the imported libraries.");
                continue;
            }
            machine.AddNode(state.ToString(), new AnimationNodeAnimation { Animation = new StringName(clipName) }, Vector2.Zero);
        }

        foreach (var (from, _) in StateClips)
        foreach (var (to, _) in StateClips)
        {
            if (from == to) continue;
            machine.AddTransition(from.ToString(), to.ToString(), new AnimationNodeStateMachineTransition
            {
                XfadeTime = CrossfadeSeconds,
                Reset = false,
            });
        }

        _animationTree.TreeRoot = machine;
        _animationTree.Active = true;
        _playback = (AnimationNodeStateMachinePlayback)_animationTree.Get("parameters/playback");
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

    /// <summary>
    /// Leaves the locomotion state machine and plays a fixed death pose once. Call from
    /// Health.Died. Picking the clip by hit direction/headshot is a later refinement - it needs
    /// attacker-relative data that WeaponSwitcher's hit RPC doesn't carry yet.
    /// </summary>
    public void PlayDeath()
    {
        _isDead = true;
        _animationTree.Active = false;
        _animationPlayer.Play(DefaultDeathClip);
    }

    /// <summary>
    /// Call from Health.Respawned to hand control back to the locomotion state machine.
    /// Clears the cached state so the next SetMovementState call always takes effect, even if
    /// it happens to match whatever state was active at the moment of death.
    /// </summary>
    public void ResetAfterRespawn()
    {
        _isDead = false;
        _currentState = null;
        _animationTree.Active = true;
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

        // Pull from every library the file has rather than assuming the default "" one, since
        // that assumption (and a hardcoded resource path) have both broken this project before.
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

            // Bone names themselves contain a colon ("mixamorig:Hips"), so only the FIRST colon
            // is a separator; everything after it is the real bone name, embedded colon and all.
            string boneName = sourcePath[(boneSeparator + 1)..];
            animation.TrackSetPath(trackIndex, new NodePath($"{targetSkeletonPath}:{boneName}"));
        }
    }
}
