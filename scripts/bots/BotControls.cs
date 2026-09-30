using Godot;

/// <summary>
/// The virtual keyboard/mouse of a bot. BotBrain writes it, PlayerMovement and WeaponSwitcher read it
/// in place of Input.*. Level values (Move, Sprint, ...) persist; pulse values are "just pressed" events
/// that are consumed exactly once.
/// </summary>
public sealed class BotControls
{
	/// <summary>Same convention as Input.GetVector: X = right, Y = back (forward is negative).</summary>
	public Vector2 Move;
	public bool Sprint;
	public bool Crouch;
	public bool Aim;
	public bool FireHeld;

	public bool JumpPulse;
	public bool FirePulse;
	public bool ReloadPulse;

	public void ClearJump() { JumpPulse = false; }
	public bool ConsumeJump() { bool v = JumpPulse; JumpPulse = false; return v; }
	public bool ConsumeFirePulse() { bool v = FirePulse; FirePulse = false; return v; }
	public bool ConsumeReload() { bool v = ReloadPulse; ReloadPulse = false; return v; }

	public void Reset()
	{
		Move = Vector2.Zero;
		Sprint = false;
		Crouch = false;
		Aim = false;
		FireHeld = false;
		JumpPulse = false;
		FirePulse = false;
		ReloadPulse = false;
	}
}
