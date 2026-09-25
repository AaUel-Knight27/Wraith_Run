using Godot;

/// <summary>
/// Keeps the player's left hand on the weapon.
///
/// The weapon is glued to the RIGHT hand bone (WeaponAttachment). Nothing was gluing the left hand
/// to it, so the left hand simply did whatever each clip said - and the rifle clips disagree with
/// each other: measured against the right hand, the left hand sits ~39 cm along the barrel in the
/// aiming clip but ~50 cm in the sprint clip and ~35 cm in idle, with the sideways offset swinging by
/// 12 cm. Since the gun is at a fixed spot in the right hand, the left hand floated off it in every
/// state except aiming.
///
/// The fix is a rigid rule: the left hand is always at the same spot relative to the right hand.
/// Every frame, after the animation has been applied, a chain of three skeleton modifiers runs:
///
///   1. LeftHandTargetModifier  moves two helper markers to (right hand bone x offset) and to an
///                              elbow-pole point on the chest. It reads the bone pose inside the same
///                              skeleton update, so there is no one-frame lag behind the right hand.
///   2. TwoBoneIK3D             Godot's built-in solver bends shoulder + elbow so the wrist reaches
///                              the marker.
///   3. LeftHandAlignModifier   copies the marker's orientation onto the hand so the fingers wrap
///                              the gun the same way the animation had them wrapped in the aiming pose.
///
/// The default offset is the left hand's pose in the aiming clip (which the animator authored for a
/// rifle), so a rifle needs no per-weapon numbers. WeaponData.LeftHandPosition/Rotation only store an
/// adjustment on top of that. The IK fades out during the reload clip (the reload animation moves the
/// left hand on purpose) and for one-handed weapons.
/// </summary>
public partial class LeftHandIk : Node
{
	// The left hand bone relative to the right hand bone in combat_ads_idle, in skeleton units
	// (centimetres - the glTF skeleton is authored in Mixamo cm). Measured from the clip: the
	// position is ~39 cm along the right hand's +Y, which is the line of fire.
	private static readonly Vector3 BaselinePosition = new(1.833f, 38.788f, 15.844f);
	private static readonly Basis BaselineBasis = new(
		new Vector3(0.0909f, -0.7685f, -0.6333f),
		new Vector3(0.0562f, 0.6389f, -0.7672f),
		new Vector3(0.9943f, 0.0342f, 0.1013f));

	/// <summary>Live tuning: metres, in the right hand bone frame, added to the default hold.</summary>
	public Vector3 OffsetMeters { get; set; }
	/// <summary>Live tuning: degrees, applied on top of the default hold's rotation.</summary>
	public Vector3 OffsetDegrees { get; set; }

	/// <summary>What the current weapon asks for. The tuner can override it.</summary>
	public bool WeaponWantsTwoHands { get; set; } = true;
	/// <summary>Set to true to force the IK on/off regardless of the weapon (tuner checkbox).</summary>
	public bool? ForcedTwoHanded { get; set; }

	/// <summary>Current blend, 0 = pure animation, 1 = pinned to the weapon.</summary>
	public float Weight { get; private set; }

	public Node3D? Target => _target;
	public Node3D? Pole => _pole;
	public TwoBoneIK3D? Solver => _ik;
	public WeaponData? AppliedWeapon => _appliedWeapon;

	private Skeleton3D? _skeleton;
	private Node3D? _playerRoot;
	private PlayerAnimationController? _controller;
	private WeaponAttachment? _attachment;
	private Node3D? _target;
	private Node3D? _pole;
	private TwoBoneIK3D? _ik;
	private LeftHandTargetModifier? _targetModifier;
	private LeftHandAlignModifier? _alignModifier;
	private WeaponData? _appliedWeapon;
	private int _rightHand = -1;
	private int _leftHand = -1;
	private int _chest = -1;
	private float _unit = 100.0f;

	public bool IsReady => _ik != null;

	public void Setup(Skeleton3D skeleton, Node3D playerRoot, PlayerAnimationController controller)
	{
		_skeleton = skeleton;
		_playerRoot = playerRoot;
		_controller = controller;
		_attachment = playerRoot.GetNodeOrNull<WeaponAttachment>("Armature/WeaponAttachment");

		_rightHand = FindBoneEndingWith(skeleton, "righthand");
		_leftHand = FindBoneEndingWith(skeleton, "lefthand");
		_chest = FindBoneEndingWith(skeleton, "spine2");
		int upperArm = FindBoneEndingWith(skeleton, "leftarm");
		int foreArm = FindBoneEndingWith(skeleton, "leftforearm");
		if (_rightHand < 0 || _leftHand < 0 || _chest < 0 || upperArm < 0 || foreArm < 0)
		{
			GD.PushWarning("LeftHandIk: could not find the arm bones; the left hand will follow the animation.");
			return;
		}

		// Skeleton units -> metres. The skeleton's global scale is 0.01 (Mixamo centimetres).
		float scale = skeleton.GlobalTransform.Basis.Scale.X;
		_unit = Mathf.IsZeroApprox(scale) ? 100.0f : 1.0f / scale;

		_target = new Node3D { Name = "LeftHandTarget" };
		_pole = new Node3D { Name = "LeftElbowPole" };
		AddChild(_target);
		AddChild(_pole);

		// Modifier order is child order, and it matters: sync markers -> solve -> align hand.
		_targetModifier = new LeftHandTargetModifier { Name = "LeftHandTargetSync", Owner3D = this };
		skeleton.AddChild(_targetModifier);

		_ik = new TwoBoneIK3D { Name = "LeftArmIK" };
		skeleton.AddChild(_ik);
		_ik.SettingCount = 1;
		_ik.SetRootBoneName(0, skeleton.GetBoneName(upperArm));
		_ik.SetMiddleBoneName(0, skeleton.GetBoneName(foreArm));
		_ik.SetEndBoneName(0, skeleton.GetBoneName(_leftHand));
		_ik.SetTargetNode(0, _ik.GetPathTo(_target));
		_ik.SetPoleNode(0, _ik.GetPathTo(_pole));
		_ik.SetPoleDirection(0, PoleDirectionAxis);

		_alignModifier = new LeftHandAlignModifier { Name = "LeftHandAlign", Owner3D = this };
		skeleton.AddChild(_alignModifier);

		Weight = 0.0f;
		_ik.Influence = 0.0f;
		_alignModifier.Influence = 0.0f;
	}

	/// <summary>Which axis of the elbow bone points at the pole. Confirmed against the Manny rig
	/// by measurement (see the animation probe): with this axis the elbow ends up below and
	/// outside the shoulder-wrist line instead of flipping up.</summary>
	public static SkeletonModifier3D.SecondaryDirection PoleDirectionAxis = SkeletonModifier3D.SecondaryDirection.PlusX;

	public override void _Process(double delta)
	{
		if (_ik == null || _skeleton == null) return;

		var weapon = _attachment?.CurrentWeapon;
		if (!ReferenceEquals(weapon, _appliedWeapon)) ApplyWeapon(weapon);

		bool wants = ForcedTwoHanded ?? WeaponWantsTwoHands;
		bool blocked = _controller == null || _controller.IsDead || _controller.IsReloadPlaying;
		float goal = AnimationTuning.HandIkEnabled && wants && !blocked && weapon != null ? 1.0f : 0.0f;
		Weight = Mathf.MoveToward(Weight, goal, (float)delta * AnimationTuning.HandIkBlendPerSecond);

		// Influence is applied by Skeleton3D itself; the modifiers always solve at full strength.
		_ik.Influence = Weight;
		_ik.Active = Weight > 0.001f;
		_alignModifier!.Influence = Weight;
		_alignModifier.Active = Weight > 0.001f;
	}

	private void ApplyWeapon(WeaponData? weapon)
	{
		_appliedWeapon = weapon;
		if (weapon == null) return;
		WeaponWantsTwoHands = weapon.UsesTwoHands;
		OffsetMeters = weapon.LeftHandPosition;
		OffsetDegrees = weapon.LeftHandRotationDegrees;
	}

	/// <summary>Forgets tuner edits and re-reads the current weapon's stored values.</summary>
	public void ResetToWeapon()
	{
		ForcedTwoHanded = null;
		ApplyWeapon(_appliedWeapon);
	}

	// -------------------------------------------------------------------------------------------
	// Called from the skeleton modifiers, inside the skeleton's own update
	// -------------------------------------------------------------------------------------------

	internal void SyncMarkers(Skeleton3D skeleton)
	{
		if (_target == null || _pole == null || _playerRoot == null || _rightHand < 0) return;

		Transform3D right = skeleton.GetBoneGlobalPose(_rightHand);
		Basis offsetRotation = Basis.FromEuler(OffsetDegrees * (Mathf.Pi / 180.0f), EulerOrder.Yxz);
		Vector3 position = BaselinePosition + OffsetMeters * _unit;
		Transform3D handInSkeleton = new(right.Basis.Orthonormalized() * BaselineBasis * offsetRotation,
			right.Origin + right.Basis * position);
		Transform3D world = skeleton.GlobalTransform * handInSkeleton;
		_target.GlobalTransform = new Transform3D(world.Basis.Orthonormalized(), world.Origin);

		Vector3 chest = (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(_chest)).Origin;
		Vector3 poleOffset = new(AnimationTuning.PoleX, AnimationTuning.PoleY, AnimationTuning.PoleZ);
		_pole.GlobalPosition = chest + _playerRoot.GlobalTransform.Basis.Orthonormalized() * poleOffset;
	}

	internal void AlignHand(Skeleton3D skeleton)
	{
		if (_target == null || _leftHand < 0) return;
		Basis desired = (skeleton.GlobalTransform.AffineInverse() * _target.GlobalTransform).Basis.Orthonormalized();
		int parent = skeleton.GetBoneParent(_leftHand);
		Basis parentGlobal = skeleton.GetBoneGlobalPose(parent).Basis.Orthonormalized();
		skeleton.SetBonePoseRotation(_leftHand, (parentGlobal.Inverse() * desired).GetRotationQuaternion());
	}

	/// <summary>The weapon-data lines to paste under this weapon in WeaponRegistry.tres.</summary>
	public string ToResourceLines()
	{
		string F(float v)
		{
			string s = v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
			return s == "-0" ? "0" : s;
		}
		return
			"LeftHandMode = " + ((ForcedTwoHanded ?? WeaponWantsTwoHands) ? "1" : "2") + "\n" +
			$"LeftHandPosition = Vector3({F(OffsetMeters.X)}, {F(OffsetMeters.Y)}, {F(OffsetMeters.Z)})\n" +
			$"LeftHandRotationDegrees = Vector3({F(OffsetDegrees.X)}, {F(OffsetDegrees.Y)}, {F(OffsetDegrees.Z)})";
	}

	private static int FindBoneEndingWith(Skeleton3D skeleton, string suffixLower)
	{
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
			if (skeleton.GetBoneName(i).ToLowerInvariant().EndsWith(suffixLower)) return i;
		return -1;
	}
}

/// <summary>Step 1: put the IK target and pole where they belong for THIS frame's right hand.</summary>
public partial class LeftHandTargetModifier : SkeletonModifier3D
{
	public LeftHandIk? Owner3D { get; set; }

	public override void _ProcessModificationWithDelta(double delta)
	{
		var skeleton = GetSkeleton();
		if (skeleton != null) Owner3D?.SyncMarkers(skeleton);
	}
}

/// <summary>Step 3: after the arm has been solved, give the hand the marker's orientation.</summary>
public partial class LeftHandAlignModifier : SkeletonModifier3D
{
	public LeftHandIk? Owner3D { get; set; }

	public override void _ProcessModificationWithDelta(double delta)
	{
		var skeleton = GetSkeleton();
		if (skeleton != null) Owner3D?.AlignHand(skeleton);
	}
}
