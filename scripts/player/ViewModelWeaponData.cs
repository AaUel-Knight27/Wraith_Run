using Godot;

/// <summary>All rig, model, animation and attachment choices for one weapon viewmodel.</summary>
[GlobalClass]
public partial class ViewModelWeaponData : Resource
{
	[Export] public string WeaponName { get; set; } = string.Empty;
	[Export] public bool UseViewModel { get; set; } = true;
	[Export] public bool VisualReviewRequired { get; set; }
	[Export] public PackedScene? RigScene { get; set; }
	[Export] public PackedScene? WeaponModelScene { get; set; }
	[Export] public string AttachBoneName { get; set; } = string.Empty;
	[Export] public Vector3 AttachPosition { get; set; } = Vector3.Zero;
	[Export] public Vector3 AttachRotationDegrees { get; set; } = Vector3.Zero;
	[Export] public Vector3 AttachScale { get; set; } = Vector3.One;
	[Export] public Vector3 ViewPosition { get; set; } = Vector3.Zero;
	[Export] public Vector3 ViewRotationDegrees { get; set; } = Vector3.Zero;
	[Export] public Vector3 ViewScale { get; set; } = Vector3.One;
	[Export] public Godot.Collections.Array<string> ArmMeshNames { get; set; } = new();
	[Export] public Godot.Collections.Dictionary<string, Material> MeshMaterials { get; set; } = new();
	[Export] public Godot.Collections.Array<string> OriginalWeaponMeshNames { get; set; } = new();
	[Export] public Godot.Collections.Array<string> OriginalMagazineMeshNames { get; set; } = new();
	[Export] public bool HideOriginalWeapon { get; set; } = true;
	[Export] public bool HideOriginalMagazine { get; set; } = true;
	[Export] public Godot.Collections.Dictionary<string, string> ClipMap { get; set; } = new();
	[Export] public Godot.Collections.Dictionary<string, float> ClipSpeedMultipliers { get; set; } = new();
	[Export] public float IdlePoseTime { get; set; }
	[Export] public PackedScene? NormalizationReferenceRig { get; set; }
	[Export] public Godot.Collections.Array<string> NormalizationReferenceArmMeshNames { get; set; } = new();
	[Export] public float CullMargin { get; set; } = 0.5f;
	[Export] public Vector3 MuzzlePointOffset { get; set; } = new(0, 0, -0.5f);
	[Export] public string MagazineNodeName { get; set; } = string.Empty;
	[Export] public string MagazineBoneName { get; set; } = string.Empty;
	[Export] public Vector3 MagazinePosition { get; set; } = Vector3.Zero;
	[Export] public Vector3 MagazineRotationDegrees { get; set; } = Vector3.Zero;
	[Export] public Vector3 MagazineScale { get; set; } = Vector3.One;
	[Export] public bool HideModelMagazineWhenDetached { get; set; } = true;

	public float SpeedMultiplier(string genericClip) =>
		ClipSpeedMultipliers.TryGetValue(genericClip, out float value) && value > 0.0f ? value : 1.0f;
}
