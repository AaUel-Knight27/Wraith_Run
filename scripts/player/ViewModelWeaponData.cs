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
	/// <summary>Offset added to the resting viewmodel position while aiming. This moves the real
	/// hands and weapon into the sight picture rather than placing a flat image over the screen.</summary>
	[Export] public Vector3 AimPositionOffset { get; set; } = Vector3.Zero;
	[Export] public Vector3 AimRotationOffsetDegrees { get; set; } = Vector3.Zero;
	[Export] public float AimTransitionSeconds { get; set; } = 0.12f;
	/// <summary>Only weapons with a scope or dedicated scope animation allow aiming down sights.</summary>
	[Export] public bool HasScope { get; set; }
	/// <summary>When true, ViewModelBase automatically aligns its position and rotation so that the
	/// rig's internal Camera3D matches the player's FirstPersonCamera.</summary>
	[Export] public bool UseRigCameraTransform { get; set; }
	/// <summary>Name of the camera bone in the rig if the rig has an animated camera bone (e.g. "camera", "Head_Cam").</summary>
	[Export] public string CameraBoneName { get; set; } = string.Empty;
	/// <summary>Authored camera position within the rig if no camera node is in the FBX scene.</summary>
	[Export] public Vector3 RigCameraPosition { get; set; } = Vector3.Zero;
	/// <summary>Authored camera rotation degrees within the rig.</summary>
	[Export] public Vector3 RigCameraRotationDegrees { get; set; } = Vector3.Zero;
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
	// Optional model-space muzzle socket. When absent, MuzzlePointOffset is relative to the weapon model.
	[Export] public string MuzzleNodeName { get; set; } = string.Empty;
	[Export] public Vector3 MuzzleDirectionOffsetDegrees { get; set; } = Vector3.Zero;
	[Export] public bool MuzzleFlashEnabled { get; set; }
	[Export] public float MuzzleFlashScale { get; set; } = 1.0f;
	[Export] public Color MuzzleFlashColor { get; set; } = new(1.0f, 0.82f, 0.45f, 1.0f);
	[Export] public float MuzzleFlashDuration { get; set; } = 0.05f;
	[Export] public float MuzzleLightEnergy { get; set; } = 2.4f;
	[Export] public bool MuzzleSmokeEnabled { get; set; }
	[Export] public float MuzzleParticleSpeed { get; set; } = 4.0f;
	[Export] public float MuzzleSpreadDegrees { get; set; } = 12.0f;
	[Export] public string MagazineNodeName { get; set; } = string.Empty;
	[Export] public string MagazineBoneName { get; set; } = string.Empty;
	[Export] public Vector3 MagazinePosition { get; set; } = Vector3.Zero;
	[Export] public Vector3 MagazineRotationDegrees { get; set; } = Vector3.Zero;
	[Export] public Vector3 MagazineScale { get; set; } = Vector3.One;
	[Export] public bool HideModelMagazineWhenDetached { get; set; } = true;

	public float SpeedMultiplier(string genericClip) =>
		ClipSpeedMultipliers.TryGetValue(genericClip, out float value) && value > 0.0f ? value : 1.0f;
}
