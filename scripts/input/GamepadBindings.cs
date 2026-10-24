using Godot;

/// <summary>
/// Defines standard default gamepad mappings across PC and mobile platforms.
/// </summary>
public static class GamepadBindings
{
	public static void ApplyDefaultGamepadBindings()
	{
		// Axes
		BindAxis("move_left", JoyAxis.LeftX, -1.0f);
		BindAxis("move_right", JoyAxis.LeftX, 1.0f);
		BindAxis("move_forward", JoyAxis.LeftY, -1.0f);
		BindAxis("move_back", JoyAxis.LeftY, 1.0f);

		BindAxis("look_stick_left", JoyAxis.RightX, -1.0f);
		BindAxis("look_stick_right", JoyAxis.RightX, 1.0f);
		BindAxis("look_stick_up", JoyAxis.RightY, -1.0f);
		BindAxis("look_stick_down", JoyAxis.RightY, 1.0f);

		BindAxis("fire", JoyAxis.TriggerRight, 1.0f);
		BindAxis("aim", JoyAxis.TriggerLeft, 1.0f);

		// Buttons
		BindButton("jump", JoyButton.A);
		BindButton("crouch", JoyButton.B);
		BindButton("reload", JoyButton.X);
		BindButton("interact", JoyButton.Y);
		BindButton("sprint", JoyButton.LeftStick);

		// Weapon Cycling
		BindButton("weapon_cycle_next", JoyButton.RightShoulder);
		BindButton("weapon_cycle_prev", JoyButton.LeftShoulder);

		// Downed Actions
		BindButton(GameUiState.GiveUpAction, JoyButton.Y);
		BindButton(GameUiState.ReviveAction, JoyButton.X);

		// Menus
		BindButton(GameUiState.PauseAction, JoyButton.Start);
		BindButton(GameUiState.ScoreboardAction, JoyButton.Back);

		// Vehicle controls
		BindAxis("vehicle_left", JoyAxis.LeftX, -1.0f);
		BindAxis("vehicle_right", JoyAxis.LeftX, 1.0f);
		BindAxis("vehicle_gas", JoyAxis.TriggerRight, 1.0f);
		BindAxis("vehicle_brake", JoyAxis.TriggerLeft, 1.0f);
		BindButton("vehicle_handbrake", JoyButton.A);
		BindButton("vehicle_interact", JoyButton.Y);
	}

	private static void BindButton(string action, JoyButton button)
	{
		if (!InputMap.HasAction(action)) InputMap.AddAction(action);
		var evt = new InputEventJoypadButton { ButtonIndex = button };
		if (!InputMap.ActionHasEvent(action, evt)) InputMap.ActionAddEvent(action, evt);
	}

	private static void BindAxis(string action, JoyAxis axis, float axisValue)
	{
		if (!InputMap.HasAction(action)) InputMap.AddAction(action);
		var evt = new InputEventJoypadMotion { Axis = axis, AxisValue = axisValue };
		if (!InputMap.ActionHasEvent(action, evt)) InputMap.ActionAddEvent(action, evt);
	}
}
