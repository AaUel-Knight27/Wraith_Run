using Godot;
using System.Globalization;

/// <summary>
/// Live in-game calibrator for how a weapon sits in the hand. Every grip value in
/// WeaponRegistry.tres (GripPosition, GripRotationDegrees, ModelScale) was computed from the raw
/// mesh geometry - a reasonable starting point, but nobody has looked at the result on screen.
/// This is how you finish the job without an editor-reimport cycle per nudge: toggle it on, watch
/// the gun move in real time, and copy the printed numbers straight into the .tres.
///
/// This is a development tool, not shipped game behaviour. It only arms for the local player
/// (IsMultiplayerAuthority) and touches only the visual Grip node - it never sends anything over
/// the network and has no effect on hitscan, damage, or any other peer's view.
/// </summary>
public partial class WeaponGripTuner : Node
{
	private const Key ToggleKey = Key.F3;
	private const Key PrintKey = Key.F4;
	private const Key ResetKey = Key.F5;

	// Position in metres, rotation in degrees, scale as a multiplier - all per key-repeat while a
	// key is held. Hold Shift for the coarse step to close in fast, then fine-tune unshifted.
	private const float PositionStepFine = 0.001f;
	private const float PositionStepCoarse = 0.01f;
	private const float RotationStepFine = 0.5f;
	private const float RotationStepCoarse = 5.0f;
	private const float ScaleStepFine = 0.005f;
	private const float ScaleStepCoarse = 0.05f;

	private WeaponAttachment _attachment = null!;
	private CharacterBody3D _player = null!;
	private Label _label = null!;
	private CanvasLayer _canvas = null!;

	private bool _active;
	private bool _togglePressedLastFrame;
	private bool _printPressedLastFrame;
	private bool _resetPressedLastFrame;
	private string? _lastWeaponName;
	private float _baselineScale = 1.0f;
	private Vector3 _baselineRotation;

	public override void _Ready()
	{
		_player = (CharacterBody3D)GetParent();
		_attachment = GetNode<WeaponAttachment>("../Armature/WeaponAttachment");

		// Never arms on a remote copy of another player - there is nothing here for it to tune and
		// no reason to build UI for it.
		SetProcess(_player.IsMultiplayerAuthority());
		if (!_player.IsMultiplayerAuthority()) return;

		BuildOverlay();
	}

	public override void _Process(double delta)
	{
		HandleToggle();
		if (!_active) return;

		TrackWeaponChange();
		HandleAdjustment((float)delta);
		HandlePrint();
		HandleReset();
		UpdateLabel();
	}

	private void HandleToggle()
	{
		bool pressed = Input.IsPhysicalKeyPressed(ToggleKey);
		if (pressed && !_togglePressedLastFrame)
		{
			_active = !_active;
			_canvas.Visible = _active;
			if (_active) TrackWeaponChange();
		}
		_togglePressedLastFrame = pressed;
	}

	/// <summary>Re-reads the baseline whenever the equipped weapon changes, including the very
	/// first frame tuning is turned on - otherwise the first nudge would jump from whatever stale
	/// baseline was left over from the previous weapon.</summary>
	private void TrackWeaponChange()
	{
		string? name = _attachment.CurrentWeapon?.WeaponName;
		if (name == _lastWeaponName) return;
		_lastWeaponName = name;
		_baselineScale = _attachment.CurrentWeapon?.ModelScale ?? 1.0f;
		_baselineRotation = _attachment.CurrentWeapon?.GripRotationDegrees ?? Vector3.Zero;
	}

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
		if (scaleDelta != 0.0f)
		{
			_baselineScale = Mathf.Max(0.001f, _baselineScale + scaleDelta);
			foreach (Node child in grip.GetChildren())
				if (child is Node3D model && child.Name == "EquippedWeapon")
					model.Scale = Vector3.One * _baselineScale;
		}
	}

	private void HandlePrint()
	{
		bool pressed = Input.IsPhysicalKeyPressed(PrintKey);
		if (pressed && !_printPressedLastFrame) PrintCurrentValues();
		_printPressedLastFrame = pressed;
	}

	private void HandleReset()
	{
		bool pressed = Input.IsPhysicalKeyPressed(ResetKey);
		if (pressed && !_resetPressedLastFrame && _attachment.CurrentWeapon != null)
		{
			// Re-equip the same WeaponData, which rebuilds the Grip node straight from its
			// registry values - the simplest way to discard a nudge session gone wrong.
			_attachment.Equip(_attachment.CurrentWeapon);
			_lastWeaponName = null;
		}
		_resetPressedLastFrame = pressed;
	}

	private void PrintCurrentValues()
	{
		var weapon = _attachment.CurrentWeapon;
		if (weapon == null) return;
		Vector3 p = _attachment.GripPositionForData;
		Vector3 r = _attachment.CurrentGrip?.RotationDegrees ?? Vector3.Zero;
		string block =
			$"GripPosition = Vector3({F(p.X)}, {F(p.Y)}, {F(p.Z)})\n" +
			$"GripRotationDegrees = Vector3({F(r.X)}, {F(r.Y)}, {F(r.Z)})\n" +
			$"ModelScale = {F(_baselineScale)}";
		GD.Print($"--- {weapon.WeaponName} grip (paste into scripts/data/WeaponRegistry.tres) ---\n{block}\n---");
	}

	private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

	private void UpdateLabel()
	{
		var weapon = _attachment.CurrentWeapon;
		Vector3 p = _attachment.GripPositionForData;
		Vector3 r = _attachment.CurrentGrip?.RotationDegrees ?? Vector3.Zero;
		_label.Text =
			$"WEAPON GRIP TUNER - {weapon?.WeaponName ?? "(no weapon)"}\n" +
			$"Pos  X {F(p.X)}  Y {F(p.Y)}  Z {F(p.Z)}\n" +
			$"Rot  X {F(r.X)}  Y {F(r.Y)}  Z {F(r.Z)}\n" +
			$"Scale {F(_baselineScale)}\n" +
			"\n" +
			"Arrows: move X/Z   PgUp/PgDn: move Y\n" +
			"[ ]: yaw   , .: pitch   ; ': roll\n" +
			"- =: scale   Shift: coarse step\n" +
			"F4: print values to paste   F5: reset weapon\n" +
			"F3: close";
	}

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
	}
}
