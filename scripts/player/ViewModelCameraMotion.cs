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

	public void SetViewModel(ViewModelBase? viewModel)
	{
		_skeleton = viewModel?.FindChild("Skeleton3D", true, false) as Skeleton3D;
		_boneIndex = _skeleton?.FindBone(BoneName) ?? -1;
		if (_skeleton == null || _boneIndex < 0) return;
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
		Quaternion blended = Quaternion.Identity.Slerp(adjustedOffset, Mathf.Clamp(Strength, 0.0f, 1.0f));
		Rotation = blended.GetEuler();
	}
}
