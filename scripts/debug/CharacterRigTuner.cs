using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

/// <summary>
/// Live in-game armature/mesh/camera editor for Manny's rig. Development tool only: it arms for the
/// local player and is inert in release exports, the same rule WeaponGripTuner (F3), WeaponSoundTuner
/// (F6) and AnimationTuner (F8) already follow. Unlike those three, this is an RPC-bearing tool like
/// WeaponGripTuner/WeaponSoundTuner, so it has to be a real node in Player.tscn (not spawned in code
/// like AnimationTuner) - see the integration notes shipped alongside this file.
///
/// F2  Open / close the editor. Opening it pauses mouse-look and weapon-switch digit keys (typing "5"
///     into a number box would otherwise switch to weapon slot 5) - the same trade-off WeaponGripTuner's
///     F7 panel makes. Walking, crouching, sprinting and aiming keep working (they are polled every
///     physics frame, not delivered as the input events that got paused), so you can hold the actual
///     pose you are tuning. Fire and Reload are one-shots you cannot casually "hold", so this panel has
///     dedicated preview buttons for them instead.
///
/// WHAT YOU ARE ACTUALLY EDITING: every field here reads and writes CharacterRigTuning directly - the
/// same static data CharacterRigOverrideModifier and CharacterRigCameraScaleApplier read every frame
/// for every player, in every build. There is no separate "preview" copy: typing a number changes the
/// game's actual live pose immediately, for you and (once synced) for everyone else too. The "editing
/// mode" dropdown only chooses which PoseMode bucket a change is filed under - what you actually SEE is
/// always whichever bucket is really active for you right now (see CharacterRigTuning.
/// ActiveLayersHighToLow), exactly like it would in a shipped build.
///
/// LIVE SYNC: while open, whichever single target (bone/camera/scale, in whichever mode) is currently
/// selected is sent to every other peer, at most every SyncIntervalSeconds and at least every
/// KeepAliveSeconds - the same rate-limit-plus-keepalive shape WeaponGripTuner's SyncGripToPeers uses,
/// so a peer that joins mid-session still converges. A target you edited earlier and then navigated
/// away from stays wherever you left it (it was already applied and already sent), so this only ever
/// needs to track the one target you are looking at right now.
///
/// PERMANENCE: "Copy / Print ALL" builds the SetBone/SetCamera/SetCharacterScale lines for every
/// non-identity value across every mode, ready to paste into CharacterRigTuning.LoadDefaults(). That is
/// the actual handoff - once pasted, the calibration ships in release builds, and the debug-only saved
/// session (Save/Delete buttons, character_rig_tuning.cfg) stops mattering.
/// </summary>
public partial class CharacterRigTuner : Node
{
	private const Key ToggleKey = Key.F2;
	private const float SyncIntervalSeconds = 0.05f;
	private const float KeepAliveSeconds = 1.0f;

	// Field layout: 0-2 = position X/Y/Z (metres), 3-5 = rotation X/Y/Z (degrees), 6 = scale.
	private const int FieldCount = 7;
	private const int ScaleFieldIndex = 6;

	private static readonly Color InvalidTint = new(1.0f, 0.55f, 0.55f);

	private enum TargetKind { Bone, Camera, CharacterScale }

	private CharacterBody3D _playerBody = null!;
	private PlayerMovement _movement = null!;
	private PlayerAnimationController _controller = null!;
	private Skeleton3D? _skeleton;
	private Node3D _armature = null!;
	private WeaponSwitcher? _switcher;

	private CanvasLayer _canvas = null!;
	private OptionButton _modeOption = null!;
	private LineEdit _searchField = null!;
	private VBoxContainer _targetListBox = null!;
	private readonly Dictionary<string, Button> _boneButtons = new();
	private Label _liveLabel = null!;
	private Label _targetLabel = null!;
	private Label _previewLabel = null!;
	private Control _positionRow = null!;
	private Control _rotationRow = null!;
	private Control _scaleRow = null!;
	private readonly LineEdit[] _fields = new LineEdit[FieldCount];

	private bool _active;
	private bool _togglePressedLastFrame;
	private bool _syncingFields;

	private TargetKind _currentKind = TargetKind.Camera;
	private string? _currentBoneName;
	private CharacterRigTuning.PoseMode _currentMode = CharacterRigTuning.PoseMode.Global;

	private float _syncCooldown;
	private float _keepAliveCountdown;
	private float[]? _lastSent;
	private string? _lastSentIdentity;

	// Player state remembered while the panel is open, so closing it puts things back - see
	// WeaponGripTuner's identical fields for why this has to be restored rather than just re-enabled.
	private bool _switcherWasProcessing = true;
	private bool _playerWasProcessingUnhandledInput = true;

	public override void _Ready()
	{
		_playerBody = (CharacterBody3D)GetParent();
		_movement = (PlayerMovement)_playerBody;
		_controller = GetNode<PlayerAnimationController>("../AnimationController");
		_armature = GetNode<Node3D>("../Armature");
		_skeleton = FindSkeleton(_armature);
		_switcher = GetNodeOrNull<WeaponSwitcher>("../WeaponSwitcher");

		// Defensive: PlayerAnimationController._Ready() already calls this, and node order in
		// Player.tscn puts it before this node, but EnsureLoaded() is a cheap no-op the second time.
		CharacterRigTuning.EnsureLoaded();

		// Never arms on a remote copy of another player, and never in a release export - same rule
		// WeaponGripTuner follows and for the same reason (a player pressing F2 could otherwise
		// reshape the rig in everyone else's game). Fields above are still assigned before this check
		// so RpcSyncTarget/RpcReloadAllDefaults keep working on a non-authority copy that receives them.
		bool enabled = OS.IsDebugBuild() && _playerBody.IsMultiplayerAuthority();
		SetProcess(enabled);
		if (!enabled) return;

		if (_skeleton == null)
		{
			GD.PushError("CharacterRigTuner could not find a Skeleton3D under Armature.");
			return;
		}
		BuildOverlay();
	}

	public override void _Process(double delta)
	{
		HandleToggle();
		if (!_active) return;
		SyncCurrentTargetToPeers((float)delta);
		UpdateLiveLabel();
		UpdatePreviewLabel();
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
			SuspendGameplayInput();
			SyncFieldsFromData();
			Callable.From(() => { if (_active) _searchField.GrabFocus(); }).CallDeferred();
		}
		else
		{
			ResumeGameplayInput();
		}
	}

	/// <summary>Same trade-off as WeaponGripTuner's F7 panel: digits type into number boxes instead of
	/// switching weapons, and the mouse is free to click the target list instead of looking around.
	/// WASD/crouch/sprint/aim are untouched - PlayerMovement polls Input.IsActionPressed for all of
	/// them in _PhysicsProcess, which SetProcessUnhandledInput(false) does not affect.</summary>
	private void SuspendGameplayInput()
	{
		if (_switcher != null)
		{
			_switcherWasProcessing = _switcher.IsProcessing();
			_switcher.SetProcess(false);
		}
		_playerWasProcessingUnhandledInput = _playerBody.IsProcessingUnhandledInput();
		_playerBody.SetProcessUnhandledInput(false);
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	private void ResumeGameplayInput()
	{
		_switcher?.SetProcess(_switcherWasProcessing);
		_playerBody.SetProcessUnhandledInput(_playerWasProcessingUnhandledInput);
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	// -------------------------------------------------------------------------------------------
	// Target selection
	// -------------------------------------------------------------------------------------------

	private void SelectTarget(TargetKind kind, string? boneName)
	{
		_currentKind = kind;
		_currentBoneName = boneName;
		UpdateFieldVisibility();
		SyncFieldsFromData();
	}

	private void UpdateFieldVisibility()
	{
		bool showVector = _currentKind is TargetKind.Bone or TargetKind.Camera;
		_positionRow.Visible = showVector;
		_rotationRow.Visible = showVector;
		_scaleRow.Visible = _currentKind is TargetKind.Bone or TargetKind.CharacterScale;
	}

	private void UpdateTargetLabel()
	{
		string targetName = _currentKind switch
		{
			TargetKind.Bone => _currentBoneName ?? "(none)",
			TargetKind.Camera => "Camera (first-person)",
			_ => "Whole-character scale",
		};
		_targetLabel.Text = $"Editing: {_currentMode} -> {targetName}";
	}

	private void OnSearchChanged(string text)
	{
		string needle = text.Trim().ToLowerInvariant();
		foreach (var pair in _boneButtons)
			pair.Value.Visible = needle.Length == 0 || pair.Key.ToLowerInvariant().Contains(needle);
	}

	private void OnModeSelected(long index)
	{
		_currentMode = (CharacterRigTuning.PoseMode)index;
		SyncFieldsFromData();
	}

	// -------------------------------------------------------------------------------------------
	// Reading / writing the current target
	// -------------------------------------------------------------------------------------------

	/// <summary>The 7 field values as CharacterRigTuning currently has them for (_currentMode,
	/// _currentKind[, _currentBoneName]). This is the single source of truth the fields, the preview
	/// line and the network sync all read from - typing never edits anything except CharacterRigTuning
	/// itself.</summary>
	private float[] ReadCurrentAppliedValues()
	{
		switch (_currentKind)
		{
			case TargetKind.Bone when _currentBoneName != null:
				var b = CharacterRigTuning.GetBone(_currentMode, _currentBoneName);
				return new[]
				{
					b.PositionOffset.X, b.PositionOffset.Y, b.PositionOffset.Z,
					b.RotationOffsetDegrees.X, b.RotationOffsetDegrees.Y, b.RotationOffsetDegrees.Z,
					b.ScaleMultiplier,
				};
			case TargetKind.Camera:
				var c = CharacterRigTuning.GetCamera(_currentMode);
				return new[]
				{
					c.PositionOffset.X, c.PositionOffset.Y, c.PositionOffset.Z,
					c.RotationOffsetDegrees.X, c.RotationOffsetDegrees.Y, c.RotationOffsetDegrees.Z,
					1.0f,
				};
			default:
				float scale = CharacterRigTuning.GetCharacterScale(_currentMode);
				return new[] { 0f, 0f, 0f, 0f, 0f, 0f, scale };
		}
	}

	private void ApplyFieldsToTarget()
	{
		float[] v = ParseFields();
		switch (_currentKind)
		{
			case TargetKind.Bone:
				if (_currentBoneName == null) return;
				CharacterRigTuning.SetBone(_currentMode, _currentBoneName,
					new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]), v[ScaleFieldIndex]);
				break;
			case TargetKind.Camera:
				CharacterRigTuning.SetCamera(_currentMode, new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]));
				break;
			case TargetKind.CharacterScale:
				CharacterRigTuning.SetCharacterScale(_currentMode, v[ScaleFieldIndex]);
				break;
		}
	}

	/// <summary>Parses every box, tinting an unparseable one red and keeping its previous value - a
	/// box mid-edit ("-", "0.") never snaps the target to a garbage number. Mirrors WeaponGripTuner's
	/// ApplyFieldsToWeapon exactly.</summary>
	private float[] ParseFields()
	{
		float[] v = ReadCurrentAppliedValues();
		for (int i = 0; i < FieldCount; i++)
		{
			bool ok = TryParse(_fields[i].Text, out float parsed);
			_fields[i].Modulate = ok ? Colors.White : InvalidTint;
			if (ok) v[i] = parsed;
		}
		return v;
	}

	private void ResetCurrentTarget()
	{
		switch (_currentKind)
		{
			case TargetKind.Bone:
				if (_currentBoneName != null) CharacterRigTuning.ClearBone(_currentMode, _currentBoneName);
				break;
			case TargetKind.Camera:
				CharacterRigTuning.ClearCamera(_currentMode);
				break;
			case TargetKind.CharacterScale:
				CharacterRigTuning.ClearCharacterScale(_currentMode);
				break;
		}
		SyncFieldsFromData();
	}

	private void ReloadAllDefaults()
	{
		CharacterRigTuning.ReloadFromDefaults();
		Rpc(MethodName.RpcReloadAllDefaults);
		SyncFieldsFromData();
	}

	// -------------------------------------------------------------------------------------------
	// Print / copy
	// -------------------------------------------------------------------------------------------

	private void PrintAndCopyAll()
	{
		string block = CharacterRigTuning.ToSourceLines();
		GD.Print("--- Character Rig Tuning (paste into CharacterRigTuning.LoadDefaults()) ---\n"
			+ (block.Length == 0 ? "(nothing tuned yet)\n" : block) + "---");
		DisplayServer.ClipboardSet(block);
	}

	// -------------------------------------------------------------------------------------------
	// Live sync to the other players
	// -------------------------------------------------------------------------------------------

	/// <summary>Sends the CURRENTLY SELECTED target's live values to every other peer when they
	/// changed (rate limited), plus a periodic refresh - same shape as WeaponGripTuner.
	/// SyncGripToPeers. A target you tuned earlier and then clicked away from needs no further sync:
	/// it was already applied to CharacterRigTuning and already sent while it was selected.</summary>
	private void SyncCurrentTargetToPeers(float delta)
	{
		_syncCooldown -= delta;
		_keepAliveCountdown -= delta;
		if (_syncCooldown > 0.0f) return;
		if (Multiplayer.GetPeers().Length == 0) return;
		if (_currentKind == TargetKind.Bone && _currentBoneName == null) return;

		float[] current = ReadCurrentAppliedValues();
		string identity = $"{_currentKind}:{_currentMode}:{_currentBoneName}";
		bool changed = _lastSent == null || _lastSentIdentity != identity || !SameValues(current, _lastSent);
		if (!changed && _keepAliveCountdown > 0.0f) return;

		_lastSent = current;
		_lastSentIdentity = identity;
		_syncCooldown = SyncIntervalSeconds;
		_keepAliveCountdown = KeepAliveSeconds;
		Rpc(MethodName.RpcSyncTarget, (int)_currentKind, (int)_currentMode, _currentBoneName ?? "",
			new Vector3(current[0], current[1], current[2]), new Vector3(current[3], current[4], current[5]),
			current[ScaleFieldIndex]);
	}

	private static bool SameValues(float[] a, float[] b)
	{
		for (int i = 0; i < a.Length; i++)
			if (Mathf.Abs(a[i] - b[i]) > 0.00001f) return false;
		return true;
	}

	/// <summary>Runs on every OTHER peer: writes the same value into their own copy of
	/// CharacterRigTuning, which CharacterRigOverrideModifier/CharacterRigCameraScaleApplier are
	/// already reading every frame - so the edit shows up for them exactly as it does for you, from
	/// their own camera. Ignored unless it comes from the player's own owner. Enum fields cross the
	/// wire as plain ints (Godot RPC parameters have to be Variant-friendly types).</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	public void RpcSyncTarget(int kind, int mode, string boneName, Vector3 pos, Vector3 rot, float scale)
	{
		if (!OS.IsDebugBuild()) return;
		if (Multiplayer.GetRemoteSenderId() != _playerBody.GetMultiplayerAuthority()) return;
		var poseMode = (CharacterRigTuning.PoseMode)mode;
		switch ((TargetKind)kind)
		{
			case TargetKind.Bone:
				if (!string.IsNullOrEmpty(boneName)) CharacterRigTuning.SetBone(poseMode, boneName, pos, rot, scale);
				break;
			case TargetKind.Camera:
				CharacterRigTuning.SetCamera(poseMode, pos, rot);
				break;
			case TargetKind.CharacterScale:
				CharacterRigTuning.SetCharacterScale(poseMode, scale);
				break;
		}
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	public void RpcReloadAllDefaults()
	{
		if (!OS.IsDebugBuild()) return;
		if (Multiplayer.GetRemoteSenderId() != _playerBody.GetMultiplayerAuthority()) return;
		CharacterRigTuning.ReloadFromDefaults();
	}

	// -------------------------------------------------------------------------------------------
	// Formatting / parsing
	// -------------------------------------------------------------------------------------------

	private static bool TryParse(string text, out float value)
	{
		string cleaned = text.Trim().Replace(',', '.');
		return float.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
			&& float.IsFinite(value);
	}

	/// <summary>Four decimals - the precision pasted source lines are printed at.</summary>
	private static string F(float v) => Format(v, "0.####");

	/// <summary>Six decimals for the entry boxes, so re-applying every box on each keystroke does not
	/// round away digits you just typed.</summary>
	private static string FieldText(float v) => Format(v, "0.######");

	private static string Format(float v, string format)
	{
		string s = v.ToString(format, CultureInfo.InvariantCulture);
		return s == "-0" ? "0" : s;
	}

	private static Skeleton3D? FindSkeleton(Node root)
	{
		if (root is Skeleton3D skeleton) return skeleton;
		foreach (Node child in root.GetChildren())
		{
			var found = FindSkeleton(child);
			if (found != null) return found;
		}
		return null;
	}

	// -------------------------------------------------------------------------------------------
	// Live readout
	// -------------------------------------------------------------------------------------------

	private void UpdateLiveLabel()
	{
		bool authority = _movement.IsMultiplayerAuthority();
		PlayerMovement.MovementState state = authority
			? _movement.State
			: Enum.TryParse(_movement.ReplicatedMovementState, out PlayerMovement.MovementState remote)
				? remote : PlayerMovement.MovementState.Idle;
		bool aiming = authority ? _movement.IsAiming : _movement.ReplicatedAimState;
		bool firing = _controller.IsFirePlaying;
		bool reloading = _controller.IsReloadPlaying;
		string flags = (aiming ? " +Aim" : "") + (reloading ? " +Reload" : "") + (firing ? " +Fire" : "");

		var activeLayers = CharacterRigTuning.ActiveLayersHighToLow(_movement, _controller).ToList();
		string previewNote = activeLayers.Contains(_currentMode)
			? "previewing live"
			: "not active right now - do this, or use a Preview button, to see it";

		int peers = Multiplayer.GetPeers().Length;
		string net = peers > 0 ? $"live-syncing to {peers} other player(s)" : "no other players connected";
		int tunedBones = CharacterRigTuning.TunedBoneNames().Count();

		_liveLabel.Text =
			$"Live: {state}{flags}   Tuned bones so far: {tunedBones}   {net}\n" +
			$"Editing mode '{_currentMode}': {previewNote}";
	}

	private void UpdatePreviewLabel()
	{
		if (_currentKind == TargetKind.Bone && _currentBoneName == null)
		{
			_previewLabel.Text = string.Empty;
			return;
		}
		float[] v = ReadCurrentAppliedValues();
		_previewLabel.Text = _currentKind switch
		{
			TargetKind.Bone =>
				$"SetBone(PoseMode.{_currentMode}, \"{_currentBoneName}\", " +
				$"new Vector3({F(v[0])}f, {F(v[1])}f, {F(v[2])}f), new Vector3({F(v[3])}f, {F(v[4])}f, {F(v[5])}f), {F(v[6])}f);",
			TargetKind.Camera =>
				$"SetCamera(PoseMode.{_currentMode}, " +
				$"new Vector3({F(v[0])}f, {F(v[1])}f, {F(v[2])}f), new Vector3({F(v[3])}f, {F(v[4])}f, {F(v[5])}f));",
			_ => $"SetCharacterScale(PoseMode.{_currentMode}, {F(v[6])}f);",
		};
	}

	private void SyncFieldsFromData()
	{
		_syncingFields = true;
		float[] values = ReadCurrentAppliedValues();
		for (int i = 0; i < FieldCount; i++)
		{
			_fields[i].Text = FieldText(values[i]);
			_fields[i].Modulate = Colors.White;
		}
		_syncingFields = false;
		UpdateTargetLabel();
		UpdatePreviewLabel();
	}

	private void OnFieldEdited()
	{
		if (_syncingFields) return;
		ApplyFieldsToTarget();
	}

	private void OnFieldSubmitted(int index)
	{
		if (_syncingFields) return;
		ApplyFieldsToTarget();
		NormalizeField(index);
		_fields[index].FindNextValidFocus()?.GrabFocus();
	}

	private void NormalizeField(int index)
	{
		if (_syncingFields) return;
		_syncingFields = true;
		_fields[index].Text = FieldText(ReadCurrentAppliedValues()[index]);
		_fields[index].Modulate = Colors.White;
		_syncingFields = false;
	}

	// -------------------------------------------------------------------------------------------
	// UI construction. Built from code on purpose - a debug tool with no matching .tscn to go stale,
	// same reasoning as AnimationTuner's own BuildUi().
	// -------------------------------------------------------------------------------------------

	private void BuildOverlay()
	{
		_canvas = new CanvasLayer { Name = "CharacterRigEditorOverlay", Visible = false, Layer = 65 };
		AddChild(_canvas);

		var panel = new Panel { Name = "CharacterRigEditorPanel" };
		panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
		panel.Position = new Vector2(16, 16);
		panel.CustomMinimumSize = new Vector2(460, 0);
		_canvas.AddChild(panel);

		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(460, 720) };
		panel.AddChild(scroll);
		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 6);
		scroll.AddChild(column);

		column.AddChild(new Label { Text = "CHARACTER RIG EDITOR - F2 to toggle", ThemeTypeVariation = "HeaderSmall" });
		_liveLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
		column.AddChild(_liveLabel);
		column.AddChild(new HSeparator());

		column.AddChild(new Label { Text = "Editing mode (which bucket new edits are filed under):" });
		_modeOption = new OptionButton();
		foreach (CharacterRigTuning.PoseMode mode in Enum.GetValues<CharacterRigTuning.PoseMode>())
			_modeOption.AddItem(mode.ToString());
		_modeOption.ItemSelected += OnModeSelected;
		column.AddChild(_modeOption);
		column.AddChild(new HSeparator());

		column.AddChild(new Label { Text = "Target:" });
		_searchField = new LineEdit { PlaceholderText = "Filter bones (e.g. \"index\", \"lefthand\", \"spine\")..." };
		_searchField.TextChanged += OnSearchChanged;
		column.AddChild(_searchField);

		var listScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 220) };
		column.AddChild(listScroll);
		_targetListBox = new VBoxContainer();
		listScroll.AddChild(_targetListBox);
		AddTargetButton("[ Camera (first-person) ]", TargetKind.Camera, null);
		AddTargetButton("[ Whole-character scale ]", TargetKind.CharacterScale, null);
		_targetListBox.AddChild(new HSeparator());
		BuildBoneButtons();
		column.AddChild(new HSeparator());

		_targetLabel = new Label { Text = "Editing: -" };
		column.AddChild(_targetLabel);

		_positionRow = BuildVectorRow(column, "Position offset (m)", 0);
		_rotationRow = BuildVectorRow(column, "Rotation offset (deg)", 3);
		_scaleRow = BuildScalarRow(column, "Scale (x)", ScaleFieldIndex);

		column.AddChild(new Label { Text = "This line (paste into CharacterRigTuning.LoadDefaults()):" });
		_previewLabel = new Label
		{
			CustomMinimumSize = new Vector2(420, 0),
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			Modulate = new Color(1.0f, 0.95f, 0.6f),
		};
		column.AddChild(_previewLabel);
		column.AddChild(new HSeparator());

		var previewRow = new HBoxContainer();
		previewRow.AddThemeConstantOverride("separation", 6);
		previewRow.AddChild(MakeButton("Preview Fire", () => _controller.PlayFire()));
		previewRow.AddChild(MakeButton("Preview Reload", () => _controller.PlayReload()));
		column.AddChild(previewRow);

		var actionRow = new HBoxContainer();
		actionRow.AddThemeConstantOverride("separation", 6);
		actionRow.AddChild(MakeButton("Copy / Print ALL", PrintAndCopyAll));
		actionRow.AddChild(MakeButton("Reset this target", ResetCurrentTarget));
		actionRow.AddChild(MakeButton("Reload ALL defaults", ReloadAllDefaults));
		column.AddChild(actionRow);

		var sessionRow = new HBoxContainer();
		sessionRow.AddThemeConstantOverride("separation", 6);
		sessionRow.AddChild(MakeButton("Save session", () => CharacterRigTuning.Save()));
		sessionRow.AddChild(MakeButton("Delete saved session", () => CharacterRigTuning.DeleteSaved()));
		sessionRow.AddChild(MakeButton("Close (F2)", () => SetActive(false)));
		column.AddChild(sessionRow);

		var hint = new Label
		{
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			Modulate = new Color(1.0f, 1.0f, 1.0f, 0.65f),
			Text = "Mouse-look and weapon-switch digits are paused while this is open. Walking, "
				+ "crouching, sprinting and aiming still work, so you can hold the real pose while you "
				+ "tune it. Fire/Reload are one-shots - use the preview buttons for those.",
		};
		column.AddChild(hint);

		SelectTarget(TargetKind.Camera, null);
	}

	private void AddTargetButton(string label, TargetKind kind, string? boneName)
	{
		var button = new Button
		{
			Text = label,
			FocusMode = Control.FocusModeEnum.None,
			Alignment = HorizontalAlignment.Left,
		};
		button.Pressed += () => SelectTarget(kind, boneName);
		_targetListBox.AddChild(button);
		if (boneName != null) _boneButtons[boneName] = button;
	}

	private void BuildBoneButtons()
	{
		var names = new List<string>();
		for (int i = 0; i < _skeleton!.GetBoneCount(); i++) names.Add(_skeleton.GetBoneName(i));
		names.Sort(StringComparer.OrdinalIgnoreCase);
		foreach (string name in names) AddTargetButton(name, TargetKind.Bone, name);
	}

	private Control BuildVectorRow(VBoxContainer parent, string title, int firstIndex)
	{
		var box = new VBoxContainer();
		box.AddChild(new Label { Text = title });
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 4);
		for (int i = 0; i < 3; i++) row.AddChild(CreateField(firstIndex + i));
		box.AddChild(row);
		parent.AddChild(box);
		return box;
	}

	private Control BuildScalarRow(VBoxContainer parent, string title, int index)
	{
		var row = new HBoxContainer();
		row.AddThemeConstantOverride("separation", 4);
		row.AddChild(new Label { Text = title, CustomMinimumSize = new Vector2(110, 0) });
		row.AddChild(CreateField(index));
		parent.AddChild(row);
		return row;
	}

	private LineEdit CreateField(int index)
	{
		var field = new LineEdit { CustomMinimumSize = new Vector2(96.0f, 0.0f), SelectAllOnFocus = true };
		field.TextChanged += _ => OnFieldEdited();
		field.TextSubmitted += _ => OnFieldSubmitted(index);
		field.FocusExited += () => NormalizeField(index);
		_fields[index] = field;
		return field;
	}

	private static Button MakeButton(string text, Action onPressed)
	{
		// No keyboard focus: otherwise Space or Enter would press the button again after a click.
		var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
		button.Pressed += onPressed;
		return button;
	}
}
