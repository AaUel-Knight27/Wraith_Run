using Godot;
using System;
using System.Collections.Generic;

/// <summary>Loads per-weapon viewmodel data only on the owning human player.</summary>
public partial class ViewModelManager : Node3D
{
	[Export] public string DataDirectory { get; set; } = "res://data/viewmodels";
	private readonly Dictionary<string, ViewModelWeaponData> _weaponData = new();
	private PlayerMovement? _movement;
	private Node3D? _armature;
	private Camera3D? _camera;
	private ViewModelCameraMotion? _cameraMotion;
	private ViewModelBase? _activeViewModel;
	private bool _localHuman;
	private string _activeWeapon = string.Empty;
	private string _lastMovementClip = string.Empty;
	private readonly Dictionary<string, ViewModelWeaponData> _savedSnapshots = new();
	private readonly Dictionary<string, ViewModelWeaponData> _baseSnapshots = new();
	private readonly Dictionary<string, string> _sourcePaths = new();

	public ViewModelBase? ActiveViewModel => _activeViewModel;
	public string ActiveWeaponName => _activeWeapon;
	public IEnumerable<string> WeaponNames => _weaponData.Keys;

	public override void _Ready()
	{
		Node? player = GetParent()?.GetParent()?.GetParent();
		_localHuman = PlayerIdentity.IsLocalHuman(player);
		SetProcess(_localHuman);
		if (!_localHuman) return;
		_movement = player as PlayerMovement;
		_armature = player?.GetNodeOrNull<Node3D>("Armature");
		_camera = player?.GetNodeOrNull<Camera3D>("Head/CameraShake/FirstPersonCamera");
		_cameraMotion = GetParent() as ViewModelCameraMotion;
		LoadWeaponData();
		var tuner = new ViewModelTuner { Name = "ViewModelTuner", Manager = this };
		AddChild(tuner);
	}

	public override void _Process(double delta)
	{
		if (_activeViewModel == null) return;
		// The tablet is a physical device in front of the player's camera. Leaving the weapon
		// viewmodel active puts its hands and gun through the tablet/menu, as though the player is
		// holding both items at once. Hide it while the tablet owns the interface.
		bool tabletOpen = GameUiState.MenuOpen;
		_activeViewModel.Visible = !tabletOpen;
		_activeViewModel.SetAiming(!tabletOpen && _movement?.IsAiming == true);
	}

	public void EquipWeapon(string weaponName)
	{
		if (!_localHuman || weaponName == _activeWeapon) return;
		_activeWeapon = weaponName;
		ClearViewModel();
		if (!_weaponData.TryGetValue(weaponName, out ViewModelWeaponData? data) || !data.UseViewModel)
		{
			GD.Print($"VIEWMODEL SWITCH weapon='{weaponName}' live={GetLiveViewModelCount()}");
			return;
		}
		if (data.RigScene == null)
		{
			GD.PushWarning($"{weaponName}: viewmodel data is enabled but rig scene is missing.");
			return;
		}
		var viewModel = new ViewModelBase { Name = $"{weaponName.Replace(" ", string.Empty)}ViewModel" };
		viewModel.Configure(data);
		_activeViewModel = viewModel;
		_lastMovementClip = string.Empty;
		if (_camera != null) _camera.AddChild(viewModel);
		else AddChild(viewModel);
		if (_armature != null) _armature.Visible = false;
		_cameraMotion?.SetViewModel(viewModel);
		if (!viewModel.Play("draw")) viewModel.Play("idle");
		GD.Print($"VIEWMODEL SWITCH weapon='{weaponName}' live={GetLiveViewModelCount()}");
	}

	/// <summary>Counts currently attached viewmodel roots; used by the runtime diagnostic.</summary>
	public int GetLiveViewModelCount()
	{
		int count = 0;
		Node root = _camera is Node cameraNode ? cameraNode : this;
		foreach (Node child in root.GetChildren())
			if (child is ViewModelBase && !child.IsQueuedForDeletion()) count++;
		return count;
	}

	public void Play(string genericName, float speed = 1.0f)
	{ if (_localHuman) _activeViewModel?.Play(genericName, speed); }

	public void PlayMuzzleFire()
	{ if (_localHuman) _activeViewModel?.PlayMuzzleFire(); }

	public ViewModelWeaponData? GetWeaponData(string weaponName) =>
		_weaponData.TryGetValue(weaponName, out ViewModelWeaponData? data) ? data : null;

	public ViewModelWeaponData? GetSavedSnapshot(string weaponName) =>
		_savedSnapshots.TryGetValue(weaponName, out ViewModelWeaponData? data) ? data : null;

	public bool IsModified(string weaponName)
	{
		if (!_weaponData.TryGetValue(weaponName, out ViewModelWeaponData? data) || !_savedSnapshots.TryGetValue(weaponName, out ViewModelWeaponData? saved)) return false;
		foreach (Godot.Collections.Dictionary property in data.GetPropertyList())
			if (IsStoredExport(property))
			{
				string name = property["name"].AsString();
				if (!data.Get(name).Equals(saved.Get(name))) return true;
			}
		return false;
	}

	public bool EquipForTuner(string weaponName)
	{
		if (!_localHuman) return false;
		WeaponSwitcher? switcher = GetParent()?.GetParent()?.GetParent()?.GetNodeOrNull<WeaponSwitcher>("WeaponSwitcher");
		return switcher?.EquipByName(weaponName) ?? false;
	}

	public void RefreshActiveViewModel(bool rebuild = false)
	{
		if (!_localHuman || _activeViewModel == null) return;
		if (rebuild)
		{
			string weaponName = _activeWeapon;
			_activeWeapon = string.Empty;
			EquipWeapon(weaponName);
		}
		else _activeViewModel.ApplyData();
	}

	public string GetSourcePath(string weaponName) => _sourcePaths.TryGetValue(weaponName, out string? path) ? path : string.Empty;

	public void RestoreSaved(string weaponName)
	{
		if (!_weaponData.TryGetValue(weaponName, out ViewModelWeaponData? target) || !_savedSnapshots.TryGetValue(weaponName, out ViewModelWeaponData? source)) return;
		CopyProperties(source, target);
		RefreshActiveViewModel();
	}

	public void PlayForDuration(string genericName, float duration)
	{
		if (!_localHuman || _activeViewModel == null) return;
		float length = _activeViewModel.GetClipLength(genericName);
		float speed = length > 0 && duration > 0 ? length / duration : 1.0f;
		_activeViewModel.Play(genericName, speed);
	}

	public void PlayReload(float duration, bool fullReload)
	{
		if (_activeViewModel == null) return;
		PlayForDuration(fullReload && _activeViewModel.HasResolvedClip("reload_full") ? "reload_full" : "reload", duration);
	}

	public void UpdateMovement(PlayerMovement.MovementState state)
	{
		if (!_localHuman || _activeViewModel == null) return;
		string clip = state switch
		{
			PlayerMovement.MovementState.Walk or PlayerMovement.MovementState.CrouchWalk => "walk",
			PlayerMovement.MovementState.Sprint => "run",
			_ => "idle",
		};
		if (_activeViewModel.HasResolvedClip(clip) && clip != _lastMovementClip)
		{
			_lastMovementClip = clip;
			_activeViewModel.Play(clip);
		}
	}

	private void LoadWeaponData()
	{
		if (!DirAccess.DirExistsAbsolute(DataDirectory)) { GD.PushWarning($"Viewmodel data directory '{DataDirectory}' does not exist."); return; }
		using var dir = DirAccess.Open(DataDirectory);
		if (dir == null) return;
		dir.ListDirBegin();
		string file;
		while ((file = dir.GetNext()) != "")
		{
			if (dir.CurrentIsDir()) continue;
			// Godot's exported PCK stores imported text resources under their remap names
			// (for example AK47.tres.remap). Keep loading the original resource path so
			// ResourceLoader follows the remap, but normalize the filename before filtering.
			string resourceFile = file.EndsWith(".remap", System.StringComparison.Ordinal)
				? file[..^".remap".Length]
				: file;
			if (!resourceFile.EndsWith(".tres", System.StringComparison.Ordinal)) continue;
			string path = $"{DataDirectory.TrimEnd('/')}/{resourceFile}";
			var data = ResourceLoader.Load<ViewModelWeaponData>(path);
			if (data == null || string.IsNullOrWhiteSpace(data.WeaponName)) { GD.PushWarning($"Invalid viewmodel data resource '{path}'."); continue; }
			_baseSnapshots[data.WeaponName] = (ViewModelWeaponData)data.Duplicate(true);
			ApplyUserOverride(data);
			_weaponData[data.WeaponName] = data;
			_savedSnapshots[data.WeaponName] = (ViewModelWeaponData)data.Duplicate(true);
			_sourcePaths[data.WeaponName] = path;
			GD.Print($"VIEWMODEL DATA weapon='{data.WeaponName}' rig='{data.RigScene?.ResourcePath ?? "none"}' attach='{data.AttachBoneName}' clips={FormatClipMap(data.ClipMap)} enabled={data.UseViewModel}");
		}
		dir.ListDirEnd();
		ValidateAndLogWeaponData();
	}

	private static string OverridePath(string weaponName) => $"user://viewmodel_overrides/{weaponName}.json";

	private static void ApplyUserOverride(ViewModelWeaponData data)
	{
		string path = OverridePath(data.WeaponName);
		if (!FileAccess.FileExists(path)) return;
		using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		if (file == null) return;
		Variant parsed = Json.ParseString(file.GetAsText());
		if (parsed.VariantType != Variant.Type.Dictionary) { GD.PushWarning($"Invalid viewmodel override '{path}'."); return; }
		var values = parsed.AsGodotDictionary();
		foreach (Variant key in values.Keys)
		{
			string property = key.AsString();
			if (values[key].VariantType != Variant.Type.String) continue;
			data.Set(property, GD.StrToVar(values[key].AsString()));
		}
	}

	public Error SaveOverride(string weaponName, out string path)
	{
		path = OverridePath(weaponName);
		if (!_weaponData.TryGetValue(weaponName, out ViewModelWeaponData? data) || !_baseSnapshots.TryGetValue(weaponName, out ViewModelWeaponData? baseline)) return Error.DoesNotExist;
		DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("user://viewmodel_overrides"));
		var changed = new Godot.Collections.Dictionary<string, string>();
		foreach (Godot.Collections.Dictionary property in data.GetPropertyList())
		{
			if (!IsStoredExport(property)) continue;
			string name = property["name"].AsString();
			Variant value = data.Get(name);
			if (!value.Equals(baseline.Get(name))) changed[name] = GD.VarToStr(value);
		}
		using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
		if (file == null) return FileAccess.GetOpenError();
		file.StoreString(Json.Stringify(changed, "\t"));
		_savedSnapshots[weaponName] = (ViewModelWeaponData)data.Duplicate(true);
		return Error.Ok;
	}

	public Error SaveResource(string weaponName, string path)
	{
		if (!_weaponData.TryGetValue(weaponName, out ViewModelWeaponData? data)) return Error.DoesNotExist;
		Error result = ResourceSaver.Save(data, path);
		if (result == Error.Ok)
		{
			_savedSnapshots[weaponName] = (ViewModelWeaponData)data.Duplicate(true);
			if (FileAccess.FileExists(OverridePath(weaponName)))
				DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(OverridePath(weaponName)));
		}
		return result;
	}

	private static bool IsStoredExport(Godot.Collections.Dictionary property)
	{
		if (!property.ContainsKey("usage")) return false;
		PropertyUsageFlags usage = (PropertyUsageFlags)property["usage"].AsInt64();
		// C# Resource exports are marked Storage at runtime, but some Godot builds do not retain
		// the Editor bit in GetPropertyList(). Storage is the stable persistence contract here.
		return usage.HasFlag(PropertyUsageFlags.Storage);
	}

	private static void CopyProperties(ViewModelWeaponData source, ViewModelWeaponData target)
	{
		foreach (Godot.Collections.Dictionary property in source.GetPropertyList())
			if (IsStoredExport(property))
			{
				string name = property["name"].AsString();
				target.Set(name, source.Get(name));
			}
	}

	private void ValidateAndLogWeaponData()
	{
		var clipNamesByRig = new Dictionary<string, (string[] Clips, Godot.Collections.Array<string> Bones)>();
		foreach (ViewModelWeaponData data in _weaponData.Values)
		{
			if (!data.UseViewModel || data.RigScene == null)
			{
				GD.Print($"VIEWMODEL RESOLVED weapon='{data.WeaponName}' rig='none' attach='none' clips=<disabled>");
				continue;
			}
			if (data.WeaponModelScene == null && data.HideOriginalWeapon)
				GD.PushWarning($"{data.WeaponName}: no weapon model scene is configured.");
			else if (!string.IsNullOrEmpty(data.MagazineNodeName))
			{
				Node model = data.WeaponModelScene.Instantiate();
				if (model.FindChild(data.MagazineNodeName, true, false) == null)
					GD.PushWarning($"{data.WeaponName}: magazine node '{data.MagazineNodeName}' is missing in {data.WeaponModelScene.ResourcePath}.");
				model.QueueFree();
			}
			string rigPath = data.RigScene.ResourcePath;
			if (!clipNamesByRig.ContainsKey(rigPath))
			{
				Node rig = data.RigScene.Instantiate();
				var animations = rig.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
				var skeleton = rig.FindChild("Skeleton3D", true, false) as Skeleton3D;
				var bones = new Godot.Collections.Array<string>();
				if (skeleton != null) for (int i = 0; i < skeleton.GetBoneCount(); i++) bones.Add(skeleton.GetBoneName(i));
				clipNamesByRig[rigPath] = (animations?.GetAnimationList() ?? System.Array.Empty<string>(), bones);
				rig.QueueFree();
			}
			var rigInfo = clipNamesByRig[rigPath];
			var resolved = new List<string>();
			foreach (var pair in data.ClipMap)
			{
				string? clip = ResolveFromNames(pair.Value, rigInfo.Clips);
				if (clip == null) GD.PushWarning($"{data.WeaponName}: clip '{pair.Key}' -> '{pair.Value}' is missing or ambiguous in {rigPath}.");
				else resolved.Add($"{pair.Key}={clip}");
			}
			if (!string.IsNullOrEmpty(data.AttachBoneName) && !rigInfo.Bones.Contains(data.AttachBoneName))
				GD.PushWarning($"{data.WeaponName}: attach bone '{data.AttachBoneName}' is missing in {rigPath}.");
			GD.Print($"VIEWMODEL RESOLVED weapon='{data.WeaponName}' rig='{rigPath}' attach='{data.AttachBoneName}' clips={string.Join(",", resolved)}");
		}
	}

	private static string? ResolveFromNames(string requested, string[] names)
	{
		foreach (string name in names) if (name == requested) return name;
		var matches = new List<string>();
		foreach (string name in names) if (name.EndsWith("|" + requested, System.StringComparison.Ordinal)) matches.Add(name);
		if (matches.Count == 1) return matches[0];
		if (matches.Count > 1) return null;
		foreach (string name in names) if (name.EndsWith(requested, System.StringComparison.Ordinal)) matches.Add(name);
		return matches.Count == 1 ? matches[0] : null;
	}

	private void ClearViewModel()
	{
		if (_activeViewModel != null && IsInstanceValid(_activeViewModel))
		{
			_activeViewModel.Visible = false;
			_animationPlayerStop(_activeViewModel);
			_activeViewModel.GetParent()?.RemoveChild(_activeViewModel);
			_activeViewModel.QueueFree();
		}
		_activeViewModel = null;
		_lastMovementClip = string.Empty;
		_cameraMotion?.SetViewModel(null);
		if (_armature != null && IsInstanceValid(_armature)) _armature.Visible = true;
	}

	private static void _animationPlayerStop(ViewModelBase viewModel)
	{
		if (viewModel.FindChild("AnimationPlayer", true, false) is AnimationPlayer animationPlayer)
			animationPlayer.Stop();
	}

	private static string FormatClipMap(Godot.Collections.Dictionary<string, string> map)
	{ var rows = new List<string>(); foreach (var pair in map) rows.Add($"{pair.Key}={pair.Value}"); return string.Join(",", rows); }
}
