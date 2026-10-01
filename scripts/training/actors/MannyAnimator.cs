using Godot;
using System.Collections.Generic;

/// <summary>
/// Plays Manny's real animation clips on a lightweight body (bots and friends).
///
/// The player's PlayerAnimationController is hard-wired to PlayerMovement, so it cannot drive a bot. This
/// imports the very same three clip files the same way (duplicate each clip and rewrite every bone track to
/// point at THIS body's skeleton) and plays them through a plain AnimationPlayer - no AnimationTree needed.
/// </summary>
public partial class MannyAnimator : Node
{
    public const string Idle = "locomotion_idle";
    public const string Run = "locomotion_sprint_fwd";
    public const string CrouchIdle = "locomotion_crouch_idle";
    public const string CrouchWalk = "locomotion_crouch_walk";
    public const string Aim = "combat_ads_idle";
    public const string Death = "Death From The Front";

    private static readonly string[] ClipSources =
    {
        "res://art/animations/player/locomotion/player animation locomotion.glb",
        "res://art/animations/player/combat/player animation combat.glb",
        "res://art/animations/player/death/player animation death.glb",
    };

    private static readonly string[] LoopingClips = { Idle, Run, CrouchIdle, CrouchWalk, Aim };

    private AnimationPlayer? _player;
    private string _current = string.Empty;
    private float _currentSpeed = 1.0f;

    /// <summary>root = the body the AnimationPlayer is a child of; armature = the Manny scene under it.</summary>
    public void Setup(Node3D root, Node3D armature)
    {
        Skeleton3D? skeleton = FindSkeleton(armature);
        if (skeleton == null)
        {
            GD.PushError("MannyAnimator: no Skeleton3D under the Manny armature.");
            return;
        }

        _player = new AnimationPlayer { Name = "AnimationPlayer" };
        root.AddChild(_player);
        NodePath skeletonPath = root.GetPathTo(skeleton);

        // Every Manny body has the same node layout, so the retargeted clips are identical: build them once and share the
        // (read-only) library between all actors. Without this each spawn re-imports three animation files.
        string pathKey = skeletonPath.ToString();
        if (_sharedLibrary == null || _sharedPathKey != pathKey)
        {
            var library = new AnimationLibrary();
            foreach (string source in ClipSources) ImportClips(source, library, skeletonPath);
            foreach (string clip in LoopingClips)
                if (library.HasAnimation(clip)) library.GetAnimation(clip).LoopMode = Animation.LoopModeEnum.Linear;
            _sharedLibrary = library;
            _sharedPathKey = pathKey;
        }
        _player.AddAnimationLibrary("", _sharedLibrary);
    }

    private static AnimationLibrary? _sharedLibrary;
    private static string _sharedPathKey = string.Empty;

    public bool Has(string clip) => _player != null && _player.HasAnimation(clip);

    public float Length(string clip) => Has(clip) ? (float)_player!.GetAnimation(clip).Length : 0.0f;

    /// <summary>Loops a clip; no-op if it is already playing at (about) this speed.</summary>
    public void Play(string clip, float speedScale = 1.0f, float blend = 0.2f)
    {
        if (_player == null || !_player.HasAnimation(clip)) return;
        if (_current == clip && Mathf.Abs(_currentSpeed - speedScale) < 0.05f) return;
        _current = clip;
        _currentSpeed = speedScale;
        _player.Play(clip, blend, speedScale);
    }

    /// <summary>Plays a clip once and holds its last frame (death).</summary>
    public void PlayOnce(string clip)
    {
        if (_player == null || !_player.HasAnimation(clip)) return;
        _current = clip;
        _currentSpeed = 1.0f;
        _player.Play(clip, 0.1, 1.0f);
    }

    private static Skeleton3D? FindSkeleton(Node root)
    {
        if (root is Skeleton3D skeleton) return skeleton;
        foreach (Node child in root.GetChildren())
        {
            Skeleton3D? found = FindSkeleton(child);
            if (found != null) return found;
        }
        return null;
    }

    private static void ImportClips(string scenePath, AnimationLibrary library, NodePath targetSkeletonPath)
    {
        var scene = ResourceLoader.Load<PackedScene>(scenePath);
        Node? importedRoot = scene?.Instantiate();
        var sourcePlayer = importedRoot?.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
        if (sourcePlayer == null)
        {
            GD.PushError($"MannyAnimator: no AnimationPlayer found in {scenePath}.");
            importedRoot?.QueueFree();
            return;
        }

        foreach (StringName libraryName in sourcePlayer.GetAnimationLibraryList())
        {
            AnimationLibrary sourceLibrary = sourcePlayer.GetAnimationLibrary(libraryName);
            foreach (StringName clipName in sourceLibrary.GetAnimationList())
            {
                Animation? animation = sourceLibrary.GetAnimation(clipName);
                if (animation == null) continue;
                var retargeted = (Animation)animation.Duplicate();
                Rebind(retargeted, targetSkeletonPath);
                if (library.HasAnimation(clipName)) library.RemoveAnimation(clipName);
                library.AddAnimation(clipName, retargeted);
            }
        }
        importedRoot?.QueueFree();
    }

    private static void Rebind(Animation animation, NodePath targetSkeletonPath)
    {
        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            string sourcePath = animation.TrackGetPath(trackIndex).ToString();
            int boneSeparator = sourcePath.IndexOf(':');
            if (boneSeparator < 0) continue;
            // Bone names contain a colon ("mixamorig:Hips"): only the FIRST colon separates path from bone.
            string boneName = sourcePath[(boneSeparator + 1)..];
            animation.TrackSetPath(trackIndex, new NodePath($"{targetSkeletonPath}:{boneName}"));
        }
    }
}
