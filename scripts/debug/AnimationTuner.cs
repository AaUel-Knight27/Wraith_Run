using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Press F8 in a debug build to open a panel that edits AnimationTuning and the current weapon's left-
/// hand offset while the game keeps running, so you can see the character's legs and hands respond
/// immediately instead of guessing numbers, exporting, and re-testing.
///
/// Sections:
///   Move speed      - the three PlayerMovement speeds (now [Export] properties, so this is a live
///                      inspector for them - the F8 panel is not a separate copy of the numbers).
///   Run cadence     - the ground speed each directional run clip was authored for, and the min/max
///                      playback speed the game is allowed to stretch a clip to before it gives up
///                      and lets the feet slide instead of looking sped-up/slowed-down.
///   Crouch cadence  - the same, for the crouch-walk clips.
///   Left hand       - position/rotation offset from the default two-handed grip, and the elbow pole
///                      position, for the CURRENT weapon. Switch weapons in-game and the panel follows.
///
/// "Copy AnimationTuning" and "Copy weapon lines" put ready-to-paste C#/.tres text on the clipboard,
/// which is the actual handoff: tune here, paste the numbers into the source, delete the tuner's saved
/// file (button provided) once you're happy, and the debug-only save file stops mattering.
/// </summary>
public partial class AnimationTuner : CanvasLayer
{
	private Panel? _panel;
	private VBoxContainer? _list;
	private RichTextLabel? _stats;
	private PlayerAnimationController? _controller;
	private LeftHandIk? _handIk;
	private readonly List<Action> _refreshers = new();
	private bool _open;

	public override void _Ready()
	{
		Layer = 64;
		ProcessMode = ProcessModeEnum.Always;
		_controller = GetParent<PlayerAnimationController>();
		BuildUi();
		SetOpen(false);
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F8 })
		{
			SetOpen(!_open);
			GetViewport().SetInputAsHandled();
		}
	}

	private void SetOpen(bool open)
	{
		_open = open;
		if (_panel != null) _panel.Visible = open;
		if (open) foreach (var refresh in _refreshers) refresh();
	}

	public override void _Process(double delta)
	{
		if (!_open || _stats == null || _controller == null) return;
		_handIk = _controller.HandIk;
		_stats.Text =
			$"state {_controller.CurrentGraphState}   speed {_controller.MeasuredSpeed:0.00} m/s   " +
			$"heading {Mathf.RadToDeg(Mathf.Atan2(_controller.Heading.X, _controller.Heading.Y)):0} deg   " +
			$"time-scale {_controller.TimeScale:0.00}   " +
			$"hand IK weight {(_handIk?.Weight ?? 0):0.00}   " +
			$"weapon {_handIk?.AppliedWeapon?.WeaponName ?? "-"}";
	}

	// ---------------------------------------------------------------------------------------------
	// UI construction. Built from code on purpose: a debug tool with no matching .tscn to go stale.
	// ---------------------------------------------------------------------------------------------

	private void BuildUi()
	{
		_panel = new Panel { Name = "TunerPanel" };
		_panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
		_panel.Position = new Vector2(16, 16);
		_panel.CustomMinimumSize = new Vector2(420, 0);
		AddChild(_panel);

		var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(420, 640) };
		_panel.AddChild(scroll);
		_list = new VBoxContainer();
		_list.AddThemeConstantOverride("separation", 2);
		scroll.AddChild(_list);

		Header("Animation Tuner - F8 to toggle");
		_stats = new RichTextLabel { CustomMinimumSize = new Vector2(400, 40), FitContent = true, BbcodeEnabled = false };
		_list!.AddChild(_stats);

		Header("Move speed (m/s)");
		FloatSlider("Walk", 0.5f, 12f, 0.05f,
			() => _controller!.GetParent<CharacterBody3D>().Get("WalkSpeed").AsSingle(),
			v => _controller!.GetParent<CharacterBody3D>().Set("WalkSpeed", v));
		FloatSlider("Sprint", 0.5f, 14f, 0.05f,
			() => _controller!.GetParent<CharacterBody3D>().Get("SprintSpeed").AsSingle(),
			v => _controller!.GetParent<CharacterBody3D>().Set("SprintSpeed", v));
		FloatSlider("Crouch", 0.5f, 8f, 0.05f,
			() => _controller!.GetParent<CharacterBody3D>().Get("CrouchSpeed").AsSingle(),
			v => _controller!.GetParent<CharacterBody3D>().Set("CrouchSpeed", v));

		Header("Run cadence");
		FloatField("Authored speed @0", 0.2f, 12f, () => AnimationTuning.RunSpeedAt0, v => AnimationTuning.RunSpeedAt0 = v);
		FloatField("Authored speed @45", 0.2f, 12f, () => AnimationTuning.RunSpeedAt45, v => AnimationTuning.RunSpeedAt45 = v);
		FloatField("Authored speed @135", 0.2f, 12f, () => AnimationTuning.RunSpeedAt135, v => AnimationTuning.RunSpeedAt135 = v);
		FloatField("Authored speed @180", 0.2f, 12f, () => AnimationTuning.RunSpeedAt180, v => AnimationTuning.RunSpeedAt180 = v);

		Header("Crouch cadence");
		FloatField("Authored speed @0", 0.2f, 6f, () => AnimationTuning.CrouchSpeedAt0, v => AnimationTuning.CrouchSpeedAt0 = v);
		FloatField("Authored speed @45", 0.2f, 6f, () => AnimationTuning.CrouchSpeedAt45, v => AnimationTuning.CrouchSpeedAt45 = v);
		FloatField("Authored speed @135", 0.2f, 6f, () => AnimationTuning.CrouchSpeedAt135, v => AnimationTuning.CrouchSpeedAt135 = v);
		FloatField("Authored speed @180", 0.2f, 6f, () => AnimationTuning.CrouchSpeedAt180, v => AnimationTuning.CrouchSpeedAt180 = v);

		Header("Playback stretch limits");
		FloatSlider("Min time-scale", 0.2f, 1.0f, 0.01f, () => AnimationTuning.MinTimeScale, v => AnimationTuning.MinTimeScale = v);
		FloatSlider("Max time-scale", 1.0f, 2.5f, 0.01f, () => AnimationTuning.MaxTimeScale, v => AnimationTuning.MaxTimeScale = v);
		FloatSlider("Crossfade (s)", 0.0f, 0.6f, 0.01f, () => AnimationTuning.CrossfadeSeconds, v => AnimationTuning.CrossfadeSeconds = v);

		Header("Leg heading (strafing)");
		BoolField("Enabled", () => AnimationTuning.LegTwistEnabled, v => AnimationTuning.LegTwistEnabled = v);
		FloatSlider("Switch to backward clips at", 90f, 170f, 1f,
			() => AnimationTuning.BackwardSwitchAngle, v => AnimationTuning.BackwardSwitchAngle = v);
		FloatSlider("Switch to forward clips at", 10f, 90f, 1f,
			() => AnimationTuning.ForwardSwitchAngle, v => AnimationTuning.ForwardSwitchAngle = v);

		Header("Left hand (current weapon)");
		BoolField("IK enabled", () => AnimationTuning.HandIkEnabled, v => AnimationTuning.HandIkEnabled = v);
		FloatSlider("Elbow pole X (out)", -0.9f, 0.9f, 0.01f, () => AnimationTuning.PoleX, v => AnimationTuning.PoleX = v);
		FloatSlider("Elbow pole Y (down)", -0.9f, 0.4f, 0.01f, () => AnimationTuning.PoleY, v => AnimationTuning.PoleY = v);
		FloatSlider("Elbow pole Z (back)", -0.4f, 0.9f, 0.01f, () => AnimationTuning.PoleZ, v => AnimationTuning.PoleZ = v);
		VectorField("Position offset (m)", -0.4f, 0.4f,
			() => _handIk?.OffsetMeters ?? Vector3.Zero, v => { if (_handIk != null) _handIk.OffsetMeters = v; });
		VectorField("Rotation offset (deg)", -90f, 90f,
			() => _handIk?.OffsetDegrees ?? Vector3.Zero, v => { if (_handIk != null) _handIk.OffsetDegrees = v; });
		var twoHand = new HBoxContainer();
		twoHand.AddChild(new Label { Text = "Two-handed override", CustomMinimumSize = new Vector2(210, 0) });
		var auto = new Button { Text = "Auto", ToggleMode = true };
		var force2 = new Button { Text = "Force 2H", ToggleMode = true };
		var force1 = new Button { Text = "Force 1H", ToggleMode = true };
		auto.Pressed += () => { if (_handIk != null) _handIk.ForcedTwoHanded = null; Sync(auto, force2, force1, null); };
		force2.Pressed += () => { if (_handIk != null) _handIk.ForcedTwoHanded = true; Sync(auto, force2, force1, true); };
		force1.Pressed += () => { if (_handIk != null) _handIk.ForcedTwoHanded = false; Sync(auto, force2, force1, false); };
		twoHand.AddChild(auto); twoHand.AddChild(force2); twoHand.AddChild(force1);
		_list.AddChild(twoHand);
		var resetHand = new Button { Text = "Reset hand offset to weapon's stored values" };
		resetHand.Pressed += () => _handIk?.ResetToWeapon();
		_list.AddChild(resetHand);

		Header("Save / export");
		var row = new HBoxContainer();
		var save = new Button { Text = "Save" };
		save.Pressed += () => AnimationTuning.Save();
		var clear = new Button { Text = "Delete saved file" };
		clear.Pressed += () => AnimationTuning.DeleteSaved();
		var copyTuning = new Button { Text = "Copy AnimationTuning" };
		copyTuning.Pressed += () => DisplayServer.ClipboardSet(AnimationTuning.ToSourceLines());
		var copyHand = new Button { Text = "Copy weapon lines" };
		copyHand.Pressed += () => DisplayServer.ClipboardSet(_handIk?.ToResourceLines() ?? "");
		row.AddChild(save); row.AddChild(clear); row.AddChild(copyTuning); row.AddChild(copyHand);
		_list.AddChild(row);
		var note = new Label
		{
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			Text = "Save keeps these numbers across a restart (user://animation_tuning.cfg, debug builds "
				+ "only). Copy AnimationTuning pastes over the defaults at the top of AnimationTuning.cs. "
				+ "Copy weapon lines pastes under the current weapon in WeaponRegistry.tres.",
		};
		_list.AddChild(note);
	}

	private static void Sync(Button auto, Button f2, Button f1, bool? state)
	{
		auto.ButtonPressed = state == null;
		f2.ButtonPressed = state == true;
		f1.ButtonPressed = state == false;
	}

	private void Header(string text)
	{
		_list!.AddChild(new Label { Text = text, ThemeTypeVariation = "HeaderSmall" });
	}

	private void BoolField(string label, Func<bool> get, Action<bool> set)
	{
		var row = new HBoxContainer();
		row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(210, 0) });
		var box = new CheckBox { ButtonPressed = get() };
		box.Toggled += set.Invoke;
		row.AddChild(box);
		_refreshers.Add(() => box.ButtonPressed = get());
		_list!.AddChild(row);
	}

	private void FloatSlider(string label, float min, float max, float step, Func<float> get, Action<float> set)
	{
		var row = new HBoxContainer();
		row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(210, 0) });
		var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = get(), CustomMinimumSize = new Vector2(140, 0) };
		var value = new Label { CustomMinimumSize = new Vector2(50, 0), Text = get().ToString("0.00", CultureInfo.InvariantCulture) };
		slider.ValueChanged += v => { set((float)v); value.Text = ((float)v).ToString("0.00", CultureInfo.InvariantCulture); };
		_refreshers.Add(() => { slider.SetValueNoSignal(get()); value.Text = get().ToString("0.00", CultureInfo.InvariantCulture); });
		row.AddChild(slider); row.AddChild(value);
		_list!.AddChild(row);
	}

	/// <summary>A slider with a wider range than FloatSlider suits (authored speeds), same layout.</summary>
	private void FloatField(string label, float min, float max, Func<float> get, Action<float> set)
		=> FloatSlider(label, min, max, 0.02f, get, set);

	private void VectorField(string label, float min, float max, Func<Vector3> get, Action<Vector3> set)
	{
		_list!.AddChild(new Label { Text = label });
		Vector3 current = get();
		var row = new HBoxContainer();
		HSlider MakeAxis(string axisLabel, float initial, Action<float> onChange)
		{
			row.AddChild(new Label { Text = axisLabel, CustomMinimumSize = new Vector2(16, 0) });
			var slider = new HSlider { MinValue = min, MaxValue = max, Step = 0.005f, Value = initial, CustomMinimumSize = new Vector2(110, 0) };
			slider.ValueChanged += v => onChange((float)v);
			row.AddChild(slider);
			return slider;
		}
		var sx = MakeAxis("X", current.X, v => { var c = get(); c.X = v; set(c); });
		var sy = MakeAxis("Y", current.Y, v => { var c = get(); c.Y = v; set(c); });
		var sz = MakeAxis("Z", current.Z, v => { var c = get(); c.Z = v; set(c); });
		_refreshers.Add(() =>
		{
			Vector3 v = get();
			sx.SetValueNoSignal(v.X); sy.SetValueNoSignal(v.Y); sz.SetValueNoSignal(v.Z);
		});
		_list.AddChild(row);
	}
}
