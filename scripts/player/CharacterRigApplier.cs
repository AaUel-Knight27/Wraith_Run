using Godot;
using System.Collections.Generic;

/// <summary>
/// The two runtime consumers of CharacterRigTuning. Both run on EVERY player - local and remote,
/// debug and release builds alike - because once numbers are pasted into CharacterRigTuning's
/// LoadDefaults() they are part of the shipped game, not a debug feature. The only debug-only piece
/// of this system is the editor UI itself (scripts/debug/CharacterRigTuner.cs) that writes the live
/// values these two classes read.
/// </summary>

/// <summary>
/// Applies per-bone position/rotation/scale overrides after everything else has posed the skeleton
/// this frame - animation, LegHeadingTwist, and LeftHandIk's two modifiers. Added as the LAST child
/// of the skeleton (see PlayerAnimationController._Ready), the same trick LeftHandAlignModifier uses
/// to have the final say: skeleton modifiers run in child order, so whatever this class writes is
/// what actually renders.
///
/// Overrides are DELTAS on top of whatever pose is already there, not replacements - a position/
/// rotation offset stays sensible however the underlying clip or IK solve changes, instead of fighting
/// a specific animation's pose. A bone with no override for the currently active PoseMode (see
/// CharacterRigTuning.ActiveLayersHighToLow) is left exactly as the animation/IK left it.
/// </summary>
public partial class CharacterRigOverrideModifier : SkeletonModifier3D
{
	private PlayerMovement? _movement;
	private PlayerAnimationController? _controller;
	private readonly HashSet<string> _appliedThisFrame = new();

	public void Setup(PlayerMovement movement, PlayerAnimationController controller)
	{
		_movement = movement;
		_controller = controller;
	}

	public override void _ProcessModificationWithDelta(double delta)
	{
		var skeleton = GetSkeleton();
		if (skeleton == null || _movement == null || _controller == null) return;

		// Metres -> the skeleton's own raw authored units (Mixamo centimetres, undone by the 0.01
		// scale manny.glb carries at its root). Recomputed fresh every frame rather than cached, so a
		// live "whole character scale" edit never leaves this conversion stale - see
		// CharacterRigCameraScaleApplier and WeaponAttachment.InverseScaleOf for the same correction.
		float rawScale = skeleton.GlobalTransform.Basis.Scale.X;
		float unit = Mathf.IsZeroApprox(rawScale) ? 100.0f : 1.0f / rawScale;

		// TunedBoneNames() is every bone tuned in ANY mode - typically a handful, so walking it once
		// per active layer (at most five: Fire, Reload, Aim, the movement state, Global) is cheap.
		// For each bone, the first layer with an EXPLICIT entry (TryGetBone) wins; a lower-priority
		// mode's entry for the same bone is correctly ignored, identity-reset entries included.
		_appliedThisFrame.Clear();
		foreach (var mode in CharacterRigTuning.ActiveLayersHighToLow(_movement, _controller))
		{
			foreach (string boneName in CharacterRigTuning.TunedBoneNames())
			{
				if (_appliedThisFrame.Contains(boneName)) continue;
				if (!CharacterRigTuning.TryGetBone(mode, boneName, out var ov)) continue;
				_appliedThisFrame.Add(boneName);
				int index = skeleton.FindBone(boneName);
				if (index < 0) continue;
				ApplyOverride(skeleton, index, ov, unit);
			}
		}
	}

	private static void ApplyOverride(Skeleton3D skeleton, int boneIndex, CharacterRigTuning.BoneOverride ov, float unit)
	{
		if (ov.IsIdentity) return;
		skeleton.SetBonePosePosition(boneIndex, skeleton.GetBonePosePosition(boneIndex) + ov.PositionOffset * unit);

		if (ov.RotationOffsetDegrees != Vector3.Zero)
		{
			Basis current = new Basis(skeleton.GetBonePoseRotation(boneIndex));
			Basis extra = Basis.FromEuler(ov.RotationOffsetDegrees * (Mathf.Pi / 180.0f), EulerOrder.Yxz);
			skeleton.SetBonePoseRotation(boneIndex, (current * extra).GetRotationQuaternion());
		}

		if (!Mathf.IsEqualApprox(ov.ScaleMultiplier, 1.0f))
			skeleton.SetBonePoseScale(boneIndex, skeleton.GetBonePoseScale(boneIndex) * ov.ScaleMultiplier);
	}
}

/// <summary>
/// Applies the per-mode first-person camera offset and whole-character scale. Neither lives on the
/// skeleton (Camera3D is a plain sibling under Head; character scale is the Armature node's own
/// Scale), so this is a regular Node rather than a SkeletonModifier3D, ticking in _Process alongside
/// everything else PlayerAnimationController owns.
///
/// Camera offsets are added on top of FirstPersonCamera's scene-authored local transform - the same
/// value it has always had, captured once in Setup(). Nothing else in the project ever touches that
/// local transform at runtime (PlayerMovement only ever moves Head and the camera's Fov - see its own
/// class comment on FirstPersonMp5Viewmodel), so there is nothing for this to race against.
/// </summary>
public partial class CharacterRigCameraScaleApplier : Node
{
	private Camera3D? _camera;
	private Node3D? _armature;
	private PlayerMovement? _movement;
	private PlayerAnimationController? _controller;
	private WeaponAttachment? _worldAttachment;
	private Vector3 _baseCameraPosition;
	private Vector3 _baseCameraRotationDegrees;

	public void Setup(Camera3D camera, Node3D armature, PlayerMovement movement, PlayerAnimationController controller)
	{
		_camera = camera;
		_armature = armature;
		_movement = movement;
		_controller = controller;
		_baseCameraPosition = camera.Position;
		_baseCameraRotationDegrees = camera.RotationDegrees;
		_worldAttachment = armature.GetNodeOrNull<WeaponAttachment>("WeaponAttachment");
	}

	public override void _Process(double delta)
	{
		if (_camera == null || _armature == null || _movement == null || _controller == null) return;

		CharacterRigTuning.CameraOverride? camera = null;
		float? scale = null;
		foreach (var mode in CharacterRigTuning.ActiveLayersHighToLow(_movement, _controller))
		{
			if (camera == null && CharacterRigTuning.TryGetCamera(mode, out var camOv)) camera = camOv;
			if (scale == null && CharacterRigTuning.TryGetScale(mode, out float s)) scale = s;
			if (camera != null && scale != null) break;
		}

		var resolvedCamera = camera ?? CharacterRigTuning.CameraOverride.Identity;
		_camera.Position = _baseCameraPosition + resolvedCamera.PositionOffset;
		_camera.RotationDegrees = _baseCameraRotationDegrees + resolvedCamera.RotationOffsetDegrees;

		float resolvedScale = scale ?? 1.0f;
		if (!Mathf.IsEqualApprox(_armature.Scale.X, resolvedScale))
		{
			_armature.Scale = Vector3.One * resolvedScale;
			// The weapon grip's unit correction (WeaponAttachment.InverseScaleOf) is only recomputed
			// at Equip() time. Re-equipping the same weapon after a live scale change keeps the gun
			// sized correctly instead of drifting until the next weapon switch.
			if (_worldAttachment?.CurrentWeapon != null) _worldAttachment.Equip(_worldAttachment.CurrentWeapon);
		}
	}
}
