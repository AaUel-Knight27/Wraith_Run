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
