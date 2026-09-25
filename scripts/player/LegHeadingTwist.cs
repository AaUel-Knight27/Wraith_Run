using Godot;

/// <summary>
/// Turns the legs towards where the character is travelling while the upper body keeps facing the aim.
///
/// The project only ships forward and backward run cycles (plus 45-degree diagonals); there is no
/// sideways run. Blending the two diagonals to fake one does not work: the legs of a forward-right and
/// a back-right cycle move in opposite directions relative to the body, so the mix barely travels
/// (measured: ~2 m/s of foot travel where 4.7 is needed) and the feet skate. Instead the pelvis is
/// yawed by the missing angle and the spine is un-twisted across Spine, Spine1 and Spine2 so the chest,
/// arms and weapon keep exactly the orientation the animation gave them. The legs then run in the
/// travel direction and the feet plant properly. This is the standard way shooters strafe on a set
/// without strafe clips.
///
/// It runs after the animation and before the hand IK, so the IK sees the final upper body.
/// </summary>
public partial class LegHeadingTwist : SkeletonModifier3D
{
	/// <summary>Yaw of the legs towards the character's right, in radians (negative = left).</summary>
	public float TwistRadians { get; set; }

	private int _hips = -1;
	private readonly int[] _spine = { -1, -1, -1 };
	private bool _resolved;

	private void Resolve(Skeleton3D skeleton)
	{
		_resolved = true;
		_hips = Find(skeleton, "hips");
		_spine[0] = Find(skeleton, "spine");
		_spine[1] = Find(skeleton, "spine1");
		_spine[2] = Find(skeleton, "spine2");
	}

	public override void _ProcessModificationWithDelta(double delta)
	{
		var skeleton = GetSkeleton();
		if (skeleton == null || Mathf.Abs(TwistRadians) < 0.0005f) return;
		if (!_resolved) Resolve(skeleton);
		if (_hips < 0 || _spine[0] < 0 || _spine[1] < 0 || _spine[2] < 0) return;

		// The vertical axis, expressed in the skeleton's own (Blender-oriented) space.
		Vector3 up = (skeleton.GlobalTransform.Basis.Inverse() * Vector3.Up).Normalized();
		// Positive twist = towards the character's right. Godot rotates positively from forward (-Z)
		// towards the LEFT, so a rightward turn is a negative rotation about up.
		float yaw = -TwistRadians;

		// Capture the un-twisted orientation of every bone we are about to touch.
		Basis hipsOld = skeleton.GetBoneGlobalPose(_hips).Basis.Orthonormalized();
		var spineOld = new Basis[3];
		for (int i = 0; i < 3; i++) spineOld[i] = skeleton.GetBoneGlobalPose(_spine[i]).Basis.Orthonormalized();

		// Pelvis takes the whole twist...
		Basis hipsNew = new Basis(up, yaw) * hipsOld;
		skeleton.SetBonePoseRotation(_hips, hipsNew.GetRotationQuaternion());

		// ...and it is handed back along the spine: 2/3 of it is still on Spine, 1/3 on Spine1, none on
		// Spine2, so everything above the waist ends up exactly where the animation put it.
		Basis parentNew = hipsNew;
		for (int i = 0; i < 3; i++)
		{
			float remaining = yaw * (2 - i) / 3.0f;
			Basis desired = new Basis(up, remaining) * spineOld[i];
			skeleton.SetBonePoseRotation(_spine[i], (parentNew.Inverse() * desired).GetRotationQuaternion());
			parentNew = desired;
		}
	}

	private static int Find(Skeleton3D skeleton, string suffixLower)
	{
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
			if (skeleton.GetBoneName(i).ToLowerInvariant().EndsWith(suffixLower)) return i;
		return -1;
	}
}
