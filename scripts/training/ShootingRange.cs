using System;
using System.Collections.Generic;
using Godot;

/// <summary>Small offline firing range. Uses the production Player and hitscan weapon code.</summary>
public partial class ShootingRange : Node3D
{
	private static readonly string[] LaneNames = { "SHORT", "MEDIUM", "LONG" };
	private static readonly float[] LaneRanges = { 10.0f, 30.0f, 75.0f };
	private static readonly float[] LaneCenters = { -8.0f, 0.0f, 8.0f };
	private static readonly float[] TargetOffsets = { -2.0f, -0.7f, 0.7f, 2.0f };
	private readonly int[] _hitCounts = new int[LaneNames.Length];
	private readonly List<ShootingTarget> _targets = new();
	private readonly Label[] _counterLabels = new Label[LaneNames.Length];
	private WeaponSwitcher? _weapons;
	private OptionButton? _weaponPicker;
	private bool _resetWasDown;
	private bool _ownsOfflinePeer;

	public override void _Ready()
	{
		EnterOfflineSession();
		BuildLighting();
		BuildRangeGeometry();
		BuildLaneSigns();
		BuildTargets();
		SpawnPlayer();
		BuildHud();
		// Whoever sent the player here (the hub tablet, the old menu) put the loading screen up; only a match
		// scene used to take it down, so without this it covered the range for good and looked like a freeze.
		LoadingScreen.Instance?.Hide();
	}

	public override void _ExitTree()
	{
		// Hand the hub the same state it expects after leaving a session (no peer), so it starts its own.
		if (_ownsOfflinePeer && Multiplayer.MultiplayerPeer is OfflineMultiplayerPeer)
			Multiplayer.MultiplayerPeer = null;
	}

	/// <summary>The range is a private, offline scene. Coming from the hub the network session has just been
	/// torn down and no peer is assigned, so every Multiplayer.GetUniqueId() in the Player and HUD code
	/// logged an error each frame (thousands of them, which is what froze the debugger). Close any leftover
	/// session and give the scene Godot's own offline peer: this machine is peer 1 and the authority.</summary>
	private void EnterOfflineSession()
	{
		GetNodeOrNull<LanSession>("/root/LanSession")?.Stop();
		MatchManager.Instance?.EndSession();
		if (Multiplayer.MultiplayerPeer == null)
		{
			Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
			_ownsOfflinePeer = true;
		}
	}

	public override void _Process(double delta)
	{
		bool resetDown = Input.IsKeyPressed(Key.F6);
		if (resetDown && !_resetWasDown) ResetTargets();
		_resetWasDown = resetDown;
	}

	private void BuildLighting()
	{
		var environment = new WorldEnvironment { Name = "RangeEnvironment" };
		environment.Environment = new Godot.Environment
		{
			BackgroundMode = Godot.Environment.BGMode.Color,
			BackgroundColor = new Color(0.31f, 0.36f, 0.39f),
			AmbientLightSource = Godot.Environment.AmbientSource.Color,
			AmbientLightColor = new Color(0.66f, 0.69f, 0.70f),
			AmbientLightEnergy = 0.8f,
		};
		AddChild(environment);
		AddChild(new DirectionalLight3D
		{
			Name = "Sun",
			RotationDegrees = new Vector3(-48.0f, -22.0f, 0.0f),
			LightEnergy = 1.1f,
			ShadowEnabled = true,
		});
	}

	private void BuildRangeGeometry()
	{
		var floor = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.36f, 0.34f), Roughness = 1.0f };
		var wall = new StandardMaterial3D { AlbedoColor = new Color(0.48f, 0.48f, 0.43f), Roughness = 1.0f };
		var divider = new StandardMaterial3D { AlbedoColor = new Color(0.27f, 0.30f, 0.28f), Roughness = 1.0f };
		AddBox("Floor", new Vector3(0, -0.25f, -37.0f), new Vector3(24, 0.5f, 90), floor);
		AddBox("LeftWall", new Vector3(-12.25f, 2.5f, -37.0f), new Vector3(0.5f, 5, 90), wall);
		AddBox("RightWall", new Vector3(12.25f, 2.5f, -37.0f), new Vector3(0.5f, 5, 90), wall);
		AddBox("Backstop", new Vector3(0, 3.0f, -82.25f), new Vector3(24, 6, 0.5f), wall);
		AddBox("LaneDividerLeft", new Vector3(-4.0f, 0.4f, -37.0f), new Vector3(0.16f, 0.8f, 87), divider);
		AddBox("LaneDividerRight", new Vector3(4.0f, 0.4f, -37.0f), new Vector3(0.16f, 0.8f, 87), divider);
	}

	private void AddBox(string name, Vector3 position, Vector3 size, Material material)
	{
		var body = new StaticBody3D { Name = name, Position = position };
		var shape = new CollisionShape3D { Shape = new BoxShape3D { Size = size } };
		var mesh = new MeshInstance3D
		{
			Mesh = new BoxMesh { Size = size },
			MaterialOverride = material,
		};
		body.AddChild(shape);
		body.AddChild(mesh);
		AddChild(body);
	}

	private void BuildLaneSigns()
	{
		for (int lane = 0; lane < LaneNames.Length; lane++)
		{
			var sign = new Label3D
			{
				Name = $"{LaneNames[lane]}Sign",
				Text = $"{LaneNames[lane]}  {LaneRanges[lane]:0} m",
				Position = new Vector3(LaneCenters[lane], 2.4f, 1.2f),
				FontSize = 48,
				PixelSize = 0.006f,
				Modulate = new Color(1.0f, 0.82f, 0.32f),
			};
			AddChild(sign);

			var downrange = new Label3D
			{
				Name = $"{LaneNames[lane]}DistanceMarker",
				Text = $"{LaneRanges[lane]:0} m",
				Position = new Vector3(LaneCenters[lane], 2.0f, -LaneRanges[lane]),
				FontSize = 36,
				PixelSize = 0.004f,
				Modulate = new Color(1.0f, 0.82f, 0.32f),
			};
			AddChild(downrange);
		}
	}

	private void BuildTargets()
	{
		var steel = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.31f, 0.33f), Metallic = 0.55f, Roughness = 0.48f };
		var flash = new StandardMaterial3D { AlbedoColor = new Color(1.0f, 0.68f, 0.12f), EmissionEnabled = true, Emission = new Color(1.0f, 0.4f, 0.04f), EmissionEnergyMultiplier = 1.2f };
		for (int lane = 0; lane < LaneNames.Length; lane++)
		{
			foreach (float offset in TargetOffsets)
			{
				var target = new ShootingTarget
				{
					Name = $"{LaneNames[lane]}Target{_targets.Count + 1}",
					Position = new Vector3(LaneCenters[lane] + offset, 0.0f, -LaneRanges[lane]),
					CollisionLayer = 1,
					CollisionMask = 0,
				};
				target.Configure(lane, steel, flash);
				target.Hit += OnTargetHit;
				AddTargetPlate(target, "UpperPlate", new Vector3(0, 1.25f, 0), new Vector3(0.62f, 0.8f, 0.14f), steel);
				AddTargetPlate(target, "LowerPlate", new Vector3(0, 0.63f, 0), new Vector3(0.46f, 0.48f, 0.14f), steel);
				AddChild(target);
				_targets.Add(target);
			}
		}
	}

	private static void AddTargetPlate(ShootingTarget target, string name, Vector3 position, Vector3 size, Material material)
	{
		var plate = new MeshInstance3D
		{
			Name = name,
			Position = position,
			Mesh = new BoxMesh { Size = size },
			MaterialOverride = material,
		};
		var collision = new CollisionShape3D
		{
			Name = $"{name}Collision",
			Position = position,
			Shape = new BoxShape3D { Size = size },
		};
		target.RegisterMesh(plate);
		target.AddChild(plate);
		target.AddChild(collision);
	}

	private void SpawnPlayer()
	{
		var packed = ResourceLoader.Load<PackedScene>("res://scenes/Player.tscn");
		if (packed == null)
		{
			GD.PushError("ShootingRange: could not load the production Player scene.");
			return;
		}
		var player = packed.Instantiate<CharacterBody3D>();
		player.Name = "Player";
		player.Position = new Vector3(0, 0, 2.0f);
		AddChild(player);
		_weapons = player.GetNodeOrNull<WeaponSwitcher>("WeaponSwitcher");
		if (_weapons == null)
		{
			GD.PushError("ShootingRange: the production Player has no WeaponSwitcher.");
			return;
		}
		_weapons.ShotFired += OnShotFired;
	}

	private void BuildHud()
	{
		var layer = new CanvasLayer { Name = "RangeHud", Layer = 3 };
		AddChild(layer);
		var panel = new PanelContainer
		{
			Name = "RangeControls",
			AnchorLeft = 0.02f,
			AnchorTop = 0.04f,
			AnchorRight = 0.36f,
			AnchorBottom = 0.36f,
			OffsetLeft = 0,
			OffsetTop = 0,
			OffsetRight = 0,
			OffsetBottom = 0,
		};
		layer.AddChild(panel);
		var column = new VBoxContainer { Name = "Controls", Theme = BuildHudTheme() };
		panel.AddChild(column);
		column.AddChild(new Label { Text = "SHOOTING RANGE   •   FIRE: LMB   •   RESET: F6" });
		_weaponPicker = new OptionButton { Name = "WeaponPicker", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
		var registry = ResourceLoader.Load<WeaponRegistry>("res://scripts/data/WeaponRegistry.tres");
		if (registry != null)
		{
			foreach (var weapon in registry.Weapons) _weaponPicker.AddItem(weapon.WeaponName);
			_weaponPicker.ItemSelected += OnWeaponSelected;
		}
		column.AddChild(_weaponPicker);
		for (int lane = 0; lane < LaneNames.Length; lane++)
		{
			_counterLabels[lane] = new Label { Name = $"{LaneNames[lane]}Counter", Text = $"{LaneNames[lane]}  {LaneRanges[lane]:0} m: 0 hits" };
			column.AddChild(_counterLabels[lane]);
		}
		var reset = new Button { Text = "RESET TARGET COUNTERS" };
		reset.Pressed += ResetTargets;
		column.AddChild(reset);
		_weaponPicker?.Select(0);
	}

	private static Theme BuildHudTheme()
	{
		var theme = new Theme();
		theme.DefaultFontSize = 20;
		return theme;
	}

	private void OnWeaponSelected(long index)
	{
		if (_weaponPicker == null || _weapons == null) return;
		string name = _weaponPicker.GetItemText((int)index);
		if (!_weapons.EquipByNameForPractice(name))
			GD.PushWarning($"ShootingRange: could not equip '{name}'.");
		else
			_weapons.RefillAmmo();
	}

	private void OnShotFired(WeaponData weapon, int pellets) => _weapons?.RefillAmmo();

	private void OnTargetHit(int lane)
	{
		if (lane < 0 || lane >= _hitCounts.Length) return;
		_hitCounts[lane]++;
		if (_counterLabels[lane] != null)
			_counterLabels[lane].Text = $"{LaneNames[lane]}  {LaneRanges[lane]:0} m: {_hitCounts[lane]} hits";
	}

	private void ResetTargets()
	{
		Array.Clear(_hitCounts);
		for (int lane = 0; lane < _counterLabels.Length; lane++)
			if (_counterLabels[lane] != null)
				_counterLabels[lane].Text = $"{LaneNames[lane]}  {LaneRanges[lane]:0} m: 0 hits";
		foreach (var target in _targets) target.ResetTarget();
		_weapons?.RefillAmmo();
	}
}