using Godot;

/// <summary>
/// The first-person viewmodel: fps_mp5_arms.glb's own arms + skeleton, playing its own
/// mp5_idle/mp5_shoot/mp5_reload/mp5_equip clips. Parented directly under FirstPersonCamera in
/// Player.tscn, so it inherits every bit of head look and recoil for free - nothing else ever
/// touches the camera's own local transform (see PlayerMovement.UpdateFov/AddRecoil, both of which
/// only ever move Head or the camera's Fov). The instanced scene's own root transform already
/// places it correctly: it was baked into fps_mp5_arms.glb itself from the rig's own "CameraHelper"
/// bind pose, so this node needs no position/rotation of its own - see MP5_VIEWMODEL_README.md for
/// the full derivation.
///
/// Local player only. Manny's own body mesh and the world-model weapon (WeaponAttachment on
/// Armature) are untouched for every other peer - this only ever changes what the owning player
/// sees of their own hands, exactly like Camera3D.Current already does for the camera itself
/// (PlayerMovement._Ready).
/// </summary>
public partial class FirstPersonMp5Viewmodel : Node3D
{
	// Names baked into fps_mp5_arms.glb - see MP5_VIEWMODEL_README.md for what each clip is and,
	// for mp5_equip, why the name is inferred rather than read off a label the file actually had.
	private const string IdleClip = "mp5_idle";
	private const string ShootClip = "mp5_shoot";
	private const string ReloadClip = "mp5_reload";
	private const string EquipClip = "mp5_equip";

	// mp5_equip's last frame is bit-for-bit mp5_idle's rest pose (see MP5_VIEWMODEL_README.md), and
	// mp5_idle itself is a single static pose, so a short blend is enough to be responsive without
	// a visible pop when this hands back to idle.
	private const float BlendSeconds = 0.08f;

	// The rig's own MP5 body/magazine/charging-handle meshes came along for the ride from the FBX
	// export, but every weapon - MP5 included, if it is ever added to the loadout - is rendered by
	// WeaponAttachment instead, through the same Grip system every other weapon already uses (see
	// WeaponAttachment.UseViewmodelGrip on the sibling WeaponAttachment under this node). Left
	// visible, these three would double up with whatever WeaponAttachment attaches, and the main
	// body mesh also carries a real scale defect baked into the source FBX (see the README) that
	// would otherwise render it room-sized.
	private static readonly string[] EmbeddedGunMeshNames =
		{ "MP5_1_Low.010", "MP5_1_Low.011", "MP5_1_Low.013" };

	private AnimationPlayer? _anim;
	private bool _isLocal;

	/// <summary>Small nudge on top of the baked placement (see the class doc comment), for live
	/// tuning: run the game, select this node in the Scene dock's "Remote" tab, and drag these two
	/// in the Inspector - changes apply immediately. Once you land on values you like, fold them
	/// into fps_mp5_arms.glb's own baked root transform (or just leave them set here) rather than
	/// carrying a permanent offset. Position is metres in FirstPersonCamera's frame; rotation is a
	/// simple add on top of the baked orientation, which is exact for small nudges and is all this
	/// is for - see WeaponAttachment's baseline+offset composition if a large rotation ever needs
	/// composing here instead.</summary>
	[Export] public Vector3 ExtraPositionMeters { get; set; } = Vector3.Zero;
	[Export] public Vector3 ExtraRotationDegrees { get; set; } = Vector3.Zero;

	public override void _Ready()
	{
		_isLocal = IsMultiplayerAuthority();
		Visible = _isLocal;
		if (!_isLocal) return;

		Position += ExtraPositionMeters;
		RotationDegrees += ExtraRotationDegrees;

		_anim = FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		if (_anim == null)
		{
			GD.PushError("FirstPersonMp5Viewmodel could not find fps_mp5_arms.glb's AnimationPlayer.");
			return;
		}
		_anim.AnimationFinished += OnAnimationFinished;

		foreach (string name in EmbeddedGunMeshNames)
			if (FindChild(name, true, false) is VisualInstance3D mesh) mesh.Visible = false;

		// Manny's own body has no local/remote split of its own today - FirstPersonArms.cs would do
		// exactly this, but it is not instanced anywhere in Player.tscn and refers to mesh names
		// ("Armature_Mesh"/"Armature_Mesh_ArmsOnly") manny.glb's single "Manny" mesh does not have,
		// so it would throw if it ever ran. Doing it here instead, since Manny's body is exactly
		// what would otherwise double up with this viewmodel for the local player. Remote peers are
		// unaffected - this whole block only runs where _isLocal is true (see above).
		if (GetNodeOrNull<Node3D>("../../../Armature")?.FindChild("Manny", true, false) is VisualInstance3D manny)
			manny.Visible = false;

		PlayIdle();
	}

	/// <summary>Resting pose. Called once at equip time after mp5_equip finishes, and is otherwise
	/// wherever this sits between shots/reloads.</summary>
	public void PlayIdle() => Play(IdleClip);

	/// <summary>One shot cycle. Safe to call again mid-cycle for a full-auto burst - Play() restarts
	/// an already-playing clip from the top rather than ignoring the call.</summary>
	public void PlayShoot() => Play(ShootClip);

	/// <summary>Full reload cycle (see MP5_VIEWMODEL_README.md for its length - it is not a quick
	/// clip). WeaponSwitcher already gates this behind its own reload timer; this just animates it.</summary>
	public void PlayReload() => Play(ReloadClip);

	/// <summary>Weapon-raise, played on every switch. Ends exactly at mp5_idle's rest pose, so the
	/// automatic hand-back to idle in OnAnimationFinished is seamless.</summary>
	public void PlayEquip() => Play(EquipClip);

	private void Play(string clip)
	{
		if (!_isLocal || _anim == null || !_anim.HasAnimation(clip)) return;
		_anim.Play(clip, BlendSeconds);
	}

	/// <summary>Shoot/reload/equip are one-shot cycles with nothing queued after them - hand back to
	/// idle automatically rather than freezing on the last frame (idle itself never fires this: it
	/// is a two-key static pose, but AnimationPlayer still emits AnimationFinished for it, so it is
	/// deliberately left out below to avoid re-triggering idle from idle every 0.04 s).</summary>
	private void OnAnimationFinished(StringName animName)
	{
		string name = animName.ToString();
		if (name == ShootClip || name == ReloadClip || name == EquipClip) PlayIdle();
	}
}
