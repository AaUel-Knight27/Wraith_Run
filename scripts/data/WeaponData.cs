using Godot;

[GlobalClass]
public partial class WeaponData : Resource
{
	[Export] public string WeaponName { get; set; } = string.Empty;
	[Export] public float BodyDamage { get; set; }
	[Export] public float HeadshotDamage { get; set; }
	[Export] public float FireRateRPM { get; set; }
	[Export] public int MagazineSize { get; set; }
	[Export] public float ReloadTime { get; set; }
	[Export] public float RecoilVertical { get; set; }
	[Export] public float RecoilHorizontal { get; set; }
	[Export] public float RecoilRecoveryMs { get; set; }
	[Export] public string WeaponClass { get; set; } = string.Empty;
	[Export(PropertyHint.File, "*.fbx,*.glb,*.tscn")] public string ModelScenePath { get; set; } = string.Empty;
}
