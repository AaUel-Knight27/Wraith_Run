using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Runtime viewmodel regression harness. Run with the viewmodel_check scene.</summary>
public partial class ViewModelCheck : Node3D
{
	private static readonly string[] Weapons =
	{
		"AK-47", "M4", "P90", "Kriss Vector", "Glock 19", "Desert Eagle", "Shotgun", "Karambit", "Bazooka"
	};
	private Camera3D _camera = null!;
	private ViewModelBase? _active;
	private bool _failed;
	private int _phase, _lifePass = 1, _weaponIndex, _lifeWait, _clipIndex, _clipFrame, _clipFrameLimit;
	private ViewModelWeaponData? _data;
	private AnimationPlayer? _player;
	private Skeleton3D? _skeleton;
	private string[] _clipNames = Array.Empty<string>();
	private bool _weaponFail;
	private float _minExtent, _maxExtent;
	private float _idleExtent;
	private readonly HashSet<string> _reportedScaleFailures = new();
	private readonly HashSet<string> _reportedPlacementFailures = new();
	private readonly Dictionary<MeshInstance3D, Material?> _diagnosticArmMaterials = new();
	private readonly Dictionary<MeshInstance3D, (Skin? Skin, NodePath Skeleton)> _diagnosticArmBindings = new();
	private int _shotgunSkinDiagnosticStage;

	public override void _Ready()
	{
		_camera = new Camera3D { Name = "CheckCamera", Current = true, Fov = 65, Near = 0.03f };
		AddChild(_camera);
		var world = new WorldEnvironment
		{
			Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.Color,
				BackgroundColor = new Color(0.12f, 0.14f, 0.17f),
				AmbientLightSource = Godot.Environment.AmbientSource.Color,
				AmbientLightColor = new Color(0.7f, 0.72f, 0.76f),
				AmbientLightEnergy = 0.8f
			}
		};
		AddChild(world);
		AddChild(new DirectionalLight3D
		{
			RotationDegrees = new Vector3(-25, -25, 0),
			LightEnergy = 1.2f,
			ShadowEnabled = false
		});
		AuditRemovedMannyAnimations();
		GD.Print("VIEWMODEL CHECK STEP1 mapping and 3x switch lifecycle");
		PrintMappingTable();
		_phase = 0;
	}

	public override void _Process(double delta)
	{
		if (_phase == 0) StepLifecycle();
		else if (_phase == 1) StepPrepareMeasurement();
		else if (_phase == 2) StepClip();
		else if (_phase == 3) { if (_lifeWait > 0) _lifeWait--; else StartMeasurement(); }
	}

	private void StepLifecycle()
	{
		if (_lifeWait > 0) { _lifeWait--; return; }
		ClearActive();
		string weapon = Weapons[_weaponIndex];
		ViewModelWeaponData? data = LoadData(weapon);
		if (data?.UseViewModel == true)
		{
			_active = new ViewModelBase { Name = $"{weapon.Replace(" ", string.Empty)}ViewModel" };
			_active.Configure(data);
			_camera.AddChild(_active);
		}
		_lifeWait = 1;
		int count = CountLiveViewModels();
		int expected = data?.UseViewModel == true ? 1 : 0;
		GD.Print($"VIEWMODEL SWITCH_TEST pass={_lifePass} weapon='{weapon}' live={count} expected={expected} {(count == expected ? "PASS" : "FAIL")}");
		if (count != expected) _failed = true;
		_weaponIndex++;
		if (_weaponIndex >= Weapons.Length)
		{
			_weaponIndex = 0;
			if (_lifePass < 3) { _lifePass++; _lifeWait = 2; }
			else { _phase = 1; _lifeWait = 1; }
		}
	}

	private void StepPrepareMeasurement()
	{
		if (_lifeWait > 0) { _lifeWait--; return; }
		ClearActive();
		string weapon = Weapons[_weaponIndex];
		_data = LoadData(weapon);
		if (_data == null) { Fail(weapon, "missing data"); NextWeapon(); return; }
		if (!_data.UseViewModel)
		{
			GD.Print($"VIEWMODEL RESULT weapon='{weapon}' rig=disabled clip=none arms=none gun=not_in_fps_feature SKIP");
			NextWeapon();
			return;
		}
		if (_data.RigScene == null) { Fail(weapon, "rig missing"); NextWeapon(); return; }
		_active = new ViewModelBase { Name = $"{weapon.Replace(" ", string.Empty)}ViewModel" };
		_active.Configure(_data);
		_camera.AddChild(_active);
		_lifeWait = 1;
		_phase = 3;
	}

	private void StartMeasurement()
	{
		string weapon = Weapons[_weaponIndex];
		_skeleton = _active?.Skeleton;
		_player = _active?.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		if (_active == null || _data == null || _skeleton == null || _player == null)
		{ Fail(weapon, "skeleton or AnimationPlayer missing"); NextWeapon(); return; }
		_weaponFail = CountLiveViewModels() != 1;
		CheckTextures(weapon, _active, _data, ref _weaponFail);
		var clips = new List<string>();
		foreach (string key in _data.ClipMap.Keys) if (_active.HasResolvedClip(key)) clips.Add(key);
		_clipNames = clips.ToArray();
		_clipIndex = 0;
		_phase = 2;
		StartClip();
		if (weapon == "Shotgun")
		{
			foreach (MeshInstance3D mesh in FindAll<MeshInstance3D>(_active))
				if (_data.ArmMeshNames.Contains(mesh.Name.ToString()))
				{
					_diagnosticArmMaterials[mesh] = mesh.MaterialOverride;
					_diagnosticArmBindings[mesh] = (mesh.Skin, mesh.Skeleton);
					mesh.MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Colors.Magenta, CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = true };
				}
			_shotgunSkinDiagnosticStage = 1;
		}
	}

	private void StartClip()
	{
		if (_active == null || _data == null || _clipIndex >= _clipNames.Length) { FinishWeapon(); return; }
		string clip = _clipNames[_clipIndex];
		_active.Play(clip);
		_clipFrame = 0;
		_clipFrameLimit = Math.Max(1, (int)Math.Ceiling(_active.GetClipLength(clip) * 60.0) + 1);
		_minExtent = float.PositiveInfinity; _maxExtent = 0;
		if (clip == "idle") _idleExtent = 0;
	}

	private void StepClip()
	{
		if (_active == null || _data == null || _skeleton == null || _player == null) return;
		string weapon = Weapons[_weaponIndex];
		string clip = _clipNames[_clipIndex];
		Aabb? arms = GetCameraBounds(_active, _data.ArmMeshNames);
		if (arms.HasValue)
		{
			float extent = MaxAxis(arms.Value.Size);
			_minExtent = Math.Min(_minExtent, extent); _maxExtent = Math.Max(_maxExtent, extent);
			if (clip == "idle") _idleExtent = Math.Max(_idleExtent, extent);
			else if (_idleExtent > 0 && Math.Abs(extent - _idleExtent) / _idleExtent > 0.10f) _weaponFail = true;
			CheckPlacement(weapon, clip, "arms", arms.Value, ref _weaponFail);
		}
		Aabb? gun = GetGunBounds(_active);
		if (gun.HasValue) CheckPlacement(weapon, clip, "gun", gun.Value, ref _weaponFail);
		CheckBoneScales(weapon, clip, _skeleton, ref _weaponFail);
		if (_shotgunSkinDiagnosticStage == 1 && _clipFrame == 2)
		{
			SaveScreenshot("shotgun_arms_debug_skinned.png");
			int magentaPixels = CountMagentaPixels();
			GD.Print($"VIEWMODEL PIXEL_CHECK weapon='Shotgun' diagnostic='magenta_with_original_skin' pixels={magentaPixels} {(magentaPixels >= 10 ? "PASS" : "FAIL")}");
			if (magentaPixels < 10)
			{
				GD.PrintErr("VIEWMODEL FAIL weapon='Shotgun' original skinned arm meshes produce no rendered pixels with an unshaded diagnostic material");
				_weaponFail = true;
			}
			foreach (MeshInstance3D mesh in _diagnosticArmBindings.Keys)
			{
				mesh.Skin = null;
				mesh.Skeleton = new NodePath("");
			}
			_shotgunSkinDiagnosticStage = 2;
		}
		if (_shotgunSkinDiagnosticStage == 2 && _clipFrame == 4)
		{
			SaveScreenshot("shotgun_arms_debug_unskinned.png");
			GD.Print($"VIEWMODEL PIXEL_CHECK weapon='Shotgun' diagnostic='magenta_without_skin' pixels={CountMagentaPixels()}");
			foreach (var pair in _diagnosticArmMaterials)
				if (GodotObject.IsInstanceValid(pair.Key))
				{
					pair.Key.MaterialOverride = pair.Value;
					if (_diagnosticArmBindings.TryGetValue(pair.Key, out var binding))
					{
						pair.Key.Skin = binding.Skin;
						pair.Key.Skeleton = binding.Skeleton;
					}
				}
			_diagnosticArmMaterials.Clear();
			_diagnosticArmBindings.Clear();
			_shotgunSkinDiagnosticStage = 0;
		}
		GD.Print($"VIEWMODEL FRAME weapon='{weapon}' clip='{clip}' frame={_clipFrame} arms={Format(arms)} gun={Format(gun)} distance={Distance(arms, gun):F4}m barrel_dot={GetBarrelDot(_active):F4} bone_scale_out_of_range={HasBadBoneScale(_skeleton)}");
		_clipFrame++;
		if (_clipFrame < _clipFrameLimit) return;
		if (!float.IsFinite(_minExtent)) _minExtent = _maxExtent = 0;
		GD.Print($"VIEWMODEL CLIP weapon='{weapon}' clip='{clip}' resolved='{(_active.ResolvedClips.TryGetValue(clip, out string? actual) ? actual : "none")}' frames={_clipFrame} arms_extent_range=({_minExtent:F4},{_maxExtent:F4})m");
		if (clip == "idle") SaveScreenshot($"{SafeName(weapon)}_idle.png");
		if (clip == "fire") SaveScreenshot($"{SafeName(weapon)}_fire.png");
		_clipIndex++;
		StartClip();
	}

	private void FinishWeapon()
	{
		string weapon = Weapons[_weaponIndex];
		Aabb? arms = _active != null && _data != null ? GetCameraBounds(_active, _data.ArmMeshNames) : null;
		Aabb? gun = _active != null ? GetGunBounds(_active) : null;
		float barrelDot = _active != null ? GetBarrelDot(_active) : 0;
		GD.Print($"VIEWMODEL METRICS weapon='{weapon}' rig='{_data?.RigScene?.ResourcePath}' attach='{_data?.AttachBoneName}' clip='{_player?.CurrentAnimation}' arms={Format(arms)} gun={Format(gun)} distance={Distance(arms, gun):F4}m barrel_dot={barrelDot:F4} textureOrPoseFail={_weaponFail}");
		if (arms == null || gun == null) _weaponFail = true;
		if (_weaponFail) Fail(weapon, "texture, pose, bone-scale or missing gun check failed");
		else GD.Print($"VIEWMODEL RESULT weapon='{weapon}' PASS");
		NextWeapon();
	}

	private void NextWeapon()
	{
		_weaponIndex++;
		ClearActive();
		if (_weaponIndex >= Weapons.Length)
		{
			_phase = 4;
			GD.Print(_failed ? "VIEWMODEL CHECK FAIL" : "VIEWMODEL CHECK PASS");
			GetTree().Quit(_failed ? 1 : 0);
		}
		else { _phase = 1; _lifeWait = 1; }
	}

	private void PrintMappingTable()
	{
		foreach (string weapon in Weapons)
		{
			ViewModelWeaponData? data = LoadData(weapon);
			string gun = data?.WeaponModelScene?.ResourcePath ?? (data?.UseViewModel == true ? "embedded FBX weapon" : "disabled until FPS model feature");
			GD.Print($"VIEWMODEL MAP weapon='{weapon}' data='{data?.ResourcePath ?? "missing"}' rig='{data?.RigScene?.ResourcePath ?? "none"}' bone='{data?.AttachBoneName ?? "none"}' gun='{gun}' needs_visual_review={data?.VisualReviewRequired ?? false}");
		}
	}

	private static ViewModelWeaponData? LoadData(string weapon)
	{
		string file = weapon switch
		{
			"AK-47" => "AK47", "Kriss Vector" => "KrissVector", "Glock 19" => "Glock19", "Desert Eagle" => "DesertEagle",
			_ => weapon.Replace(" ", string.Empty)
		};
		return ResourceLoader.Load<ViewModelWeaponData>($"res://data/viewmodels/{file}.tres");
	}

	private void ClearActive()
	{
		if (_active != null && GodotObject.IsInstanceValid(_active))
		{
			if (_active.FindChild("AnimationPlayer", true, false) is AnimationPlayer animationPlayer) animationPlayer.Stop();
			_active.Visible = false;
			_active.GetParent()?.RemoveChild(_active);
			_active.QueueFree();
		}
		_active = null;
	}

	private int CountLiveViewModels()
	{
		int count = 0;
		foreach (Node child in _camera.GetChildren()) if (child is ViewModelBase && !child.IsQueuedForDeletion()) count++;
		return count;
	}

	private void CheckTextures(string weapon, ViewModelBase viewModel, ViewModelWeaponData data, ref bool fail)
	{
		foreach (MeshInstance3D mesh in FindAll<MeshInstance3D>(viewModel))
		{
			if (!mesh.IsVisibleInTree()) continue;
			for (int surface = 0; surface < (mesh.Mesh?.GetSurfaceCount() ?? 0); surface++)
			{
				Material? material = mesh.MaterialOverride ?? mesh.GetSurfaceOverrideMaterial(surface) ?? mesh.Mesh!.SurfaceGetMaterial(surface);
				Texture2D? albedo = material is BaseMaterial3D baseMaterial ? baseMaterial.AlbedoTexture : null;
				bool missing = albedo == null;
				GD.Print($"VIEWMODEL MATERIAL weapon='{weapon}' mesh='{mesh.Name}' surface={surface} material='{material?.ResourcePath ?? material?.GetClass().ToString() ?? "none"}' albedo={(albedo == null ? "none" : albedo.ResourcePath)}");
				if (missing) { GD.PrintErr($"VIEWMODEL FAIL weapon='{weapon}' mesh='{mesh.Name}' surface={surface} has no albedo texture"); fail = true; }
			}
		}
	}

	private void CheckPlacement(string weapon, string clip, string part, Aabb box, ref bool fail)
	{
		Vector3 p = box.GetCenter();
		bool okay = p.X >= -0.1f && p.X <= 0.35f && p.Y >= -0.5f && p.Y <= -0.05f && p.Z >= -0.9f && p.Z <= -0.25f && box.End.Z <= -0.03f;
		if (!okay)
		{
			string key = $"{weapon}/{clip}/{part}";
			if (_reportedPlacementFailures.Add(key)) GD.PrintErr($"VIEWMODEL FAIL weapon='{weapon}' clip='{clip}' part='{part}' camera_aabb={box} center={p}");
			fail = true;
		}
	}

	private void CheckBoneScales(string weapon, string clip, Skeleton3D skeleton, ref bool fail)
	{
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
		{
			Vector3 s = skeleton.GetBonePoseScale(i);
			if (!float.IsFinite(s.X) || !float.IsFinite(s.Y) || !float.IsFinite(s.Z) || Math.Abs(s.X - 1) > 0.05 || Math.Abs(s.Y - 1) > 0.05 || Math.Abs(s.Z - 1) > 0.05)
			{
				string key = $"{weapon}/{clip}/{skeleton.GetBoneName(i)}";
				if (_reportedScaleFailures.Add(key)) GD.PrintErr($"VIEWMODEL FAIL weapon='{weapon}' clip='{clip}' bone='{skeleton.GetBoneName(i)}' scale={s}");
				fail = true;
			}
		}
	}

	private static bool HasBadBoneScale(Skeleton3D skeleton)
	{
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
		{
			Vector3 s = skeleton.GetBonePoseScale(i);
			if (!float.IsFinite(s.X) || !float.IsFinite(s.Y) || !float.IsFinite(s.Z) || Math.Abs(s.X - 1) > 0.05 || Math.Abs(s.Y - 1) > 0.05 || Math.Abs(s.Z - 1) > 0.05) return true;
		}
		return false;
	}

	private Aabb? GetCameraBounds(Node root, Godot.Collections.Array<string> names)
	{
		Aabb? bounds = null;
		foreach (MeshInstance3D mesh in FindAll<MeshInstance3D>(root))
		{
			if (!mesh.IsVisibleInTree() || !NamesContain(names, mesh.Name)) continue;
			Aabb local = mesh.GetAabb();
			Aabb cameraBox = TransformAabb(local, _camera.GlobalTransform.AffineInverse() * mesh.GlobalTransform);
			bounds = bounds.HasValue ? bounds.Value.Merge(cameraBox) : cameraBox;
		}
		return bounds;
	}

	private Aabb? GetGunBounds(Node root)
	{
		Node? gun = root.FindChild("WeaponModel", true, false);
		Aabb? bounds = null;
		IEnumerable<MeshInstance3D> meshes = gun != null ? FindAll<MeshInstance3D>(gun) : FindAll<MeshInstance3D>(root);
		foreach (MeshInstance3D mesh in meshes)
		{
			if (!mesh.IsVisibleInTree() || gun == null && !NamesContain(_active?.Data?.OriginalWeaponMeshNames ?? new Godot.Collections.Array<string>(), mesh.Name)) continue;
			Aabb box = TransformAabb(mesh.GetAabb(), _camera.GlobalTransform.AffineInverse() * mesh.GlobalTransform);
			bounds = bounds.HasValue ? bounds.Value.Merge(box) : box;
		}
		return bounds;
	}

	private float GetBarrelDot(ViewModelBase viewModel)
	{
		Node3D? gun = viewModel.FindChild("WeaponModel", true, false) as Node3D;
		Vector3 localAxis = new(0, 0, -1);
		if (gun == null)
		{
			MeshInstance3D? embedded = FindAll<MeshInstance3D>(viewModel).FirstOrDefault(m => NamesContain(viewModel.Data?.OriginalWeaponMeshNames ?? new Godot.Collections.Array<string>(), m.Name));
			if (embedded == null) return 0;
			Vector3 size = embedded.GetAabb().Size;
			localAxis = size.X >= size.Y && size.X >= size.Z ? Vector3.Right : size.Y >= size.Z ? Vector3.Up : Vector3.Back;
			Vector3 embeddedDirection = (embedded.GlobalBasis * localAxis).Normalized();
			return Math.Abs(embeddedDirection.Dot(-_camera.GlobalBasis.Z.Normalized()));
		}
		Vector3 direction = (gun.GlobalBasis * localAxis).Normalized();
		Vector3 cameraForward = -_camera.GlobalBasis.Z.Normalized();
		return direction.Dot(cameraForward);
	}

	private static Aabb TransformAabb(Aabb aabb, Transform3D transform)
	{
		Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
		Vector3 max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
		for (int i = 0; i < 8; i++)
		{
			Vector3 p = aabb.Position + new Vector3((i & 1) == 0 ? 0 : aabb.Size.X, (i & 2) == 0 ? 0 : aabb.Size.Y, (i & 4) == 0 ? 0 : aabb.Size.Z);
			p = transform * p; min = min.Min(p); max = max.Max(p);
		}
		return new Aabb(min, max - min);
	}

	private static bool NamesContain(Godot.Collections.Array<string> names, StringName name)
	{ foreach (string item in names) if (string.Equals(item, name.ToString(), StringComparison.OrdinalIgnoreCase)) return true; return false; }
	private static float MaxAxis(Vector3 v) => Math.Max(v.X, Math.Max(v.Y, v.Z));
	private static string Format(Aabb? b) => b.HasValue ? $"{b.Value.Position}/{b.Value.Size}" : "none";
	private static float Distance(Aabb? a, Aabb? b) => a.HasValue && b.HasValue ? Math.Min(a.Value.Position.Length(), b.Value.Position.Length()) : -1;
	private void Fail(string weapon, string reason) { GD.PrintErr($"VIEWMODEL RESULT weapon='{weapon}' FAIL: {reason}"); _failed = true; }
	private void SaveScreenshot(string fileName)
	{
		string directory = ProjectSettings.GlobalizePath("res://debug_shots");
		DirAccess.MakeDirRecursiveAbsolute(directory);
		Image? image = GetViewport().GetTexture().GetImage();
		if (image == null || image.IsEmpty()) { GD.Print($"VIEWMODEL SCREENSHOT unavailable '{fileName}' (empty viewport)"); return; }
		Error error = image.SavePng($"res://debug_shots/{fileName}");
		GD.Print($"VIEWMODEL SCREENSHOT file='res://debug_shots/{fileName}' result={error}");
	}
	private void AuditRemovedMannyAnimations()
	{
		string[] removed =
		{
			"res://art/animations/player/locomotion/player animation locomotion.glb",
			"res://art/animations/player/combat/player animation combat.glb",
			"res://art/animations/player/death/player animation death.glb"
		};
		bool sourceAssetsAbsent = removed.All(path => !ResourceLoader.Exists(path));
		PackedScene? mannyScene = ResourceLoader.Load<PackedScene>("res://art/characters/manny.glb");
		Node? manny = mannyScene?.Instantiate();
		AnimationPlayer? player = manny?.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		int clipCount = 0;
		if (player != null)
			foreach (StringName libraryName in player.GetAnimationLibraryList())
				clipCount += player.GetAnimationLibrary(libraryName)?.GetAnimationList().Count ?? 0;
		bool passed = sourceAssetsAbsent && clipCount == 0;
		GD.Print($"MANNY ANIMATION AUDIT standalone_assets_absent={sourceAssetsAbsent} embedded_clip_count={clipCount} {(passed ? "PASS" : "FAIL")}");
		if (!passed) _failed = true;
		manny?.Free();
	}
	private int CountMagentaPixels()
	{
		Image image = GetViewport().GetTexture().GetImage();
		if (image.IsEmpty()) return 0;
		int count = 0;
		for (int y = 0; y < image.GetHeight(); y++)
		for (int x = 0; x < image.GetWidth(); x++)
		{
			Color pixel = image.GetPixel(x, y);
			if (pixel.R > 0.75f && pixel.B > 0.75f && pixel.G < 0.25f) count++;
		}
		return count;
	}
	private static string SafeName(string value) => value.ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
	private static IEnumerable<T> FindAll<T>(Node root) where T : Node
	{ foreach (Node child in root.GetChildren()) { if (child is T item) yield return item; foreach (T nested in FindAll<T>(child)) yield return nested; } }
}
