using Godot;

/// <summary>
/// Attaches the active weapon to the visible player rig (the single "Manny" body mesh +
/// "Armature" skeleton from manny.glb) and owns everything that lives on the weapon itself:
/// the model transform, the muzzle flash, and the fire/reload audio players.
///
/// Nothing in here is authority-gated. Every peer's copy of a player runs it, because every peer
/// needs to see and hear that player's weapon - WeaponSwitcher decides WHEN to fire, this decides
/// what firing looks and sounds like.
/// </summary>
public partial class WeaponAttachment : Node3D
{
	// Godot's glTF importer sanitizes bone names on import and does not preserve
	// "mixamorig:RightHand" verbatim. Matching by substring survives whatever the sanitized form
	// turns out to be; this is already confirmed working in the runtime logs.
	private const string HandBoneNameFragment = "righthand";
	private const float MuzzleFlashSeconds = 0.045f;

	private AudioStreamPlayer3D? _fireAudio;
	private AudioStreamPlayer3D? _reloadAudio;
	private AudioStreamPlayer3D? _emptyAudio;
	private Node3D? _muzzle;
	private MeshInstance3D? _flashMesh;
	private OmniLight3D? _flashLight;
	private float _flashRemaining;
	private WeaponData? _weapon;
	private readonly RandomNumberGenerator _rng = new();

	/// <summary>World-space muzzle point, or null for melee / a weapon with no configured muzzle.
	/// Impact and tracer effects should originate here rather than at the camera.</summary>
	public Node3D? Muzzle => _muzzle;

	public override void _Ready()
	{
		_rng.Randomize();
		SetProcess(false);
		// The initial equip is WeaponSwitcher's job now - it knows the loadout and the replicated
		// weapon index. Equipping the registry's "active" weapon here as well just built the model
		// twice on every spawn.
	}

	public override void _Process(double delta)
	{
		if (_flashRemaining <= 0.0f) return;
		_flashRemaining -= (float)delta;
		if (_flashRemaining > 0.0f) return;
		SetFlashVisible(false);
		SetProcess(false);
	}

	/// <summary>Single attachment path used for the initial equip and for hot switching.</summary>
	public void Equip(WeaponData? weapon)
	{
		var oldAttachment = GetParent().FindChild("WeaponBoneAttachment", true, false);
		if (oldAttachment != null)
		{
			// Remove immediately before creating the replacement. QueueFree alone leaves a
			// same-named BoneAttachment in the tree until frame end and can make a rapid switch
			// resolve the outgoing attachment instead of the new hand attachment.
			oldAttachment.GetParent()?.RemoveChild(oldAttachment);
			oldAttachment.QueueFree();
		}
		_fireAudio = null;
		_reloadAudio = null;
		_emptyAudio = null;
		_muzzle = null;
		_flashMesh = null;
		_flashLight = null;
		_flashRemaining = 0.0f;
		SetProcess(false);
		_weapon = weapon;
		if (weapon == null)
		{
			GD.PushError("Weapon attachment was handed a null weapon.");
			return;
		}

		// There is one shared "Manny" body mesh (no separate remote/local view-model split),
		// skinned to the skeleton manny.glb imports. Search by type rather than by a hardcoded
		// node name - a hardcoded name is exactly what broke weapon attachment twice already.
		var skeleton = (GetParent() as Skeleton3D) ?? FindSkeleton3D(GetParent());
		int handBoneIndex = skeleton == null ? -1 : FindBoneContaining(skeleton, HandBoneNameFragment);
		if (skeleton == null || handBoneIndex < 0)
		{
			string available = skeleton == null ? "(no Skeleton3D found)" : DumpBoneNames(skeleton);
			GD.PushError($"Weapon attachment could not find a bone containing '{HandBoneNameFragment}'. Actual bones: {available}");
			return;
		}

		var attachment = new BoneAttachment3D
		{
			Name = "WeaponBoneAttachment",
			BoneName = skeleton.GetBoneName(handBoneIndex),
		};
		skeleton.AddChild(attachment);

		// manny.glb's skeleton is authored in Mixamo centimetres and brought back to metres by a
		// 0.01 scale on the glTF root node. Everything parented under a bone inherits that 0.01,
		// so a weapon authored in metres renders at 1/100 size. Undo the skeleton's scale rather
		// than hardcoding 100, so this survives a re-export at a different unit scale.
		Vector3 unitFix = InverseScaleOf(skeleton);

		// The grip pivot carries the per-weapon offset/rotation and the unit correction. The model
		// hangs off it, so the muzzle marker can be a sibling in the same frame and both move
		// together when the grip values are retuned.
		var grip = new Node3D { Name = "Grip" };
		attachment.AddChild(grip);
		grip.Position = weapon.GripPosition * unitFix;
		grip.RotationDegrees = weapon.GripRotationDegrees;
		grip.Scale = unitFix;

		AddModel(grip, weapon);
		AddMuzzle(grip, weapon);
		AddAudioPlayers(grip, weapon);
	}

	/// <summary>Fire effects: one shot of audio plus a muzzle flash. Called on every peer.</summary>
	public void PlayFire()
	{
		if (_fireAudio != null)
		{
			RollFireVariant();
			_fireAudio.PitchScale = _rng.RandfRange(0.94f, 1.06f);
			_fireAudio.Play();
		}
		if (_flashMesh == null && _flashLight == null) return;
		SetFlashVisible(true);
		_flashRemaining = MuzzleFlashSeconds;
		SetProcess(true);
	}

	public void PlayReload()
	{
		if (_reloadAudio == null) return;
		_reloadAudio.PitchScale = _rng.RandfRange(0.97f, 1.03f);
		_reloadAudio.Play();
	}

	/// <summary>Dry-fire click. Falls back to silence rather than reusing the fire sound, which
	/// would read as a real shot to anyone nearby.</summary>
	public void PlayEmpty() => _emptyAudio?.Play();

	/// <summary>Spawns an impact one-shot at a world point. Parented to the map rather than to the
	/// weapon so it does not follow the barrel around while it plays.</summary>
	public void PlayImpact(Vector3 worldPosition)
	{
		if (_weapon == null || _weapon.ImpactSounds.Length == 0) return;
		var stream = LoadStream(_weapon.ImpactSounds[_rng.RandiRange(0, _weapon.ImpactSounds.Length - 1)]);
		if (stream == null) return;

		var player = new AudioStreamPlayer3D
		{
			Stream = stream,
			UnitSize = 6.0f,
			PitchScale = _rng.RandfRange(0.9f, 1.1f),
		};
		// One-shots are added to the current scene root: a node parented under the player would be
		// dragged along by movement, and one parented under the weapon dies on the next switch.
		Node host = GetTree().CurrentScene ?? (Node)this;
		host.AddChild(player);
		player.GlobalPosition = worldPosition;
		player.Finished += player.QueueFree;
		player.Play();
	}

	// -------------------------------------------------------------------------------------------

	private void AddModel(Node3D grip, WeaponData weapon)
	{
		if (string.IsNullOrEmpty(weapon.ModelScenePath))
		{
			// A weapon with no art is still a usable weapon - it fires, reloads, damages and makes
			// noise. Warn so it is visible, but do not fail the equip.
			GD.PushWarning($"{weapon.WeaponName} has no model assigned; equipping it without one.");
			return;
		}

		var model = ResourceLoader.Load<PackedScene>(weapon.ModelScenePath)?.Instantiate<Node3D>();
		if (model == null)
		{
			GD.PushError($"Weapon attachment could not load {weapon.ModelScenePath}.");
			return;
		}
		model.Name = "EquippedWeapon";
		model.Scale = Vector3.One * (weapon.ModelScale <= 0.0f ? 1.0f : weapon.ModelScale);
		grip.AddChild(model);
	}

	private void AddMuzzle(Node3D grip, WeaponData weapon)
	{
		if (weapon.MuzzleDistance <= 0.0f) return;

		// +Y in the grip's frame is the line of fire (see WeaponData's notes on the hand bone).
		_muzzle = new Node3D { Name = "Muzzle", Position = new Vector3(0.0f, weapon.MuzzleDistance, 0.0f) };
		grip.AddChild(_muzzle);

		var flashMaterial = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			BlendMode = BaseMaterial3D.BlendModeEnum.Add,
			AlbedoColor = new Color(1.0f, 0.78f, 0.35f),
			BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
			DisableReceiveShadows = true,
		};
		_flashMesh = new MeshInstance3D
		{
			Name = "Flash",
			Mesh = new QuadMesh { Size = new Vector2(0.28f, 0.28f) },
			MaterialOverride = flashMaterial,
			Visible = false,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		_muzzle.AddChild(_flashMesh);

		// A real light sells the flash far better than a billboard alone, and one short-lived omni
		// is affordable even on the HD 520 - but only while it is actually on, hence Visible.
		_flashLight = new OmniLight3D
		{
			Name = "FlashLight",
			LightColor = new Color(1.0f, 0.82f, 0.45f),
			LightEnergy = 2.4f,
			OmniRange = 4.0f,
			ShadowEnabled = false,
			Visible = false,
		};
		_muzzle.AddChild(_flashLight);
	}

	private void SetFlashVisible(bool visible)
	{
		if (_flashMesh != null) _flashMesh.Visible = visible;
		if (_flashLight != null) _flashLight.Visible = visible;
		if (!visible || _flashMesh == null) return;
		// Re-roll the flash each shot so a full-auto burst does not look like one frozen sprite.
		_flashMesh.RotationDegrees = new Vector3(0.0f, 0.0f, _rng.RandfRange(0.0f, 360.0f));
		_flashMesh.Scale = Vector3.One * _rng.RandfRange(0.8f, 1.25f);
	}

	private void AddAudioPlayers(Node3D grip, WeaponData weapon)
	{
		Node host = _muzzle ?? (Node)grip;
		_fireAudio = AddAudioPlayer(host, "WeaponFireAudio", PickOne(weapon.FireSounds), weapon.FireAudioUnitSize);
		_reloadAudio = AddAudioPlayer(grip, "WeaponReloadAudio", PickOne(weapon.ReloadSounds), 8.0f);
		_emptyAudio = AddAudioPlayer(grip, "WeaponEmptyAudio", weapon.EmptySound, 5.0f);

		if (weapon.FireSounds.Length > 1)
		{
			// One player, many clips: the stream is swapped per shot in PlayFire's caller path via
			// _fireVariants so a burst does not repeat the same waveform.
			_fireVariants = new AudioStream?[weapon.FireSounds.Length];
			for (int i = 0; i < weapon.FireSounds.Length; i++)
				_fireVariants[i] = LoadStream(weapon.FireSounds[i]);
		}
		else
		{
			_fireVariants = null;
		}

		if (weapon.FireSounds.Length == 0 && weapon.FireMode != WeaponData.FireModeType.Melee)
			GD.PushWarning($"{weapon.WeaponName} has no fire sounds assigned.");
	}

	private AudioStream?[]? _fireVariants;

	/// <summary>Swaps in a random fire clip before the shot plays. Kept separate from PlayFire so
	/// the variant list can be absent without branching at the call site.</summary>
	private void RollFireVariant()
	{
		if (_fireVariants == null || _fireAudio == null) return;
		var pick = _fireVariants[_rng.RandiRange(0, _fireVariants.Length - 1)];
		if (pick != null) _fireAudio.Stream = pick;
	}

	private static string PickOne(string[] paths) => paths.Length == 0 ? string.Empty : paths[0];

	private static AudioStreamPlayer3D? AddAudioPlayer(Node parent, string name, string path, float unitSize)
	{
		if (string.IsNullOrEmpty(path)) return null;
		var stream = LoadStream(path);
		if (stream == null) { GD.PushError($"Missing weapon audio: {path}"); return null; }
		var player = new AudioStreamPlayer3D { Name = name, Stream = stream, UnitSize = unitSize };
		parent.AddChild(player);
		return player;
	}

	private static AudioStream? LoadStream(string path) =>
		string.IsNullOrEmpty(path) ? null : ResourceLoader.Load<AudioStream>(path);

	/// <summary>Reciprocal of a node's global scale, so a child can cancel it out. Falls back to no
	/// correction if the scale is degenerate rather than dividing by zero.</summary>
	private static Vector3 InverseScaleOf(Node3D node)
	{
		Vector3 scale = node.GlobalTransform.Basis.Scale;
		if (Mathf.IsZeroApprox(scale.X) || Mathf.IsZeroApprox(scale.Y) || Mathf.IsZeroApprox(scale.Z))
			return Vector3.One;
		return new Vector3(1.0f / scale.X, 1.0f / scale.Y, 1.0f / scale.Z);
	}

	private static Skeleton3D? FindSkeleton3D(Node root)
	{
		if (root is Skeleton3D skeleton) return skeleton;
		foreach (Node child in root.GetChildren())
		{
			var found = FindSkeleton3D(child);
			if (found != null) return found;
		}
		return null;
	}

	private static int FindBoneContaining(Skeleton3D skeleton, string fragmentLower)
	{
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
			if (skeleton.GetBoneName(i).ToLowerInvariant().Contains(fragmentLower))
				return i;
		return -1;
	}

	private static string DumpBoneNames(Skeleton3D skeleton)
	{
		var names = new string[skeleton.GetBoneCount()];
		for (int i = 0; i < names.Length; i++) names[i] = skeleton.GetBoneName(i);
		return string.Join(", ", names);
	}
}
