using Godot;

/// <summary>Small rest-relative camera rotation layer for rigs with an animated camera bone.</summary>
public partial class ViewModelCameraMotion : Node3D
{
	[Export] public float Strength { get; set; } = 0.5f;
	[Export] public string BoneName { get; set; } = "Head_Cam";
	[Export] public bool FlipX { get; set; }
	[Export] public bool FlipY { get; set; }
	[Export] public bool FlipZ { get; set; }

	private Skeleton3D? _skeleton;
	private int _boneIndex = -1;
	private Quaternion _restRotation = Quaternion.Identity;
	private ViewModelBase? _viewModel;

	public void SetViewModel(ViewModelBase? viewModel)
	{
		_viewModel = viewModel;
		_skeleton = viewModel?.FindChild("Skeleton3D", true, false) as Skeleton3D;
		if (_skeleton == null)
		{
			_boneIndex = -1;
			Rotation = Vector3.Zero;
			return;
		}

		string boneToFind = !string.IsNullOrEmpty(viewModel?.Data?.CameraBoneName)
			? viewModel.Data.CameraBoneName
			: BoneName;

		_boneIndex = _skeleton.FindBone(boneToFind);
		if (_boneIndex < 0)
		{
			foreach (string candidate in new[] { "camera", "Head_Cam", "Camera", "head_cam" })
			{
				_boneIndex = _skeleton.FindBone(candidate);
				if (_boneIndex >= 0) break;
			}
		}

		if (_boneIndex < 0)
		{
			Rotation = Vector3.Zero;
			return;
		}
		_restRotation = _skeleton.GetBoneRest(_boneIndex).Basis.GetRotationQuaternion();
	}

	public override void _Process(double delta)
	{
		if (_skeleton == null || _boneIndex < 0 || !IsInstanceValid(_skeleton))
		{
			Rotation = Vector3.Zero;
			return;
		}

		Quaternion pose = _skeleton.GetBonePoseRotation(_boneIndex);
		Quaternion offset = _restRotation.Inverse() * pose;
		Vector3 euler = offset.GetEuler();
		if (FlipX) euler.X = -euler.X;
		if (FlipY) euler.Y = -euler.Y;
		if (FlipZ) euler.Z = -euler.Z;
		Quaternion adjustedOffset = Basis.FromEuler(euler).GetRotationQuaternion();

		// Damp camera bone sway during ADS so sights remain stable
		float adsDamping = (_viewModel != null && _viewModel.IsAiming) ? 0.15f : 1.0f;
		Quaternion blended = Quaternion.Identity.Slerp(adjustedOffset, Mathf.Clamp(Strength * adsDamping, 0.0f, 1.0f));
		if (blended.IsFinite())
			Rotation = blended.GetEuler();
	}
}
