using Godot;

/// <summary>Attaches the active registry weapon to the shared visible player rig (the single
/// "Manny" body mesh + "Armature" skeleton from manny.glb - there is no separate arms-only
/// view-model anymore).</summary>
public partial class WeaponAttachment : Node3D
{
	private const string RegistryPath = "res://scripts/data/WeaponRegistry.tres";
	// Godot's glTF importer sanitizes bone names on import (documented engine behavior - it does
	// not preserve "mixamorig:RightHand" verbatim, and exactly how it mangles it isn't something
	// I can verify without running the importer myself). Matching by substring instead of exact
	// string survives whatever the actual sanitized form turns out to be.
	private const string HandBoneNameFragment = "righthand";
	private AudioStreamPlayer3D? _fireAudio;
	private AudioStreamPlayer3D? _reloadAudio;

	public override void _Ready()
	{
		Equip(ResourceLoader.Load<WeaponRegistry>(RegistryPath)?.GetActiveWeapon());
	}

	/// <summary>Single attachment path used for initial equip and hot switching.</summary>
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
		if (weapon == null || string.IsNullOrEmpty(weapon.ModelScenePath))
		{
			GD.PushError("Weapon attachment could not resolve an active weapon from WeaponRegistry.");
			return;
		}

		// There's one shared "Manny" body mesh now (no separate remote/local view-model split),
		// skinned to the "Armature" Skeleton3D that manny.glb imports as its scene root. Search
		// by type rather than a hardcoded node name - a hardcoded name is exactly what broke
		// weapon attachment twice already on this project.
		var skeleton = (GetParent() as Skeleton3D) ?? FindSkeleton3D(GetParent());
		int handBoneIndex = skeleton == null ? -1 : FindBoneContaining(skeleton, HandBoneNameFragment);
		if (skeleton == null || handBoneIndex < 0)
		{
			string available = skeleton == null ? "(no Skeleton3D found)" : DumpBoneNames(skeleton);
			GD.PushError($"Weapon attachment could not find a bone containing '{HandBoneNameFragment}'. Actual bones: {available}");
			return;
		}
		string handBoneName = skeleton!.GetBoneName(handBoneIndex);

		var attachment = new BoneAttachment3D { Name = "WeaponBoneAttachment", BoneName = handBoneName };
		skeleton.AddChild(attachment);

		var weaponInstance = ResourceLoader.Load<PackedScene>(weapon.ModelScenePath)?.Instantiate<Node3D>();
		if (weaponInstance == null)
		{
			GD.PushError($"Weapon attachment could not load {weapon.ModelScenePath}.");
			attachment.QueueFree();
			return;
		}

		weaponInstance.Name = "EquippedWeapon";
		// The old rig's Right_Hand_Attach bone had a purpose-built grip offset; the actual hand
		// bone here doesn't. Zero is a starting point, not a verified value - expect to nudge
		// Position/Rotation here once you can actually see the grip in-editor.
		weaponInstance.Position = Vector3.Zero;
		weaponInstance.Rotation = Vector3.Zero;
		// manny.glb's skeleton is authored in Mixamo centimetres and brought back to metres by a
		// 0.01 scale on the glTF root node. Everything parented under a bone inherits that 0.01,
		// so a weapon model authored in metres renders at 1/100 size - i.e. invisible. Undo the
		// skeleton's scale on the weapon instead of hardcoding 100, so this keeps working if the
		// character is ever re-exported at a different unit scale.
		weaponInstance.Scale = InverseScaleOf(skeleton!);
		attachment.AddChild(weaponInstance);
		AddAudioPlayers(attachment, weapon.WeaponName);
	}

	public void PlayFire() => _fireAudio?.Play();
	public void PlayReload() => _reloadAudio?.Play();

	/// <summary>Reciprocal of a node's global scale, so a child can cancel it out. Falls back to
	/// no correction if the scale is degenerate rather than dividing by zero.</summary>
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

	private void AddAudioPlayers(Node attachment, string weaponName)
	{
		string firePath = weaponName switch
		{
			"AK-47" => "res://audio/Clips/Weapons/Rifle/Shoot/Rifle_Shoot-001.wav",
			"Shotgun" => "res://audio/Clips/Weapons/Shotgun/Shoot/Shotgun_Shoot-001.wav",
			"Glock 19" => "res://audio/Clips/Weapons/Pistol/Shoot/Pistol_Shoot-001.wav",
			_ => string.Empty,
		};
		string reloadPath = weaponName switch
		{
			"AK-47" => "res://audio/Clips/Weapons/Rifle/Reload/rifle_reload.wav",
			"Shotgun" => "res://audio/Clips/Weapons/Shotgun/Reload/Shotgun_reload 1.wav",
			"Glock 19" => "res://audio/Clips/Weapons/Pistol/Reload/Pistol_Reload.wav",
			_ => string.Empty,
		};
		_fireAudio = AddAudioPlayer(attachment, "WeaponFireAudio", firePath);
		_reloadAudio = AddAudioPlayer(attachment, "WeaponReloadAudio", reloadPath);
		if (weaponName == "Karambit") GD.Print("Karambit has no migrated swing/hit or reload audio; no audio player was assigned.");
	}

	private static AudioStreamPlayer3D? AddAudioPlayer(Node parent, string name, string path)
	{
		if (string.IsNullOrEmpty(path)) return null;
		var stream = ResourceLoader.Load<AudioStream>(path);
		if (stream == null) { GD.PushError($"Missing weapon audio: {path}"); return null; }
		var player = new AudioStreamPlayer3D { Name = name, Stream = stream, UnitSize = 8.0f };
		parent.AddChild(player);
		return player;
	}
}
