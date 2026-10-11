using Godot;

/// <summary>
/// Attaches a weapon to the shared character skeleton's hand bone and owns the model transform,
/// muzzle flash, and fire/reload audio players for the world-model weapon.
///
/// Nothing in here is authority-gated by default. Every peer's copy of a player runs it, because
/// every peer needs to see and hear that player's weapon - WeaponSwitcher decides WHEN to fire,
/// this decides what firing looks and sounds like.
/// </summary>
public partial class WeaponAttachment : Node3D
{
	// Godot's glTF importer sanitizes imported bone names, so match the stable hand-name fragment.
	[Export] public string HandBoneFragment { get; set; } = "righthand";

	/// <summary>Skip audio nodes when this attachment is used only to preview a weapon in the UI.</summary>
	[Export] public bool SkipAudio { get; set; }

	private const float MuzzleFlashSeconds = 0.045f;

	// Shared across every weapon rather than a per-weapon WeaponData field: only one recording was
	// supplied, and "a body got hit" reads the same whether it was a bullet or a melee strike.
	private const string FleshImpactSound = "res://audio/Clips/Weapons/Impacts/Feedback/flesh_hit.wav";

	// Bullet-on-surface sounds. Which one plays comes from SurfaceClassifier (scripts/combat/
	// SurfaceType.cs); ricochet and flyby are layered/triggered by WeaponSwitcher. The trims level
	// the clips against each other - as supplied they span ~24 dB of peak level (the wall hit is
	// quiet, the metal hits are hot), and one 3D player gain per clip is the cheapest fix that
	// leaves the audio files themselves untouched.
	private const string SurfaceDirectory = "res://audio/Clips/Weapons/Impacts/Surfaces";
	private const string MetalHeavySound = SurfaceDirectory + "/BulletMetalHeavy.mp3";
	private const string MetalThickSound = SurfaceDirectory + "/BulletMetalThick.mp3";
	private const string WoodSound = SurfaceDirectory + "/BulletWood.mp3";
	private const string GlassSound = SurfaceDirectory + "/BulletGlassHit.wav";
	private const string WallSound = SurfaceDirectory + "/BulletWallHit.wav";
	private const string RicochetSound = SurfaceDirectory + "/BulletRicochet.wav";
	private const string FlyBySound = SurfaceDirectory + "/bullet_flyby.wav";
	private const string RocketFlyBySound = "res://audio/Clips/Weapons/RPG/Extras/rpg_doppler_effect.wav";

	private const float MetalHeavyTrimDb = -6.0f;
	private const float MetalThickTrimDb = -7.0f;
	private const float WoodTrimDb = -6.0f;
	private const float GlassTrimDb = -2.0f;
	private const float WallTrimDb = 8.0f;
	private const float RicochetTrimDb = -2.0f;
	private const float FlyByTrimDb = 9.0f;
	private const float RocketFlyByTrimDb = -3.0f;

	/// <summary>A metal hit from a weapon whose BodyDamage is at least this plays the heavy clip
	/// (AK-47, M4, Desert Eagle); lighter weapons (SMGs, Glock, shotgun pellets) play the thick one.</summary>
	private const float HeavyMetalMinBodyDamage = 30.0f;

	private const float SurfaceImpactUnitSize = 8.0f;
	private const float SurfaceImpactMaxDistance = 100.0f;

	/// <summary>Voices per fire player. A full-auto burst restarts the same one-shot every few
	/// milliseconds; with polyphony 1 each shot chopped the previous shot's tail off, so a long
	/// burst sounded like a stutter instead of overlapping reports.</summary>
	private const int FirePolyphony = 8;

	private AudioStreamPlayer3D? _fireAudio;
	private AudioStreamPlayer3D? _reloadAudio;
	private AudioStreamPlayer3D? _emptyAudio;
	private Node3D? _muzzle;
	private MeshInstance3D? _flashMesh;
	private OmniLight3D? _flashLight;
	private float _flashRemaining;
	private Vector3 _gripUnitFix = Vector3.One;
	private WeaponData? _weapon;
	private Node3D? _grip;
	private readonly RandomNumberGenerator _rng = new();

	/// <summary>World-space muzzle point, or null for melee / a weapon with no configured muzzle.
	/// Impact and tracer effects should originate here rather than at the camera.</summary>
	public Node3D? Muzzle => _muzzle;

	/// <summary>The pivot the weapon model hangs off - position/rotation/scale here are exactly
	/// WeaponData's GripPosition/GripRotationDegrees/ModelScale, already converted into the hand
	/// bone's local frame. WeaponGripTuner writes to this node directly for live preview, then
	/// reads the equivalent WeaponData-space numbers back out via GripPositionFromNode /
	/// GripRotationFromNode for the user to paste into the .tres.</summary>
	public Node3D? CurrentGrip => _grip;

	/// <summary>The weapon currently attached, for anything that needs to read or report its data
	/// (the HUD, the grip tuner) without duplicating WeaponSwitcher's own bookkeeping.</summary>
	public WeaponData? CurrentWeapon => _weapon;

	/// <summary>Converts the Grip node's live Position back into WeaponData.GripPosition units -
	/// undoes the skeleton-scale correction applied when the grip was built, so the value the tuner
	/// reports is the one that belongs in the .tres, not the scaled one actually on the node.</summary>
	public Vector3 GripPositionForData =>
		_grip == null ? Vector3.Zero : DivideComponents(_grip.Position, _gripUnitFix);

	private static Vector3 DivideComponents(Vector3 a, Vector3 b) => new(
		Mathf.IsZeroApprox(b.X) ? a.X : a.X / b.X,
		Mathf.IsZeroApprox(b.Y) ? a.Y : a.Y / b.Y,
		Mathf.IsZeroApprox(b.Z) ? a.Z : a.Z / b.Z);

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

		// There is one shared character body mesh (no separate remote/local view-model split),
		// skinned to the imported character skeleton. Search by type rather than by a hardcoded
		// node name - a hardcoded name is exactly what broke weapon attachment twice already.
		var skeleton = (GetParent() as Skeleton3D) ?? FindSkeleton3D(GetParent());
		int handBoneIndex = skeleton == null ? -1 : FindBoneContaining(skeleton, HandBoneFragment.ToLowerInvariant());
		if (skeleton == null || handBoneIndex < 0)
		{
			string available = skeleton == null ? "(no Skeleton3D found)" : DumpBoneNames(skeleton);
			GD.PushError($"Weapon attachment could not find a bone containing '{HandBoneFragment}'. Actual bones: {available}");
			return;
		}

		var attachment = new BoneAttachment3D
		{
			Name = "WeaponBoneAttachment",
			BoneName = skeleton.GetBoneName(handBoneIndex),
		};
		skeleton.AddChild(attachment);

		// Convert weapon offsets and scale into the skeleton's authored unit space. Derive it from the live skeleton rather than hardcoding a
		// model scale, so this survives a re-export at a different unit scale.
		Vector3 unitFix = InverseScaleOf(skeleton);

		// The grip pivot carries the per-weapon offset/rotation and the unit correction. The model
		// hangs off it, so the muzzle marker can be a sibling in the same frame and both move
		// together when the grip values are retuned.
		var grip = new Node3D { Name = "Grip" };
		attachment.AddChild(grip);
		grip.Position = weapon.GripPosition * unitFix;
		grip.RotationDegrees = weapon.GripRotationDegrees;
		grip.Scale = unitFix;
		_grip = grip;
		_gripUnitFix = unitFix;

		AddModel(grip, weapon);
		AddMuzzle(grip, weapon);
		if (!SkipAudio) AddAudioPlayers(grip, weapon);
	}

	/// <summary>Fire effects: one shot of audio plus a muzzle flash. Called on every peer.</summary>
	public void PlayFire()
	{
		if (_fireAudio != null)
		{
			RollFireVariant();
			_fireAudio.PitchScale = _rng.RandfRange(0.94f, 1.06f);
			// Through SoundManager so the shot gets real distance falloff, muzzle direction, wall
			// occlusion and the crack-then-boom delay; straight Play() only if it is not there.
			if (SoundManager.Instance != null && _weapon != null)
				SoundManager.Instance.PlayWeaponFire(_fireAudio, _weapon, _muzzle?.GlobalPosition ?? GlobalPosition);
			else
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
		// Shell-by-shell weapons (the shotgun) raise this once per shell, every ReloadTime seconds -
		// far shorter than the clip. Restarting it each time would cut it off half a second in, over
		// and over, so let a pass that is already playing finish instead.
		if (_weapon != null && _weapon.ShellByShellReload && _reloadAudio.Playing) return;
		_reloadAudio.PitchScale = _rng.RandfRange(0.97f, 1.03f);
		_reloadAudio.Play();
	}

	/// <summary>Cuts the reload sound off - a shotgun reload interrupted by firing.</summary>
	public void StopReload()
	{
		if (_reloadAudio != null && _reloadAudio.Playing) _reloadAudio.Stop();
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
		// A weapon that opts out of surface variation is playing its explosion here,
		// which carries as far as the launcher's own report; everything else is a short-range thud.
		if (!_weapon.ImpactUsesSurfaceVariation)
			SpawnOneShotAt(stream, worldPosition, _weapon.FireAudioUnitSize, _weapon.FireMaxDistanceMeters, 0.0f);
		else
			SpawnOneShotAt(stream, worldPosition, 6.0f, 60.0f, 0.0f);
	}

	/// <summary>A bullet hitting world geometry: the sound of what it hit (metal / wood / glass /
	/// plain wall), plus a ricochet whine on top when the shooter's device rolled one. Called on
	/// every peer from WeaponSwitcher's broadcast, with the surface and ricochet already decided
	/// on the shooter's side so everyone hears the same thing.</summary>
	public void PlayGeometryImpact(Vector3 worldPosition, SurfaceType surface, bool ricochet)
	{
		if (_weapon == null) return;
		// An explosive weapon's hit is its explosion, whatever it hit.
		if (!_weapon.ImpactUsesSurfaceVariation) { PlayImpact(worldPosition); return; }

		(string path, float trimDb) = SurfaceSound(surface);
		AudioStream? stream = LoadStream(path);
		// New clips missing: fall back to the weapon's own impact clips rather than hitting silence.
		if (stream == null) { PlayImpact(worldPosition); return; }

		SpawnOneShotAt(stream, worldPosition, SurfaceImpactUnitSize, SurfaceImpactMaxDistance, trimDb);
		if (ricochet)
			SpawnOneShotAt(LoadStream(RicochetSound), worldPosition, SurfaceImpactUnitSize,
				SurfaceImpactMaxDistance, RicochetTrimDb);
	}

	private (string Path, float TrimDb) SurfaceSound(SurfaceType surface)
	{
		switch (surface)
		{
			case SurfaceType.Metal:
				return _weapon != null && _weapon.BodyDamage >= HeavyMetalMinBodyDamage
					? (MetalHeavySound, MetalHeavyTrimDb)
					: (MetalThickSound, MetalThickTrimDb);
			case SurfaceType.Wood:
				return (WoodSound, WoodTrimDb);
			case SurfaceType.Glass:
				return (GlassSound, GlassTrimDb);
			default:
				return (WallSound, WallTrimDb);
		}
	}

	/// <summary>The whip-crack of a shot passing close by. WeaponSwitcher works out where along the
	/// shot's path the local listener was closest and calls this there. A rocket gets its own
	/// doppler whoosh instead of the bullet whiz, and is audible from much further off.</summary>
	public void PlayFlyBy(Vector3 worldPosition, bool rocket)
	{
		if (rocket)
			SpawnOneShotAt(LoadStream(RocketFlyBySound), worldPosition, 12.0f, 150.0f, RocketFlyByTrimDb);
		else
			SpawnOneShotAt(LoadStream(FlyBySound), worldPosition, 4.0f, 40.0f, FlyByTrimDb);
	}

	/// <summary>Plays the shared "hit a person" sound at the impact point - bullet or blade, any
	/// weapon. Called from WeaponSwitcher over a broadcast RPC so every peer hears it, not just the
	/// shooter.</summary>
	public void PlayFleshImpact(Vector3 worldPosition)
	{
		// A rocket hit is an explosion, not a body-blow thud.
		if (_weapon != null && !_weapon.ImpactUsesSurfaceVariation) { PlayImpact(worldPosition); return; }
		SpawnOneShotAt(LoadStream(FleshImpactSound), worldPosition, 9.0f, 60.0f, 0.0f);
	}

	private void SpawnOneShotAt(AudioStream? stream, Vector3 worldPosition, float unitSize,
		float maxDistance, float volumeDb)
	{
		if (stream == null) return;
		var player = new AudioStreamPlayer3D
		{
			Stream = stream,
			UnitSize = unitSize,
			MaxDistance = maxDistance,
			VolumeDb = volumeDb,
			PitchScale = _rng.RandfRange(0.9f, 1.1f),
			Bus = "Weapons",
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
		ApplyFallbackAlbedo(model, weapon.WeaponName);
		grip.AddChild(model);
	}

	/// <summary>Mirrors ViewModelBase's embedded-texture patch, but for the world-model mesh every
	/// peer sees (this attachment, not the local-only viewmodel). Some FBX imports (the AK-47's
	/// art/weapons/AssaultRifle/AK47_gun.fbx) carry a material with no albedo bound and no texture
	/// files sitting next to the source file, so the mesh otherwise renders flat grey/white for
	/// every player who isn't the shooter. Reassigns the real texture by weapon name instead.</summary>
	private static void ApplyFallbackAlbedo(Node3D model, string weaponName)
	{
		string path = weaponName switch
		{
			"AK-47" => "res://art/animations/player/combat/AK/textures/ak74m_AlbedoTransparency.png",
			_ => string.Empty
		};
		if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path)) return;
		var albedo = GD.Load<Texture2D>(path);
		foreach (Node child in model.GetChildren())
			ApplyFallbackAlbedoRecursive(child, albedo);
	}

	private static void ApplyFallbackAlbedoRecursive(Node node, Texture2D albedo)
	{
		if (node is MeshInstance3D mesh && mesh.Mesh != null)
		{
			for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
			{
				Material? active = mesh.MaterialOverride ?? mesh.GetSurfaceOverrideMaterial(surface) ?? mesh.Mesh.SurfaceGetMaterial(surface);
				if (active is BaseMaterial3D existing && existing.AlbedoTexture != null) continue;
				BaseMaterial3D copy = active is BaseMaterial3D source
					? (BaseMaterial3D)source.Duplicate(true)
					: new StandardMaterial3D();
				copy.AlbedoTexture = albedo;
				mesh.SetSurfaceOverrideMaterial(surface, copy);
			}
		}
		foreach (Node child in node.GetChildren())
			ApplyFallbackAlbedoRecursive(child, albedo);
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

		// "WeaponsQuiet" skips the sidechain that ducks footsteps under gunfire (see
		// default_bus_layout.tres) - it exists for quiet weapons that are not loud enough to mask
		// anything. Every firearm's fire/reload/click goes to "Weapons".
		string bus = weapon.FireCanDuckMovement ? "Weapons" : "WeaponsQuiet";
		if (_fireAudio != null)
		{
			_fireAudio.Bus = bus;
			_fireAudio.MaxPolyphony = FirePolyphony;
			// AudioStreamPlayer3D's directional cone points down its own local -Z, but the grip's
			// line of fire is +Y (see AddMuzzle). +90 degrees about X maps -Z onto +Y, so
			// FireEmissionAngleDegrees actually points down the barrel. Harmless with the cone off.
			_fireAudio.RotationDegrees = new Vector3(90.0f, 0.0f, 0.0f);
		}
		if (_reloadAudio != null) { _reloadAudio.Bus = bus; _reloadAudio.MaxDistance = 40.0f; }
		if (_emptyAudio != null) { _emptyAudio.Bus = bus; _emptyAudio.MaxDistance = 25.0f; }

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

		if (weapon.FireSounds.Length == 0)
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

	/// <summary>Exists-check first: a sound that is not in the project (the old flesh-hit, Glock
	/// fire and knife-swing placeholders were never part of the new audio pack) is a quiet null
	/// instead of an engine error on every shot.</summary>
	private static AudioStream? LoadStream(string path) =>
		!string.IsNullOrEmpty(path) && ResourceLoader.Exists(path) ? ResourceLoader.Load<AudioStream>(path) : null;

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
