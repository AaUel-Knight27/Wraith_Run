using Godot;
using System.Collections.Generic;

/// <summary>Standalone editor/runtime sandbox for fitting the data-driven first-person weapon rigs.</summary>
public partial class ViewModelPreview : Node3D
{
	[Export] public string DataDirectory { get; set; } = "res://data/viewmodels";
	private readonly List<ViewModelWeaponData> _data = new();
	private Camera3D _camera = null!;
	private OptionButton _weaponPicker = null!;
	private VBoxContainer _clipList = null!;
	private Label _status = null!;
	private ViewModelBase? _active;
	private bool _showAxes;
	private bool _showBounds;
	private float _orbit;

	public override void _Ready()
	{
		CreateWorld();
		CreateInterface();
		LoadData();
		if (_data.Count > 0) SelectWeapon(0);
	}

	public override void _Process(double delta)
	{
		if (_active != null && IsInstanceValid(_active))
			_status.Text = $"{_active.Data?.WeaponName} | rig arms {_active.GetArmExtent():F3} m | normalizer available per weapon | camera near {_camera.Near:F2} m | arrows orbit";
	}

	public override void _Input(InputEvent input)
	{
		if (input is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.Left) _orbit -= 0.08f;
			if (key.Keycode == Key.Right) _orbit += 0.08f;
			_camera.Rotation = new Vector3(0, _orbit, 0);
		}
	}

	private void CreateWorld()
	{
		_camera = new Camera3D { Name = "PreviewCamera", Position = new Vector3(0, 1.55f, 0), Fov = 65, Near = 0.03f, Current = true };
		AddChild(_camera);
		var light = new DirectionalLight3D { RotationDegrees = new Vector3(-45, -25, 0), ShadowEnabled = false };
		AddChild(light);
		var floor = new MeshInstance3D { Name = "GroundPlane", Position = new Vector3(0, 0, -1.5f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
		floor.Mesh = new PlaneMesh { Size = new Vector2(30, 30) };
		floor.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.18f, 0.2f, 0.23f), Roughness = 1 };
		AddChild(floor);
		var env = new WorldEnvironment();
		env.Environment = new Environment { BackgroundMode = Environment.BGMode.Color, BackgroundColor = new Color(0.055f, 0.065f, 0.08f), AmbientLightSource = Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.58f, 0.64f), AmbientLightEnergy = 0.7f };
		AddChild(env);
	}

	private void CreateInterface()
	{
		var layer = new CanvasLayer(); AddChild(layer);
		var panel = new PanelContainer { Position = new Vector2(12, 12), CustomMinimumSize = new Vector2(390, 520) }; layer.AddChild(panel);
		var columns = new VBoxContainer { CustomMinimumSize = new Vector2(370, 500) }; panel.AddChild(columns);
		columns.AddChild(new Label { Text = "Viewmodel Preview" });
		_weaponPicker = new OptionButton(); columns.AddChild(_weaponPicker);
		_weaponPicker.ItemSelected += index => SelectWeapon((int)index);
		var toggles = new HBoxContainer(); columns.AddChild(toggles);
		var axes = new CheckButton { Text = "Bone axes" }; toggles.AddChild(axes);
		axes.Toggled += value => { _showAxes = value; _active?.SetDebugOptions(_showAxes, _showBounds); };
		var bounds = new CheckButton { Text = "Mesh AABBs" }; toggles.AddChild(bounds);
		bounds.Toggled += value => { _showBounds = value; _active?.SetDebugOptions(_showAxes, _showBounds); };
		_status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart }; columns.AddChild(_status);
		columns.AddChild(new Label { Text = "Play animation" });
		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(360, 340), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		columns.AddChild(scroll);
		_clipList = new VBoxContainer(); _clipList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; scroll.AddChild(_clipList);
		columns.AddChild(new Label { Text = "Arrow keys rotate the preview camera." });
	}

	private void LoadData()
	{
		using var dir = DirAccess.Open(DataDirectory);
		if (dir == null) { GD.PushError($"Could not open viewmodel data directory '{DataDirectory}'."); return; }
		dir.ListDirBegin(); string file;
		while ((file = dir.GetNext()) != "")
		{
			if (dir.CurrentIsDir() || !file.EndsWith(".tres")) continue;
			var item = ResourceLoader.Load<ViewModelWeaponData>($"{DataDirectory.TrimEnd('/')}/{file}");
			if (item == null) continue;
			_data.Add(item);
		}
		dir.ListDirEnd();
		_data.Sort((a, b) => string.Compare(a.WeaponName, b.WeaponName, System.StringComparison.Ordinal));
		foreach (var item in _data) _weaponPicker.AddItem(item.WeaponName + (item.UseViewModel ? "" : " (no viewmodel)"));
	}

	private void SelectWeapon(int index)
	{
		if (index < 0 || index >= _data.Count) return;
		if (_active != null) { _active.QueueFree(); _active = null; }
		foreach (Node node in _clipList.GetChildren()) node.QueueFree();
		var data = _data[index];
		if (!data.UseViewModel || data.RigScene == null)
		{
			_status.Text = $"{data.WeaponName}: no first-person viewmodel configured.";
			return;
		}
		_active = new ViewModelBase { Name = "PreviewViewModel" };
		_active.Configure(data);
		_camera.AddChild(_active);
		_active.SetDebugOptions(_showAxes, _showBounds);
		foreach (string clip in data.ClipMap.Keys)
		{
			string clipName = clip;
			var button = new Button { Text = $"{clipName}  →  {data.ClipMap[clipName]}" };
			button.Pressed += () => _active?.Play(clipName);
			_clipList.AddChild(button);
		}
		GD.Print($"Preview selected {data.WeaponName}: {data.RigScene.ResourcePath}, attach={data.AttachBoneName}, clips={data.ClipMap.Count}");
	}
}
