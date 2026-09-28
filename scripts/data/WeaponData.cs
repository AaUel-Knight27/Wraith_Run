using Godot;
using System;

[GlobalClass]
public partial class WeaponData : Resource
{
	public enum FireModeType { FullAuto, SemiAuto, Melee }

	[Export] public string WeaponName { get; set; } = string.Empty;
	[Export] public FireModeType FireMode { get; set; } = FireModeType.FullAuto;
	[Export] public float BodyDamage { get; set; }
	[Export] public float HeadshotDamage { get; set; }
	[Export] public float FireRateRPM { get; set; }
	[Export] public int MagazineSize { get; set; }

	/// <summary>Rounds carried beyond the loaded magazine - what a reload draws from. The Tactical
	/// HUD's ammo counter reads "MAG / RESERVE" (Master Development Plan Part 7.3), so this is what
	/// fills the second number. 0 on a melee weapon (MagazineSize is also 0 there, so neither shows).</summary>
	[Export] public int MaxReserveAmmo { get; set; }

	[Export] public float ReloadTime { get; set; }
	[Export] public float RecoilVertical { get; set; }
	[Export] public float RecoilHorizontal { get; set; }
	[Export] public float RecoilRecoveryMs { get; set; }
	[Export] public string WeaponClass { get; set; } = string.Empty;
	[Export(PropertyHint.File, "*.glb,*.gltf,*.tscn")] public string ModelScenePath { get; set; } = string.Empty;

	// ---------------------------------------------------------------------------------------
	// How the model sits in the hand.
	//
	// The weapon is parented to a BoneAttachment3D on mixamorig:RightHand, so these are
	// expressed in that bone's local frame. Sampling combat_ads_idle at t=0 shows that in the
	// rifle-aiming pose the hand bone's local +Y points along the character's line of fire and
	// its local +X points up - which is why the defaults are 90-degree rotations rather than
	// identity: the source models are authored with the barrel down their own +X or +Z.
	//
	// These are starting values derived from each model's own geometry, not values anyone has
	// eyeballed in the editor yet. Nudge them in the inspector; a weapon re-reads them on the
	// next equip, so pressing its number key again applies the change without a restart.
	// ---------------------------------------------------------------------------------------

	/// <summary>Offset from the hand bone to the weapon's origin, in metres.</summary>
	[Export] public Vector3 GripPosition { get; set; } = Vector3.Zero;

	/// <summary>Rotation of the model in the hand bone's frame, in degrees (Godot YXZ order).</summary>
	[Export] public Vector3 GripRotationDegrees { get; set; } = Vector3.Zero;

	/// <summary>
	/// Uniform scale applied to the model. The source models are wildly inconsistent - the AK is
	/// already 1:1 metres, the Mossberg is 13.8 units long and the P90 is 19.7 - so each one gets
	/// the factor that brings its longest axis to the real weapon's length.
	/// </summary>
	[Export] public float ModelScale { get; set; } = 1.0f;

	/// <summary>Distance from the grip to the muzzle along the line of fire, in metres. Used to
	/// place the muzzle flash; 0 disables the flash, which is what melee wants.</summary>
	[Export] public float MuzzleDistance { get; set; }

	// ---------------------------------------------------------------------------------------
	// Support (left) hand. The weapon is glued to the right hand, so the left hand has to be glued
	// to the weapon as well or it drifts off the gun whenever the clip changes (the rifle clips
	// disagree with each other by up to ~15 cm about where the left hand goes). LeftHandIk pins
	// it. The two vectors below are ADJUSTMENTS on top of the default hold, which is the pose the
	// aiming clip already uses for a rifle - so a rifle needs no numbers at all, and a weapon that
	// needs the hand moved only stores the difference. Tune them live with the animation tuner (F8).
	// ---------------------------------------------------------------------------------------

	public enum LeftHandModeType { Auto, TwoHanded, OneHanded }

	/// <summary>Auto = two-handed for everything except Pistol and Melee classes. OneHanded leaves
	/// the left hand to the animation, TwoHanded pins it to the weapon.</summary>
	[Export] public LeftHandModeType LeftHandMode { get; set; } = LeftHandModeType.Auto;

	/// <summary>Left-hand offset from the default hold, in metres, in the right hand bone's frame
	/// (the same frame GripPosition uses; +Y runs along the barrel).</summary>
	[Export] public Vector3 LeftHandPosition { get; set; } = Vector3.Zero;

	/// <summary>Extra rotation of the left hand on top of the default hold, in degrees.</summary>
	[Export] public Vector3 LeftHandRotationDegrees { get; set; } = Vector3.Zero;

	/// <summary>Resolves Auto to a yes/no.</summary>
	public bool UsesTwoHands => LeftHandMode switch
	{
		LeftHandModeType.TwoHanded => true,
		LeftHandModeType.OneHanded => false,
		_ => WeaponClass != "Pistol" && WeaponClass != "Melee",
	};

	// ---------------------------------------------------------------------------------------
	// First-person viewmodel grip (fps_mp5_arms.glb - see MP5_VIEWMODEL_README.md).
	//
	// The viewmodel's right hand ("Hand.R") is a completely different bone, on a completely
	// different skeleton, than the world-model "righthand" bone GripPosition/GripRotationDegrees
	// above are expressed in - a rifle held correctly on one will not sit right on the other. The
	// two are independent on purpose: this weapon's WORLD-model grip (what other players and the
	// third-person mesh see) is untouched by anything below.
	//
	// Same "adjustment on top of a default hold" pattern as LeftHandPosition/LeftHandRotationDegrees
	// above: the default hold is where the viewmodel's own MP5 model's body bone ("Base") sits
	// relative to Hand.R in mp5_idle, measured directly from the rig - so a weapon shaped roughly
	// like the MP5 (any of the rifles) needs no numbers at all. A pistol or the knife will need a
	// real adjustment. There is no F-key tuner for these yet (unlike GripPosition's WeaponGripTuner /
	// F3): edit the two fields below directly on the weapon's .tres and re-run to see the result, or
	// nudge FirstPersonMp5Viewmodel's ExtraPositionMeters/ExtraRotationDegrees live via the Scene
	// dock's "Remote" tab for a quick global check (that offsets the whole viewmodel, every weapon,
	// rather than one weapon - fold a per-weapon result back into the fields below afterward).
	// ---------------------------------------------------------------------------------------

	/// <summary>Viewmodel-hand offset from the default hold, in metres, in Hand.R's local frame.</summary>
	[Export] public Vector3 ViewmodelGripPosition { get; set; } = Vector3.Zero;

	/// <summary>Extra rotation of the viewmodel grip on top of the default hold, in degrees.</summary>
	[Export] public Vector3 ViewmodelGripRotationDegrees { get; set; } = Vector3.Zero;

	// --- ballistics -------------------------------------------------------------------------

	/// <summary>Rays fired per trigger pull. 1 for everything except the shotgun.</summary>
	[Export] public int PelletsPerShot { get; set; } = 1;

	/// <summary>Half-angle of the cone pellets are scattered into, in degrees.</summary>
	[Export] public float SpreadDegrees { get; set; }

	/// <summary>Pump/tube weapons load one shell at a time and can be interrupted by firing.
	/// ReloadTime is then the per-shell time rather than the whole magazine.</summary>
	[Export] public bool ShellByShellReload { get; set; }

	// --- audio ------------------------------------------------------------------------------
	// Paths rather than AudioStream references, so a sound can be swapped by editing one string
	// (or by dropping a new file at the same path) without touching any scene.

	[Export] public string[] FireSounds { get; set; } = Array.Empty<string>();
	[Export] public string[] ReloadSounds { get; set; } = Array.Empty<string>();
	[Export] public string[] ImpactSounds { get; set; } = Array.Empty<string>();
	[Export] public string EmptySound { get; set; } = string.Empty;

	/// <summary>Metres at which the fire sound falls to roughly half volume. Bigger weapons carry
	/// further; melee should stay small.</summary>
	[Export] public float FireAudioUnitSize { get; set; } = 12.0f;
}
