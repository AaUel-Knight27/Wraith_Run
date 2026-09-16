using Godot;
using System.Collections.Generic;

/// <summary>
/// Builds the local player's locomotion AnimationLibrary and state machine from
/// the imported Mixamo clips.  The clips remain as individual source assets so
/// they can be reimported or retargeted without maintaining a second copy.
/// </summary>
public partial class PlayerAnimationController : Node
{
    // 0.15 s was short enough to expose the very different first poses of Mixamo's clips.
    private const float CrossfadeSeconds = 0.24f;

    private static readonly Dictionary<PlayerMovement.MovementState, string> StateClips = new()
    {
        { PlayerMovement.MovementState.Idle, "locomotion_idle" },
        { PlayerMovement.MovementState.Walk, "locomotion_walk_fwd" },
        { PlayerMovement.MovementState.Sprint, "locomotion_sprint_fwd" },
        { PlayerMovement.MovementState.Crouch, "locomotion_crouch_idle" },
        { PlayerMovement.MovementState.CrouchWalk, "locomotion_crouch_walk" },
        // No dedicated Mixamo slide was supplied; crouch-walk is the closest temporary clip.
        { PlayerMovement.MovementState.Slide, "locomotion_crouch_walk" },
        { PlayerMovement.MovementState.Jump, "combat_jump" },
        { PlayerMovement.MovementState.Aim, "combat_ads_idle" },
        // No dedicated fall was supplied; the looping jump clip is the closest temporary clip.
        { PlayerMovement.MovementState.Fall, "combat_jump" },
    };

    private static readonly (string ClipName, string Path)[] ImportedClips =
    {
        ("combat_ads_idle", "res://art/animations/player/combat/combat_ads_idle.fbx"),
        ("combat_fire_rifle", "res://art/animations/player/combat/combat_fire_rifle.fbx"),
        ("combat_fire_rifle_crouch", "res://art/animations/player/combat/combat_fire_rifle_crouch.fbx"),
        ("combat_fire_rifle_crouch_walk", "res://art/animations/player/combat/combat_fire_rifle_crouch_walk.fbx"),
        ("combat_fire_rifle_sprint", "res://art/animations/player/combat/combat_fire_rifle_sprint.fbx"),
        ("combat_fire_rifle_walk", "res://art/animations/player/combat/combat_fire_rifle_walk.fbx"),
        ("combat_holster_rifle", "res://art/animations/player/combat/combat_holster_rifle.fbx"),
        ("locomotion_idle", "res://art/animations/player/locomotion/locomotion_idle.fbx"),
        ("combat_jump_back", "res://art/animations/player/combat/combat_jump_back.fbx"),
        ("locomotion_walk_fwd", "res://art/animations/player/locomotion/locomotion_walk_fwd.fbx"),
        ("combat_jump_fwd", "res://art/animations/player/combat/combat_jump_fwd.fbx"),
        ("combat_jump", "res://art/animations/player/combat/combat_jump.fbx"),
        ("combat_jump_rifle", "res://art/animations/player/combat/combat_jump_rifle.fbx"),
        ("combat_jump_vertical", "res://art/animations/player/combat/combat_jump_vertical.fbx"),
        ("locomotion_sprint_fwd", "res://art/animations/player/locomotion/locomotion_sprint_fwd.fbx"),
        ("combat_reload_rifle", "res://art/animations/player/combat/combat_reload_rifle.fbx"),
        ("combat_reload_rifle_crouch", "res://art/animations/player/combat/combat_reload_rifle_crouch.fbx"),
        ("combat_reload_rifle_sprint", "res://art/animations/player/combat/combat_reload_rifle_sprint.fbx"),
        ("combat_reload_rifle_walk", "res://art/animations/player/combat/combat_reload_rifle_walk.fbx"),
        ("combat_turn_left", "res://art/animations/player/combat/combat_turn_left.fbx"),
        ("combat_turn_right", "res://art/animations/player/combat/combat_turn_right.fbx"),
        ("locomotion_crouch_idle", "res://art/animations/player/locomotion/locomotion_crouch_idle.fbx"),
        ("locomotion_crouch_walk", "res://art/animations/player/locomotion/locomotion_crouch_walk.fbx"),
        ("locomotion_crouch_strafe_left", "res://art/animations/player/locomotion/locomotion_crouch_strafe_left.fbx"),
        ("locomotion_crouch_strafe_right", "res://art/animations/player/locomotion/locomotion_crouch_strafe_right.fbx"),
        ("locomotion_crouch_to_standing_rifle", "res://art/animations/player/locomotion/locomotion_crouch_to_standing_rifle.fbx"),
        ("locomotion_crouch_walk_back", "res://art/animations/player/locomotion/locomotion_crouch_walk_back.fbx"),
        ("locomotion_crouch_walk_back_left", "res://art/animations/player/locomotion/locomotion_crouch_walk_back_left.fbx"),
        ("locomotion_crouch_walk_back_right", "res://art/animations/player/locomotion/locomotion_crouch_walk_back_right.fbx"),
        ("locomotion_crouch_walk_fwd_left", "res://art/animations/player/locomotion/locomotion_crouch_walk_fwd_left.fbx"),
        ("locomotion_crouch_walk_fwd_right", "res://art/animations/player/locomotion/locomotion_crouch_walk_fwd_right.fbx"),
        ("locomotion_sprint_back", "res://art/animations/player/locomotion/locomotion_sprint_back.fbx"),
        ("locomotion_sprint_back_left", "res://art/animations/player/locomotion/locomotion_sprint_back_left.fbx"),
        ("locomotion_sprint_back_right", "res://art/animations/player/locomotion/locomotion_sprint_back_right.fbx"),
        ("locomotion_sprint_fwd_left", "res://art/animations/player/locomotion/locomotion_sprint_fwd_left.fbx"),
        ("locomotion_sprint_fwd_right", "res://art/animations/player/locomotion/locomotion_sprint_fwd_right.fbx"),
        ("locomotion_stand_to_crouch", "res://art/animations/player/locomotion/locomotion_stand_to_crouch.fbx"),
        ("locomotion_strafe_left", "res://art/animations/player/locomotion/locomotion_strafe_left.fbx"),
        ("locomotion_strafe_right", "res://art/animations/player/locomotion/locomotion_strafe_right.fbx"),
        ("locomotion_walk", "res://art/animations/player/locomotion/locomotion_walk.fbx"),
        ("locomotion_walk_back", "res://art/animations/player/locomotion/locomotion_walk_back.fbx"),
        ("locomotion_walk_fwd_left", "res://art/animations/player/locomotion/locomotion_walk_fwd_left.fbx"),
        ("locomotion_walk_fwd_right", "res://art/animations/player/locomotion/locomotion_walk_fwd_right.fbx"),
        ("death_01", "res://art/animations/player/death/death_01.fbx"),
        ("death_02_back_headshot", "res://art/animations/player/death/death_02_back_headshot.fbx"),
        ("death_03_front_headshot", "res://art/animations/player/death/death_03_front_headshot.fbx"),
        ("death_04_back", "res://art/animations/player/death/death_04_back.fbx"),
        ("death_05_front", "res://art/animations/player/death/death_05_front.fbx"),
    };

    private AnimationPlayer _animationPlayer = null!;
    private AnimationTree _animationTree = null!;
    private AnimationNodeStateMachinePlayback _playback = null!;
    private PlayerMovement.MovementState? _currentState;
    private ulong _jumpAnimationHoldUntilMs;

    public override void _Ready()
    {
        _animationPlayer = GetNode<AnimationPlayer>("../AnimationPlayer");
        _animationTree = GetNode<AnimationTree>("../AnimationTree");
        var player = GetParent<Node>();
        var armsSkeleton = player.FindChild("GeneralSkeleton", true, false) as Skeleton3D;
        if (armsSkeleton == null)
        {
            GD.PushError("Player animation setup could not find the retargeted arms Skeleton3D.");
            return;
        }
        var armsSkeletonPath = player.GetPathTo(armsSkeleton);

        var library = new AnimationLibrary();
        foreach (var (clipName, path) in ImportedClips)
            AddImportedClip(library, clipName, path, armsSkeletonPath);
        _animationPlayer.AddAnimationLibrary("", library);

        var machine = new AnimationNodeStateMachine();
        foreach (var (state, clipName) in StateClips)
            machine.AddNode(state.ToString(), new AnimationNodeAnimation { Animation = new StringName(clipName) }, Vector2.Zero);

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

    private static void AddImportedClip(AnimationLibrary library, string clipName, string path, NodePath targetSkeletonPath)
    {
        var scene = ResourceLoader.Load<PackedScene>(path);
        var importedRoot = scene?.Instantiate();
        var sourcePlayer = importedRoot?.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
        var sourceLibrary = sourcePlayer?.GetAnimationLibrary("");
        var animationNames = sourceLibrary?.GetAnimationList();
        if (animationNames != null && animationNames.Count > 0)
        {
            var animation = sourceLibrary!.GetAnimation(animationNames[0]);
            if (animation != null)
            {
                var retargetedAnimation = (Animation)animation.Duplicate();
                RebindTracksToArmsSkeleton(retargetedAnimation, targetSkeletonPath);
                library.AddAnimation(clipName, retargetedAnimation);
            }
        }
        importedRoot?.QueueFree();
    }

    private static void RebindTracksToArmsSkeleton(Animation animation, NodePath targetSkeletonPath)
    {
        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            string sourcePath = animation.TrackGetPath(trackIndex).ToString();
            int boneSeparator = sourcePath.IndexOf(':');
            if (boneSeparator < 0) continue;

            string boneName = sourcePath[(boneSeparator + 1)..];
            // The imported clips have already been retargeted by Godot's BoneMap: their tracks
            // use the exact target names (Hips, LeftUpperArm, RightHand, …). Keep those names.
            animation.TrackSetPath(trackIndex, new NodePath($"{targetSkeletonPath}:{boneName}"));
        }
    }
}
