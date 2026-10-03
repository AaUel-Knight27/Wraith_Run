using Godot;
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
	}

	public void EquipWeapon(string weaponName)
	{
		if (!_localHuman || weaponName == _activeWeapon) return;
		_activeWeapon = weaponName;
		ClearViewModel();
		if (!_weaponData.TryGetValue(weaponName, out ViewModelWeaponData? data) || !data.UseViewModel) return;
		if (data.RigScene == null || data.WeaponModelScene == null)
		{
			GD.PushWarning($"{weaponName}: viewmodel data is enabled but rig/model scene is missing.");
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
	}

	public void Play(string genericName, float speed = 1.0f)
	{ if (_localHuman) _activeViewModel?.Play(genericName, speed); }

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
			if (dir.CurrentIsDir() || !file.EndsWith(".tres")) continue;
			string path = $"{DataDirectory.TrimEnd('/')}/{file}";
			var data = ResourceLoader.Load<ViewModelWeaponData>(path);
			if (data == null || string.IsNullOrWhiteSpace(data.WeaponName)) { GD.PushWarning($"Invalid viewmodel data resource '{path}'."); continue; }
			_weaponData[data.WeaponName] = data;
			GD.Print($"VIEWMODEL DATA weapon='{data.WeaponName}' rig='{data.RigScene?.ResourcePath ?? "none"}' attach='{data.AttachBoneName}' clips={FormatClipMap(data.ClipMap)} enabled={data.UseViewModel}");
		}
		dir.ListDirEnd();
		ValidateAndLogWeaponData();
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
			if (data.WeaponModelScene == null)
				GD.PushWarning($"{data.WeaponName}: no weapon model scene is configured.");
			else if (!string.IsNullOrEmpty(data.MagazineNodeName))
			{
				Node model = data.WeaponModelScene.Instantiate();
				if (model.FindChild(data.MagazineNodeName, true, false) == null)
					GD.PushWarning($"{data.WeaponName}: magazine node '{data.MagazineNodeName}' is missing in {data.WeaponModelScene.ResourcePath}.");
				model.Free();
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
				rig.Free();
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
		if (_activeViewModel != null && IsInstanceValid(_activeViewModel)) _activeViewModel.QueueFree();
		_activeViewModel = null;
		_lastMovementClip = string.Empty;
		_cameraMotion?.SetViewModel(null);
		if (_armature != null && IsInstanceValid(_armature)) _armature.Visible = true;
	}

	private static string FormatClipMap(Godot.Collections.Dictionary<string, string> map)
	{ var rows = new List<string>(); foreach (var pair in map) rows.Add($"{pair.Key}={pair.Value}"); return string.Join(",", rows); }
}
