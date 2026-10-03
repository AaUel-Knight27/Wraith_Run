using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

	public override void _Ready()
	{
		_camera = new Camera3D { Name = "CheckCamera", Current = true, Fov = 65, Near = 0.03f };
		AddChild(_camera);
		_ = Run();
	}

	private async Task Run()
	{
		GD.Print("VIEWMODEL CHECK STEP1 mapping and 3x switch lifecycle");
		PrintMappingTable();
		for (int pass = 1; pass <= 3; pass++)
		foreach (string weapon in Weapons)
		{
			ViewModelWeaponData? data = LoadData(weapon);
			ClearActive();
			if (data?.UseViewModel == true)
			{
				_active = new ViewModelBase { Name = $"{weapon.Replace(" ", string.Empty)}ViewModel" };
				_active.Configure(data);
				_camera.AddChild(_active);
				await NextFrame();
			}
			int count = CountLiveViewModels();
			int expected = data?.UseViewModel == true ? 1 : 0;
			GD.Print($"VIEWMODEL SWITCH_TEST pass={pass} weapon='{weapon}' live={count} expected={expected} {(count == expected ? "PASS" : "FAIL")}");
			if (count != expected) _failed = true;
		}

		// Run a full measurement pass after the lifecycle stress test.
		foreach (string weapon in Weapons)
		{
			ClearActive();
			ViewModelWeaponData? data = LoadData(weapon);
			if (data == null) { Fail(weapon, "missing data"); continue; }
			if (!data.UseViewModel)
			{
				GD.Print($"VIEWMODEL RESULT weapon='{weapon}' rig=legacy clip=legacy arms=none gun=legacy textures=legacy boneScale=legacy PASS");
				continue;
			}
			if (data.RigScene == null)
			{
				Fail(weapon, $"rig missing rig={data.RigScene?.ResourcePath ?? "none"}");
				continue;
			}
			_active = new ViewModelBase { Name = $"{weapon.Replace(" ", string.Empty)}ViewModel" };
			_active.Configure(data);
			_camera.AddChild(_active);
			await NextFrame();
			Skeleton3D? skeleton = _active.Skeleton;
			AnimationPlayer? player = _active.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
			if (skeleton == null || player == null) { Fail(weapon, "skeleton or AnimationPlayer missing"); continue; }

			var frameExtentByClip = new Dictionary<string, (float Min, float Max)>();
			bool weaponFail = CountLiveViewModels() != 1;
			CheckTextures(weapon, _active, data, ref weaponFail);
			foreach (var clipEntry in data.ClipMap)
			{
				string generic = clipEntry.Key;
				if (!_active.HasResolvedClip(generic)) continue;
				_active.Play(generic);
				await NextFrame();
				float clipLength = Math.Max(0.001f, _active.GetClipLength(generic));
				float minExtent = float.PositiveInfinity;
				float maxExtent = 0;
				int frame = 0;
				int frameLimit = Math.Max(1, (int)Math.Ceiling(clipLength * 60.0) + 1);
				while (frame < frameLimit)
				{
					await NextFrame();
					Aabb? arms = GetCameraBounds(_active, data.ArmMeshNames);
					if (arms.HasValue)
					{
						float extent = MaxAxis(arms.Value.Size);
						minExtent = Math.Min(minExtent, extent);
						maxExtent = Math.Max(maxExtent, extent);
						CheckPlacement(weapon, generic, "arms", arms.Value, ref weaponFail);
					}
					CheckBoneScales(weapon, generic, skeleton, ref weaponFail);
					frame++;
				}
				if (!float.IsFinite(minExtent)) minExtent = maxExtent = 0;
				frameExtentByClip[generic] = (minExtent, maxExtent);
				GD.Print($"VIEWMODEL CLIP weapon='{weapon}' clip='{generic}' resolved='{(_active.ResolvedClips.TryGetValue(generic, out string? actual) ? actual : "none")}' frames={frame} arms_extent_range=({minExtent:F4},{maxExtent:F4})m");
				if (generic == "idle") await SaveScreenshot($"{SafeName(weapon)}_idle.png");
				if (generic == "fire") await SaveScreenshot($"{SafeName(weapon)}_fire.png");
			}

			Aabb? armBounds = GetCameraBounds(_active, data.ArmMeshNames);
			Aabb? gunBounds = GetGunBounds(_active);
			float barrelDot = GetBarrelDot(_active);
			GD.Print($"VIEWMODEL METRICS weapon='{weapon}' rig='{data.RigScene.ResourcePath}' attach='{data.AttachBoneName}' clip='{player.CurrentAnimation}' arms={Format(armBounds)} gun={Format(gunBounds)} distance={Distance(armBounds, gunBounds):F4}m barrel_dot={barrelDot:F4} no_albedo_fail={weaponFail} bone_scale_fail={weaponFail}");
			if (armBounds == null || gunBounds == null) weaponFail = true;
			if (barrelDot < 0.8f) weaponFail = true;
			if (weaponFail) Fail(weapon, "one or more texture, pose, placement, bone-scale or barrel checks failed");
			else GD.Print($"VIEWMODEL RESULT weapon='{weapon}' PASS");
		}
		GD.Print(_failed ? "VIEWMODEL CHECK FAIL" : "VIEWMODEL CHECK PASS");
		GetTree().Quit(_failed ? 1 : 0);
	}

	private void PrintMappingTable()
	{
		foreach (string weapon in Weapons)
		{
			ViewModelWeaponData? data = LoadData(weapon);
			GD.Print($"VIEWMODEL MAP weapon='{weapon}' data='{data?.ResourcePath ?? "missing"}' rig='{data?.RigScene?.ResourcePath ?? "legacy"}' bone='{data?.AttachBoneName ?? "legacy"}' gun='{data?.WeaponModelScene?.ResourcePath ?? "legacy"}' needs_visual_review={data?.VisualReviewRequired ?? false}");
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

	private async Task NextFrame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

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
		if (!okay) { GD.PrintErr($"VIEWMODEL FAIL weapon='{weapon}' clip='{clip}' part='{part}' camera_aabb={box} center={p}"); fail = true; }
	}

	private static void CheckBoneScales(string weapon, string clip, Skeleton3D skeleton, ref bool fail)
	{
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
		{
			Vector3 s = skeleton.GetBonePoseScale(i);
			if (!float.IsFinite(s.X) || !float.IsFinite(s.Y) || !float.IsFinite(s.Z) || Math.Abs(s.X - 1) > 0.05 || Math.Abs(s.Y - 1) > 0.05 || Math.Abs(s.Z - 1) > 0.05)
			{
				GD.PrintErr($"VIEWMODEL FAIL weapon='{weapon}' clip='{clip}' bone='{skeleton.GetBoneName(i)}' scale={s}"); fail = true;
			}
		}
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
		if (gun == null) return 0;
		Vector3 direction = -gun.GlobalBasis.Z.Normalized();
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
	private async Task SaveScreenshot(string fileName)
	{
		await NextFrame();
		string directory = ProjectSettings.GlobalizePath("res://debug_shots");
		DirAccess.MakeDirRecursiveAbsolute(directory);
		Image? image = GetViewport().GetTexture().GetImage();
		if (image == null || image.IsEmpty()) { GD.Print($"VIEWMODEL SCREENSHOT unavailable '{fileName}' (empty viewport)"); return; }
		Error error = image.SavePng($"res://debug_shots/{fileName}");
		GD.Print($"VIEWMODEL SCREENSHOT file='res://debug_shots/{fileName}' result={error}");
	}
	private static string SafeName(string value) => value.ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
	private static IEnumerable<T> FindAll<T>(Node root) where T : Node
	{ foreach (Node child in root.GetChildren()) { if (child is T item) yield return item; foreach (T nested in FindAll<T>(child)) yield return nested; } }
}
