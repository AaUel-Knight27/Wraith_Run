using Godot;
using System.Globalization;

/// <summary>
/// Live in-game calibrator for how a weapon sits in the hand. Development tool only: it arms for
/// the local player, only touches the visual Grip node, and is inert in release exports.
///
/// LIVE SYNC: while the tuner is open (F3), every change is also sent to the other players, so a
/// second window (host + client) shows the weapon in your hand exactly as you edit it - from their
/// point of view. Only the cosmetic grip numbers are sent, never any gameplay state.
///
/// F3  Open / close the tuner. While it is open the keyboard nudge keys work as they always did
///     (arrows, PgUp/PgDn, [ ], comma/period, semicolon/quote, minus/equal, Shift = coarse).
/// F7  Open / close the NUMBER ENTRY panel. Type Position, Rotation and Scale into the boxes and
///     the weapon updates live. The exact lines to paste into WeaponRegistry.tres are shown under
///     the boxes. Tab or Enter jumps to the next box. Nudge keys are paused while it is open,
///     because "-" and "." are typed characters there, not commands.
/// F4  Print the current values to the console AND copy them to the clipboard.
/// F5  Discard changes and rebuild the weapon from the values in WeaponRegistry.tres.
///
/// While the entry panel is open the player's mouse look, weapon switching and firing are paused.
/// Without that, typing a digit 1-8 would switch weapons and clicking a box would shoot.
/// </summary>
public partial class WeaponGripTuner : Node
{
	private const Key ToggleKey = Key.F3;
	private const Key PrintKey = Key.F4;
	private const Key ResetKey = Key.F5;
	private const Key EntryKey = Key.F7;

	// Position in metres, rotation in degrees, scale as a multiplier - all per key-repeat while a
	// key is held. Hold Shift for the coarse step to close in fast, then fine-tune unshifted.
	private const float PositionStepFine = 0.001f;
	private const float PositionStepCoarse = 0.01f;
	private const float RotationStepFine = 0.5f;
	private const float RotationStepCoarse = 5.0f;
	private const float ScaleStepFine = 0.005f;
	private const float ScaleStepCoarse = 0.05f;

	private const string ModelNodeName = "EquippedWeapon";
	private const float MinModelScale = 0.001f;

	// Live sync: changes are sent at most this often, and the full state is re-sent this often
	// even when nothing changed, so a player who joins late (or whose copy of you had not spawned
	// yet when the first message went out) catches up on its own.
	private const float SyncIntervalSeconds = 0.05f;
	private const float KeepAliveSeconds = 1.0f;

	// Text-box layout: 0-2 = position X/Y/Z, 3-5 = rotation X/Y/Z, 6 = scale.
	private const int FieldCount = 7;
	private const int ScaleFieldIndex = 6;

	private static readonly Color InvalidTint = new Color(1.0f, 0.55f, 0.55f);

	private WeaponAttachment _attachment = null!;
	private CharacterBody3D _player = null!;
	private WeaponSwitcher? _switcher;
	private CanvasLayer _canvas = null!;
	private Label _label = null!;
	private PanelContainer _panel = null!;
	private Label _panelTitle = null!;
	private Label _resultLabel = null!;
	private readonly LineEdit[] _fields = new LineEdit[FieldCount];

	private bool _active;
	private bool _panelOpen;
	private bool _syncingFields;
	private bool _togglePressedLastFrame;
	private bool _entryPressedLastFrame;
	private bool _printPressedLastFrame;
	private bool _resetPressedLastFrame;
	private string? _lastWeaponName;
	private float _baselineScale = 1.0f;
	private string _lastBlock = string.Empty;

	private float _syncCooldown;
	private float _keepAliveCountdown;
	private float[]? _lastSent;
	private string? _lastSentWeapon;

	// Player state remembered while the entry panel is open, so closing it puts things back.
	private bool _switcherWasProcessing = true;
	private bool _playerWasProcessingUnhandledInput = true;

	public override void _Ready()
	{
		_player = (CharacterBody3D)GetParent();
		_attachment = GetNode<WeaponAttachment>("../Armature/WeaponAttachment");
		_switcher = GetNodeOrNull<WeaponSwitcher>("../WeaponSwitcher");

		// Never arms on a remote copy of another player (there is nothing here for it to tune, and
		// no reason to build UI for it), and never in a release export, where a player pressing F3
		// could otherwise reshape their weapon in everyone else's game.
		bool enabled = OS.IsDebugBuild() && _player.IsMultiplayerAuthority();
		SetProcess(enabled);
		if (!enabled) return;

		BuildOverlay();
	}

	public override void _Process(double delta)
	{
		HandleToggle();
		HandleEntryToggle();
		if (!_active) return;

		bool weaponChanged = TrackWeaponChange();
		if (_panelOpen)
		{
			if (weaponChanged) SyncFieldsFromGrip();
		}
		else
		{
			HandleAdjustment((float)delta);
		}

		HandlePrint();
		HandleReset();
		SyncGripToPeers((float)delta);
		UpdateLabel();
		if (_panelOpen) UpdateResultLabel();
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

	private void HandleEntryToggle()
	{
		bool pressed = Input.IsPhysicalKeyPressed(EntryKey);
		if (pressed && !_entryPressedLastFrame) SetPanelOpen(!_panelOpen);
		_entryPressedLastFrame = pressed;
	}

	private void SetActive(bool active)
	{
		if (_active == active) return;
		if (!active) SetPanelOpen(false);
		_active = active;
		_canvas.Visible = active;
		if (active) TrackWeaponChange();
	}

	private void SetPanelOpen(bool open)
	{
		if (_panelOpen == open) return;

		if (open)
		{
			SetActive(true);
			_panelOpen = true;
			TrackWeaponChange();
			SyncFieldsFromGrip();
			_panel.Visible = true;
			SuspendGameplayInput();
			// Deferred so the panel has been laid out before it takes focus. Focusing the first
			// box means you can start typing straight after pressing F7.
			Callable.From(() => { if (_panelOpen) _fields[0].GrabFocus(); }).CallDeferred();
		}
		else
		{
			_panelOpen = false;
			_panel.Visible = false;
			foreach (LineEdit field in _fields) field.ReleaseFocus();
			ResumeGameplayInput();
		}
	}

	/// <summary>Stops the game reading the keyboard and mouse for gameplay while you type. Input
	/// is polled globally by WeaponSwitcher (digits 1-8 pick a weapon, left click fires), so a
	/// focused text box does not protect against it - the polling has to be switched off.</summary>
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

	/// <summary>Re-reads the baseline whenever the equipped weapon changes, including the very
	/// first frame tuning is turned on - otherwise the first nudge would jump from whatever stale
	/// baseline was left over from the previous weapon. Returns true when it changed.</summary>
	private bool TrackWeaponChange()
	{
		string? name = _attachment.CurrentWeapon?.WeaponName;
		if (name == _lastWeaponName) return false;
		_lastWeaponName = name;
		_baselineScale = _attachment.CurrentWeapon?.ModelScale ?? 1.0f;
		return true;
	}

	// -------------------------------------------------------------------------------------------
	// Keyboard nudging (unchanged behaviour)
	// -------------------------------------------------------------------------------------------

	private void HandleAdjustment(float delta)
	{
		var grip = _attachment.CurrentGrip;
		if (grip == null) return;

		bool coarse = Input.IsPhysicalKeyPressed(Key.Shift);
		float posStep = (coarse ? PositionStepCoarse : PositionStepFine) * 60.0f * delta;
		float rotStep = (coarse ? RotationStepCoarse : RotationStepFine) * 60.0f * delta;
		float scaleStep = (coarse ? ScaleStepCoarse : ScaleStepFine) * 60.0f * delta;

		// Position: arrows move X/Z, Page Up/Down move Y. This is the grip node's own local frame
		// (the hand bone's frame - see WeaponData's notes), not world space.
		var pos = grip.Position;
		if (Input.IsPhysicalKeyPressed(Key.Left)) pos.X -= posStep;
		if (Input.IsPhysicalKeyPressed(Key.Right)) pos.X += posStep;
		if (Input.IsPhysicalKeyPressed(Key.Up)) pos.Z -= posStep;
		if (Input.IsPhysicalKeyPressed(Key.Down)) pos.Z += posStep;
		if (Input.IsPhysicalKeyPressed(Key.Pageup)) pos.Y += posStep;
		if (Input.IsPhysicalKeyPressed(Key.Pagedown)) pos.Y -= posStep;
		grip.Position = pos;

		// Rotation: bracket keys for yaw (the one you'll touch most - it is what points the barrel
		// down the sight line), comma/period for pitch, semicolon/quote for roll.
		var rot = grip.RotationDegrees;
		if (Input.IsPhysicalKeyPressed(Key.Bracketleft)) rot.Y -= rotStep;
		if (Input.IsPhysicalKeyPressed(Key.Bracketright)) rot.Y += rotStep;
		if (Input.IsPhysicalKeyPressed(Key.Comma)) rot.X -= rotStep;
		if (Input.IsPhysicalKeyPressed(Key.Period)) rot.X += rotStep;
		if (Input.IsPhysicalKeyPressed(Key.Semicolon)) rot.Z -= rotStep;
		if (Input.IsPhysicalKeyPressed(Key.Apostrophe)) rot.Z += rotStep;
		grip.RotationDegrees = rot;

		// Scale: Minus/Equal. Uniform - none of these models need non-uniform scaling once the
		// right axis mapping is chosen.
		float scaleDelta = 0.0f;
		if (Input.IsPhysicalKeyPressed(Key.Minus)) scaleDelta -= scaleStep;
		if (Input.IsPhysicalKeyPressed(Key.Equal)) scaleDelta += scaleStep;
		if (scaleDelta != 0.0f) SetModelScale(Mathf.Max(MinModelScale, _baselineScale + scaleDelta));
	}

	private void SetModelScale(float scale)
	{
		_baselineScale = scale;
		ApplyModelScale(scale);
	}

	/// <summary>Scales the weapon model only. Split out so the network receiver can use it without
	/// touching the tuner's own baseline.</summary>
	private void ApplyModelScale(float scale)
	{
		var grip = _attachment.CurrentGrip;
		if (grip == null) return;
		foreach (Node child in grip.GetChildren())
			if (child is Node3D model && child.Name == ModelNodeName)
				model.Scale = Vector3.One * scale;
	}

	// -------------------------------------------------------------------------------------------
	// Print / copy / reset
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

	private void ResetWeapon()
	{
		var weapon = _attachment.CurrentWeapon;
		if (weapon == null) return;
		// Re-equip the same WeaponData, which rebuilds the Grip node straight from its
		// registry values - the simplest way to discard a session gone wrong. Clearing the
		// tracked name makes the next frame re-read the baseline (and refresh the entry boxes).
		_attachment.Equip(weapon);
		_baselineScale = weapon.ModelScale;
		_lastWeaponName = null;
	}

	private void PrintAndCopyValues()
	{
		var weapon = _attachment.CurrentWeapon;
		if (weapon == null) return;
		string block = BuildBlock();
		GD.Print($"--- {weapon.WeaponName} grip (paste into scripts/data/WeaponRegistry.tres) ---\n{block}\n---");
		// The clipboard gets only the three lines - no header - so it can be pasted as is.
		DisplayServer.ClipboardSet(block);
	}

	/// <summary>The three lines that belong in the weapon's block in WeaponRegistry.tres.</summary>
	private string BuildBlock()
	{
		var grip = _attachment.CurrentGrip;
		if (grip == null) return string.Empty;
		Vector3 p = _attachment.GripPositionForData;
		Vector3 r = grip.RotationDegrees;
		return
			$"GripPosition = Vector3({F(p.X)}, {F(p.Y)}, {F(p.Z)})\n" +
			$"GripRotationDegrees = Vector3({F(r.X)}, {F(r.Y)}, {F(r.Z)})\n" +
			$"ModelScale = {F(_baselineScale)}";
	}

	// -------------------------------------------------------------------------------------------
	// Live sync to the other players
	// -------------------------------------------------------------------------------------------

	/// <summary>Sends the weapon's current grip to every other peer when it changed (rate limited),
	/// plus a periodic refresh. Runs on the authority only, while the tuner is open.</summary>
	private void SyncGripToPeers(float delta)
	{
		_syncCooldown -= delta;
		_keepAliveCountdown -= delta;
		if (_syncCooldown > 0.0f) return;

		var weapon = _attachment.CurrentWeapon;
		if (weapon == null || _attachment.CurrentGrip == null) return;
		if (Multiplayer.GetPeers().Length == 0) return;

		float[] current = ReadCurrent();
		bool changed = _lastSent == null
			|| _lastSentWeapon != weapon.WeaponName
			|| !SameValues(current, _lastSent);
		if (!changed && _keepAliveCountdown > 0.0f) return;

		_lastSent = current;
		_lastSentWeapon = weapon.WeaponName;
		_syncCooldown = SyncIntervalSeconds;
		_keepAliveCountdown = KeepAliveSeconds;
		Rpc(MethodName.RpcSyncGrip,
			weapon.WeaponName,
			new Vector3(current[0], current[1], current[2]),
			new Vector3(current[3], current[4], current[5]),
			current[ScaleFieldIndex]);
	}

	private static bool SameValues(float[] a, float[] b)
	{
		for (int i = 0; i < a.Length; i++)
			if (Mathf.Abs(a[i] - b[i]) > 0.00001f) return false;
		return true;
	}

	/// <summary>Runs on every OTHER peer: puts the owner's tuned grip onto this peer's copy of that
	/// player, so the weapon is seen in their hand exactly as they are editing it. Reliable, so the
	/// final value of a quick edit is never the one that got dropped. Ignored unless it comes from
	/// the player's own owner, and unless both sides have the same weapon equipped.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	public void RpcSyncGrip(string weaponName, Vector3 dataPosition, Vector3 rotationDegrees, float modelScale)
	{
		if (!OS.IsDebugBuild()) return;
		if (Multiplayer.GetRemoteSenderId() != _player.GetMultiplayerAuthority()) return;
		var grip = _attachment.CurrentGrip;
		if (grip == null || _attachment.CurrentWeapon?.WeaponName != weaponName) return;

		// Same unit correction as ApplyFieldsToWeapon: the number is in WeaponData units, the
		// node holds it multiplied by the skeleton-scale fix that Equip() stored in grip.Scale.
		grip.Position = dataPosition * grip.Scale;
		grip.RotationDegrees = rotationDegrees;
		ApplyModelScale(Mathf.Max(MinModelScale, modelScale));
	}

	// -------------------------------------------------------------------------------------------
	// Number entry
	// -------------------------------------------------------------------------------------------

	/// <summary>Current applied values in box order (position, rotation, scale). Position is in
	/// WeaponData units, i.e. what belongs in the .tres, not the skeleton-scaled node position.</summary>
	private float[] ReadCurrent()
	{
		var grip = _attachment.CurrentGrip;
		if (grip == null) return new float[FieldCount];
		Vector3 p = _attachment.GripPositionForData;
		Vector3 r = grip.RotationDegrees;
		return new[] { p.X, p.Y, p.Z, r.X, r.Y, r.Z, _baselineScale };
	}

	/// <summary>Fills every box from what is currently on the weapon.</summary>
	private void SyncFieldsFromGrip()
	{
		_syncingFields = true;
		var grip = _attachment.CurrentGrip;
		_panelTitle.Text = $"GRIP VALUES - {_attachment.CurrentWeapon?.WeaponName ?? "(no weapon)"}";
		float[] values = ReadCurrent();
		for (int i = 0; i < FieldCount; i++)
		{
			_fields[i].Text = grip == null ? string.Empty : FieldText(values[i]);
			_fields[i].Modulate = Colors.White;
		}
		_syncingFields = false;
	}

	/// <summary>Pushes whatever is typed in the boxes onto the weapon. A box that does not parse
	/// (empty, or half-typed like "-") is tinted red and its old value is kept, so the weapon never
	/// jumps while you are in the middle of typing a number.</summary>
	private void ApplyFieldsToWeapon()
	{
		var grip = _attachment.CurrentGrip;
		if (grip == null) return;

		float[] v = ReadCurrent();
		for (int i = 0; i < FieldCount; i++)
		{
			bool ok = TryParse(_fields[i].Text, out float parsed);
			_fields[i].Modulate = ok ? Colors.White : InvalidTint;
			if (ok) v[i] = parsed;
		}

		// Position is typed in WeaponData units. The node holds it multiplied by the skeleton-scale
		// correction that Equip() stored in grip.Scale, so apply the same multiplication here.
		grip.Position = new Vector3(v[0], v[1], v[2]) * grip.Scale;
		grip.RotationDegrees = new Vector3(v[3], v[4], v[5]);
		SetModelScale(Mathf.Max(MinModelScale, v[ScaleFieldIndex]));
	}

	private void OnFieldEdited()
	{
		if (_syncingFields || !_panelOpen) return;
		ApplyFieldsToWeapon();
	}

	private void OnFieldSubmitted(int index)
	{
		if (_syncingFields || !_panelOpen) return;
		ApplyFieldsToWeapon();
		NormalizeField(index);
		_fields[index].FindNextValidFocus()?.GrabFocus();
	}

	private void OnFieldFocusExited(int index)
	{
		if (_syncingFields || !_panelOpen) return;
		NormalizeField(index);
	}

	/// <summary>Rewrites a box to the value actually applied, tidying "0.0233000" or "1e-2" and
	/// reverting a box that was left invalid. Not done while typing, or "0." would lose its dot.</summary>
	private void NormalizeField(int index)
	{
		_syncingFields = true;
		_fields[index].Text = FieldText(ReadCurrent()[index]);
		_fields[index].Modulate = Colors.White;
		_syncingFields = false;
	}

	private void UpdateResultLabel()
	{
		string block = BuildBlock();
		if (block == _lastBlock) return;
		_lastBlock = block;
		_resultLabel.Text = block;
	}

	private static bool TryParse(string text, out float value)
	{
		// A comma is accepted as a decimal point so a stray "0,0233" still works.
		string cleaned = text.Trim().Replace(',', '.');
		return float.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
			&& float.IsFinite(value);
	}

	// -------------------------------------------------------------------------------------------
	// Formatting
	// -------------------------------------------------------------------------------------------

	/// <summary>Four decimals, the precision the .tres values are stored at.</summary>
	private static string F(float v) => Format(v, "0.####");

	/// <summary>Six decimals for the entry boxes, so re-applying every box on each keystroke does
	/// not round away digits you typed.</summary>
	private static string FieldText(float v) => Format(v, "0.######");

	private static string Format(float v, string format)
	{
		string s = v.ToString(format, CultureInfo.InvariantCulture);
		return s == "-0" ? "0" : s;
	}

	private void UpdateLabel()
	{
		var weapon = _attachment.CurrentWeapon;
		Vector3 p = _attachment.GripPositionForData;
		Vector3 r = _attachment.CurrentGrip?.RotationDegrees ?? Vector3.Zero;
		string keys = _panelOpen
			? "Entry panel open - nudge keys are paused.\nF7: close panel"
			: "F7: type exact values\n" +
			  "Arrows: move X/Z   PgUp/PgDn: move Y\n" +
			  "[ ]: yaw   , .: pitch   ; ': roll\n" +
			  "- =: scale   Shift: coarse step";
		int peers = Multiplayer.GetPeers().Length;
		string net = peers > 0
			? $"Live-syncing to {peers} other player(s)"
			: "No other players connected - not syncing";
		_label.Text =
			$"WEAPON GRIP TUNER - {weapon?.WeaponName ?? "(no weapon)"}\n" +
			$"Pos  X {F(p.X)}  Y {F(p.Y)}  Z {F(p.Z)}\n" +
			$"Rot  X {F(r.X)}  Y {F(r.Y)}  Z {F(r.Z)}\n" +
			$"Scale {F(_baselineScale)}\n" +
			"\n" +
			keys + "\n" +
			"F4: print + copy values   F5: reset weapon\n" +
			net + "\n" +
			"F3: close";
	}

	// -------------------------------------------------------------------------------------------
	// UI construction
	// -------------------------------------------------------------------------------------------

	private void BuildOverlay()
	{
		_canvas = new CanvasLayer { Name = "WeaponGripTunerOverlay", Visible = false };
		AddChild(_canvas);
		_label = new Label
		{
			Position = new Vector2(24, 24),
			Modulate = new Color(1.0f, 0.95f, 0.6f),
		};
		_label.AddThemeFontSizeOverride("font_size", 16);
		_label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f));
		_label.AddThemeConstantOverride("shadow_offset_x", 1);
		_label.AddThemeConstantOverride("shadow_offset_y", 1);
		_canvas.AddChild(_label);

		BuildEntryPanel();
	}

	private void BuildEntryPanel()
	{
		// Anchored to the top-right corner and growing leftwards, so it never covers the readout
		// in the top-left and does not depend on the window size.
		_panel = new PanelContainer { Name = "GripEntryPanel", Visible = false };
		_panel.AnchorLeft = 1.0f;
		_panel.AnchorRight = 1.0f;
		_panel.AnchorTop = 0.0f;
		_panel.AnchorBottom = 0.0f;
		_panel.OffsetLeft = -24.0f;
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

		_panelTitle = new Label { Text = "GRIP VALUES" };
		column.AddChild(_panelTitle);

		var grid = new GridContainer { Columns = 4 };
		grid.AddThemeConstantOverride("h_separation", 6);
		grid.AddThemeConstantOverride("v_separation", 6);
		column.AddChild(grid);

		grid.AddChild(new Label());
		foreach (string axis in new[] { "X", "Y", "Z" })
			grid.AddChild(new Label { Text = axis, HorizontalAlignment = HorizontalAlignment.Center });

		AddFieldRow(grid, "Position", 0, 3);
		AddFieldRow(grid, "Rotation", 3, 3);
		AddFieldRow(grid, "Scale", ScaleFieldIndex, 1);

		column.AddChild(new Label { Text = "Result (paste into WeaponRegistry.tres):" });
		_resultLabel = new Label
		{
			CustomMinimumSize = new Vector2(360.0f, 0.0f),
			Modulate = new Color(1.0f, 0.95f, 0.6f),
		};
		column.AddChild(_resultLabel);

		var buttons = new HBoxContainer();
		buttons.AddThemeConstantOverride("separation", 6);
		column.AddChild(buttons);
		buttons.AddChild(CreateButton("Copy result (F4)", PrintAndCopyValues));
		buttons.AddChild(CreateButton("Reset (F5)", ResetWeapon));
		buttons.AddChild(CreateButton("Close (F7)", () => SetPanelOpen(false)));

		var hint = new Label
		{
			Text = "Tab / Enter: next box   Values apply as you type",
			Modulate = new Color(1.0f, 1.0f, 1.0f, 0.65f),
		};
		column.AddChild(hint);
	}

	private void AddFieldRow(GridContainer grid, string title, int firstIndex, int count)
	{
		grid.AddChild(new Label { Text = title });
		for (int i = 0; i < count; i++)
			grid.AddChild(CreateField(firstIndex + i));
		// Pad short rows (Scale has one box) so the grid columns stay aligned.
		for (int i = count; i < 3; i++)
			grid.AddChild(new Control());
	}

	private LineEdit CreateField(int index)
	{
		var field = new LineEdit
		{
			CustomMinimumSize = new Vector2(88.0f, 0.0f),
			SelectAllOnFocus = true,
		};
		field.TextChanged += _ => OnFieldEdited();
		field.TextSubmitted += _ => OnFieldSubmitted(index);
		field.FocusExited += () => OnFieldFocusExited(index);
		_fields[index] = field;
		return field;
	}

	private static Button CreateButton(string text, System.Action onPressed)
	{
		// No keyboard focus: otherwise Space or Enter would press the button again after a click.
		var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
		button.Pressed += onPressed;
		return button;
	}
}