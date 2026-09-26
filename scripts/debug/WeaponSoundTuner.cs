using Godot;
using System;
using System.Globalization;

/// <summary>
/// Live in-game mixing console for the currently equipped weapon's fire sound. Development tool
/// only: it arms for the local player and is inert in release exports - same rule WeaponGripTuner
/// (F3) and AnimationTuner (F8) already follow.
///
/// F6  Open / close the tuner panel. Opening it pauses mouse-look and firing, the same trade-off
///     WeaponGripTuner's F7 entry panel makes, so the mouse is free to drag sliders. A "Test Fire"
///     button in the panel plays the shot on demand so every change is heard immediately.
/// F1  Print the current weapon's sound block to the console AND copy it to the clipboard, ready
///     to paste into its entry in WeaponRegistry.tres.
/// F9  Discard changes and reload the current weapon's numbers from WeaponRegistry.tres.
///
/// LIVE SYNC (the "duo" case): while the panel is open, every change is sent to the other players,
/// at most every SyncIntervalSeconds and at least every KeepAliveSeconds. This is not cosmetic
/// like WeaponGripTuner's grip sync - a Resource loaded in one game process is not visible to any
/// other process at all, so without this a host and a client tuning together over LAN would each
/// only ever hear their own half-tuned copy of the gun. Godot's own Remote scene tree tab covers
/// single-machine, in-editor tuning just fine (including the reverb zones - see the README); this
/// exists for testing across two actual machines, which the Remote tab cannot reach.
///
/// See scripts/autoloads/SoundManager.cs for what these numbers actually do to the sound, and
/// WeaponData.cs for what each field means.
/// </summary>
public partial class WeaponSoundTuner : Node
{
	private const Key ToggleKey = Key.F6;
	private const Key PrintKey = Key.F1;
	private const Key ResetKey = Key.F9;

	private const float SyncIntervalSeconds = 0.05f;
	private const float KeepAliveSeconds = 1.0f;

	private const string WeaponRegistryPath = "res://scripts/data/WeaponRegistry.tres";

	// One row per tunable number - label, slider bounds, and how to read/write it on a WeaponData.
	// Min/Max are only the SLIDER's bounds; a wider value pasted straight into the .tres from F1
	// still works, the slider just will not reach it live.
	private readonly struct Row
	{
		public readonly string Label;
		public readonly float Min;
		public readonly float Max;
		public readonly Func<WeaponData, float> Get;
		public readonly Action<WeaponData, float> Set;
		public readonly string TresName;

		public Row(string label, float min, float max, Func<WeaponData, float> get, Action<WeaponData, float> set, string tresName)
		{
			Label = label; Min = min; Max = max; Get = get; Set = set; TresName = tresName;
		}
	}

	private readonly Row[] _rows =
	{
		new("Unit size - loudness reach (m)",  1f,   80f, w => w.FireAudioUnitSize,                 (w, v) => w.FireAudioUnitSize = v,                 "FireAudioUnitSize"),
		new("Max distance - hard cutoff (m)", 10f, 1000f, w => w.FireMaxDistanceMeters,              (w, v) => w.FireMaxDistanceMeters = v,             "FireMaxDistanceMeters"),
		new("Volume trim (dB)",              -20f,   20f, w => w.FireVolumeDb,                       (w, v) => w.FireVolumeDb = v,                      "FireVolumeDb"),
		new("Directional cone, 360=off (deg)",20f,  360f, w => w.FireEmissionAngleDegrees,           (w, v) => w.FireEmissionAngleDegrees = v,          "FireEmissionAngleDegrees"),
		new("Off-axis attenuation (dB)",     -30f,    0f, w => w.FireEmissionOffAxisAttenuationDb,   (w, v) => w.FireEmissionOffAxisAttenuationDb = v,  "FireEmissionOffAxisAttenuationDb"),
	};

	private WeaponAttachment _attachment = null!;
	private CharacterBody3D _player = null!;
	private WeaponSwitcher? _switcher;
	private WeaponRegistry? _registry;

	private CanvasLayer _canvas = null!;
	private PanelContainer _panel = null!;
	private Label _panelTitle = null!;
	private Label _resultLabel = null!;
	private Label _netLabel = null!;
	private readonly HSlider[] _sliders = new HSlider[5];
	private readonly LineEdit[] _fields = new LineEdit[5];
	private CheckBox _duckCheck = null!;
	private CheckBox _delayCheck = null!;

	private bool _active;
	private bool _syncingUi;
	private bool _togglePressedLastFrame;
	private bool _printPressedLastFrame;
	private bool _resetPressedLastFrame;
	private string? _lastWeaponName;
	private string _lastBlock = string.Empty;

	private bool _switcherWasProcessing = true;
	private bool _playerWasProcessingUnhandledInput = true;

	private float _syncCooldown;
	private float _keepAliveCountdown;
	private float[]? _lastSent; // 5 float rows + 2 bools packed as 0/1, in _rows order then duck,delay
	private string? _lastSentWeapon;

	// Snapshot of every weapon's sound fields exactly as WeaponRegistry.tres had them, taken once
	// up front before any tuning can happen. F9 (Reset) restores from this instead of re-reading
	// the file, which would just hand back the same live, already-mutated Resource the whole game
	// is sharing rather than the numbers that were actually saved to disk.
	private readonly System.Collections.Generic.Dictionary<string, float[]> _originalValues = new();

	public override void _Ready()
	{
		_player = (CharacterBody3D)GetParent();
		_attachment = GetNode<WeaponAttachment>("../Armature/WeaponAttachment");
		_switcher = GetNodeOrNull<WeaponSwitcher>("../WeaponSwitcher");
		_registry = ResourceLoader.Load<WeaponRegistry>(WeaponRegistryPath);

		// Never arms on a remote copy of another player, and never in a release export - same
		// guard WeaponGripTuner uses, for the same reason: this is a development instrument.
		bool enabled = OS.IsDebugBuild() && _player.IsMultiplayerAuthority();
		SetProcess(enabled);
		if (!enabled) return;

		if (_registry != null)
			foreach (var w in _registry.Weapons)
				_originalValues[w.WeaponName] = ReadCurrent(w);

		BuildOverlay();
	}

	public override void _Process(double delta)
	{
		HandleToggle();
		if (!_active) return;

		if (TrackWeaponChange()) SyncUiFromWeapon();
		HandlePrint();
		HandleReset();
		SyncSoundToPeers((float)delta);
		if (_active) UpdateResultLabel();
	}

	// -------------------------------------------------------------------------------------------
	// Open / close
	// -------------------------------------------------------------------------------------------

	private void HandleToggle()
	{
		bool pressed = Input.IsPhysicalKeyPressed(ToggleKey);
		if (pressed && !_togglePressedLastFrame) SetActive(!_active);
		_togglePressedLastFrame = pressed;
	}

	private void SetActive(bool active)
	{
		if (_active == active) return;
		_active = active;
		_canvas.Visible = active;

		if (active)
		{
			TrackWeaponChange();
			SyncUiFromWeapon();
			SuspendGameplayInput();
		}
		else
		{
			ResumeGameplayInput();
		}
	}

	/// <summary>Stops the game reading the mouse/keyboard for gameplay while the panel is open, the
	/// same reason WeaponGripTuner's entry panel does it: a captured mouse cursor cannot drag a
	/// slider, and left-click would otherwise fire the weapon instead of pressing a button.</summary>
	private void SuspendGameplayInput()
	{
		if (_switcher != null)
		{
			_switcherWasProcessing = _switcher.IsProcessing();
			_switcher.SetProcess(false);
		}
		_playerWasProcessingUnhandledInput = _player.IsProcessingUnhandledInput();
		_player.SetProcessUnhandledInput(false);
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	private void ResumeGameplayInput()
	{
		_switcher?.SetProcess(_switcherWasProcessing);
		_player.SetProcessUnhandledInput(_playerWasProcessingUnhandledInput);
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	/// <summary>Re-reads the baseline whenever the equipped weapon changes, including the first
	/// frame the tuner is opened. Returns true when it changed.</summary>
	private bool TrackWeaponChange()
	{
		string? name = _attachment.CurrentWeapon?.WeaponName;
		if (name == _lastWeaponName) return false;
		_lastWeaponName = name;
		return true;
	}

	// -------------------------------------------------------------------------------------------
	// Print / copy / reset / test fire
	// -------------------------------------------------------------------------------------------

	private void HandlePrint()
	{
		bool pressed = Input.IsPhysicalKeyPressed(PrintKey);
		if (pressed && !_printPressedLastFrame) PrintAndCopyValues();
		_printPressedLastFrame = pressed;
	}

	private void HandleReset()
	{
		bool pressed = Input.IsPhysicalKeyPressed(ResetKey);
		if (pressed && !_resetPressedLastFrame) ResetWeapon();
		_resetPressedLastFrame = pressed;
	}

	private void PrintAndCopyValues()
	{
		var weapon = _attachment.CurrentWeapon;
		if (weapon == null) return;
		string block = BuildBlock(weapon);
		GD.Print($"--- {weapon.WeaponName} sound (paste into its block in WeaponRegistry.tres) ---\n{block}\n---");
		DisplayServer.ClipboardSet(block);
	}

	/// <summary>Discards live tuning and restores the current weapon's seven sound fields from the
	/// _Ready-time snapshot, i.e. exactly what WeaponRegistry.tres had before this session touched
	/// it. WeaponData fields live directly on the shared Resource (there is no separate "grip
	/// node" to rebuild here, unlike WeaponGripTuner's Reset), so this writes straight back onto
	/// the live instance every WeaponAttachment using this weapon already shares.</summary>
	private void ResetWeapon()
	{
		var weapon = _attachment.CurrentWeapon;
		if (weapon == null) return;
		if (!_originalValues.TryGetValue(weapon.WeaponName, out float[]? saved)) return;

		for (int i = 0; i < _rows.Length; i++) _rows[i].Set(weapon, saved[i]);
		weapon.FireCanDuckMovement = saved[5] > 0.5f;
		weapon.FireReportDelayEnabled = saved[6] > 0.5f;
		SyncUiFromWeapon();
	}

	private void TestFire() => _attachment.PlayFire();

	private static WeaponData? FindWeapon(WeaponRegistry? registry, string? name)
	{
		if (registry == null || name == null) return null;
		foreach (var w in registry.Weapons)
			if (w.WeaponName == name) return w;
		return null;
	}

	/// <summary>The seven lines that belong in the weapon's block in WeaponRegistry.tres.</summary>
	private string BuildBlock(WeaponData weapon)
	{
		var lines = new System.Text.StringBuilder();
		foreach (var row in _rows)
			lines.Append(row.TresName).Append(" = ").Append(F(row.Get(weapon))).Append('\n');
		lines.Append("FireCanDuckMovement = ").Append(weapon.FireCanDuckMovement ? "true" : "false").Append('\n');
		lines.Append("FireReportDelayEnabled = ").Append(weapon.FireReportDelayEnabled ? "true" : "false");
		return lines.ToString();
	}

	private void UpdateResultLabel()
	{
		var weapon = _attachment.CurrentWeapon;
		if (weapon == null) return;
		string block = BuildBlock(weapon);
		if (block == _lastBlock) return;
		_lastBlock = block;
		_resultLabel.Text = block;
		_panelTitle.Text = $"SOUND - {weapon.WeaponName}";
	}

	// -------------------------------------------------------------------------------------------
	// Live sync to the other players (the "duo" case)
	// -------------------------------------------------------------------------------------------

	private void SyncSoundToPeers(float delta)
	{
		_syncCooldown -= delta;
		_keepAliveCountdown -= delta;
		UpdateNetLabel();
		if (_syncCooldown > 0.0f) return;

		var weapon = _attachment.CurrentWeapon;
		if (weapon == null || Multiplayer.GetPeers().Length == 0) return;

		float[] current = ReadCurrent(weapon);
		bool changed = _lastSent == null || _lastSentWeapon != weapon.WeaponName || !SameValues(current, _lastSent);
		if (!changed && _keepAliveCountdown > 0.0f) return;

		_lastSent = current;
		_lastSentWeapon = weapon.WeaponName;
		_syncCooldown = SyncIntervalSeconds;
		_keepAliveCountdown = KeepAliveSeconds;
		Rpc(MethodName.RpcSyncSound, weapon.WeaponName,
			current[0], current[1], current[2], current[3], current[4],
			current[5] > 0.5f, current[6] > 0.5f);
	}

	private float[] ReadCurrent(WeaponData weapon)
	{
		var values = new float[7];
		for (int i = 0; i < _rows.Length; i++) values[i] = _rows[i].Get(weapon);
		values[5] = weapon.FireCanDuckMovement ? 1f : 0f;
		values[6] = weapon.FireReportDelayEnabled ? 1f : 0f;
		return values;
	}

	private static bool SameValues(float[] a, float[] b)
	{
		for (int i = 0; i < a.Length; i++)
			if (Mathf.Abs(a[i] - b[i]) > 0.0001f) return false;
		return true;
	}

	private void UpdateNetLabel()
	{
		int peers = Multiplayer.GetPeers().Length;
		_netLabel.Text = peers > 0 ? $"Live-syncing to {peers} other player(s)" : "No other players connected - not syncing";
	}

	/// <summary>Runs on every OTHER peer: applies the tuned numbers to THIS process's own copy of
	/// the named weapon. WeaponData fields live on a Resource cached per-process by
	/// ResourceLoader, so every WeaponAttachment on this peer that fires this weapon (whoever is
	/// holding it) immediately uses the new numbers too - which is correct, the same gun should
	/// sound the same regardless of who is holding it.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	public void RpcSyncSound(string weaponName, float unitSize, float maxDistance, float volumeDb,
		float emissionAngle, float emissionOffAxisDb, bool canDuck, bool reportDelay)
	{
		if (!OS.IsDebugBuild()) return;
		if (Multiplayer.GetRemoteSenderId() != _player.GetMultiplayerAuthority()) return;
		var weapon = FindWeapon(_registry, weaponName);
		if (weapon == null) return;

		weapon.FireAudioUnitSize = unitSize;
		weapon.FireMaxDistanceMeters = maxDistance;
		weapon.FireVolumeDb = volumeDb;
		weapon.FireEmissionAngleDegrees = emissionAngle;
		weapon.FireEmissionOffAxisAttenuationDb = emissionOffAxisDb;
		weapon.FireCanDuckMovement = canDuck;
		weapon.FireReportDelayEnabled = reportDelay;

		if (_attachment.CurrentWeapon?.WeaponName == weaponName) SyncUiFromWeapon();
	}

	// -------------------------------------------------------------------------------------------
	// UI <-> WeaponData
	// -------------------------------------------------------------------------------------------

	private void SyncUiFromWeapon()
	{
		var weapon = _attachment.CurrentWeapon;
		_syncingUi = true;
		_panelTitle.Text = $"SOUND - {weapon?.WeaponName ?? "(no weapon)"}";
		for (int i = 0; i < _rows.Length; i++)
		{
			float v = weapon == null ? _rows[i].Min : _rows[i].Get(weapon);
			_sliders[i].Value = v;
			_fields[i].Text = F(v);
		}
		_duckCheck.ButtonPressed = weapon?.FireCanDuckMovement ?? true;
		_delayCheck.ButtonPressed = weapon?.FireReportDelayEnabled ?? true;
		_syncingUi = false;
		_lastBlock = string.Empty; // force UpdateResultLabel to refresh even if numbers round the same
	}

	private void OnSliderChanged(int index, double value)
	{
		if (_syncingUi) return;
		var weapon = _attachment.CurrentWeapon;
		if (weapon == null) return;
		_rows[index].Set(weapon, (float)value);
		_syncingUi = true;
		_fields[index].Text = F((float)value);
		_syncingUi = false;
	}

	private void OnFieldSubmitted(int index, string text)
	{
		if (_syncingUi) return;
		var weapon = _attachment.CurrentWeapon;
		if (weapon == null) return;
		if (!TryParse(text, out float value)) { SyncUiFromWeapon(); return; }
		value = Mathf.Clamp(value, _rows[index].Min, _rows[index].Max);
		_rows[index].Set(weapon, value);
		_syncingUi = true;
		_sliders[index].Value = value;
		_fields[index].Text = F(value);
		_syncingUi = false;
	}

	private void OnDuckToggled(bool pressed)
	{
		if (_syncingUi) return;
		var weapon = _attachment.CurrentWeapon;
		if (weapon != null) weapon.FireCanDuckMovement = pressed;
	}

	private void OnDelayToggled(bool pressed)
	{
		if (_syncingUi) return;
		var weapon = _attachment.CurrentWeapon;
		if (weapon != null) weapon.FireReportDelayEnabled = pressed;
	}

	private static bool TryParse(string text, out float value)
	{
		string cleaned = text.Trim().Replace(',', '.');
		return float.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
	}

	private static string F(float v)
	{
		string s = v.ToString("0.###", CultureInfo.InvariantCulture);
		return s == "-0" ? "0" : s;
	}

	// -------------------------------------------------------------------------------------------
	// UI construction
	// -------------------------------------------------------------------------------------------

	private void BuildOverlay()
	{
		_canvas = new CanvasLayer { Name = "WeaponSoundTunerOverlay", Visible = false };
		AddChild(_canvas);

		_panel = new PanelContainer { Name = "SoundTunerPanel" };
		_panel.AnchorLeft = 1.0f;
		_panel.AnchorRight = 1.0f;
		_panel.AnchorTop = 0.0f;
		_panel.AnchorBottom = 0.0f;
		_panel.OffsetLeft = -420.0f;
		_panel.OffsetRight = -24.0f;
		_panel.OffsetTop = 24.0f;
		_panel.OffsetBottom = 24.0f;
		_panel.GrowHorizontal = Control.GrowDirection.Begin;
		_panel.GrowVertical = Control.GrowDirection.End;
		_canvas.AddChild(_panel);

		var margin = new MarginContainer();
		foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
			margin.AddThemeConstantOverride(side, 12);
		_panel.AddChild(margin);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 8);
		margin.AddChild(column);

		_panelTitle = new Label { Text = "SOUND" };
		column.AddChild(_panelTitle);

		for (int i = 0; i < _rows.Length; i++)
			BuildRow(column, i);

		_duckCheck = new CheckBox { Text = "Ducks footsteps/movement when fired" };
		_duckCheck.Toggled += OnDuckToggled;
		column.AddChild(_duckCheck);

		_delayCheck = new CheckBox { Text = "Distant listeners hear a delayed report" };
		_delayCheck.Toggled += OnDelayToggled;
		column.AddChild(_delayCheck);

		column.AddChild(new Label { Text = "Result (paste into WeaponRegistry.tres):" });
		_resultLabel = new Label
		{
			CustomMinimumSize = new Vector2(370.0f, 0.0f),
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			Modulate = new Color(1.0f, 0.95f, 0.6f),
		};
		column.AddChild(_resultLabel);

		var buttons = new HBoxContainer();
		buttons.AddThemeConstantOverride("separation", 6);
		column.AddChild(buttons);
		buttons.AddChild(CreateButton("Test fire", TestFire));
		buttons.AddChild(CreateButton("Copy (F1)", PrintAndCopyValues));
		buttons.AddChild(CreateButton("Reset (F9)", ResetWeapon));
		buttons.AddChild(CreateButton("Close (F6)", () => SetActive(false)));

		_netLabel = new Label { Modulate = new Color(1.0f, 1.0f, 1.0f, 0.65f) };
		column.AddChild(_netLabel);

		var hint = new Label
		{
			Text = "Drag a slider or type an exact number. Bigger unit size/max distance = carries\nfurther. Directional cone 360 = omnidirectional (off).",
			Modulate = new Color(1.0f, 1.0f, 1.0f, 0.55f),
		};
		hint.AddThemeFontSizeOverride("font_size", 12);
		column.AddChild(hint);
	}

	private void BuildRow(VBoxContainer column, int index)
	{
		Row row = _rows[index];
		column.AddChild(new Label { Text = row.Label });

		var line = new HBoxContainer();
		line.AddThemeConstantOverride("separation", 6);
		column.AddChild(line);

		var slider = new HSlider
		{
			MinValue = row.Min,
			MaxValue = row.Max,
			Step = (row.Max - row.Min) / 200.0,
			CustomMinimumSize = new Vector2(260.0f, 0.0f),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		slider.ValueChanged += value => OnSliderChanged(index, value);
		_sliders[index] = slider;
		line.AddChild(slider);

		var field = new LineEdit { CustomMinimumSize = new Vector2(70.0f, 0.0f) };
		field.TextSubmitted += text => OnFieldSubmitted(index, text);
		field.FocusExited += () => OnFieldSubmitted(index, field.Text);
		_fields[index] = field;
		line.AddChild(field);
	}

	private static Button CreateButton(string text, Action onPressed)
	{
		// No keyboard focus: otherwise Space/Enter would press the button again after a click,
		// same reasoning as WeaponGripTuner's buttons.
		var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
		button.Pressed += onPressed;
		return button;
	}
}
