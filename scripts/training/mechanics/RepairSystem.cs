using Godot;
using System;

/// <summary>
/// FIXER - the repair channel. Hold [Q] beside the vehicle: each completed 6 s channel spends one charge and
/// fixes a flat tire first, otherwise restores body HP (+50).
///
/// Deliberately NOT interrupted by damage or by strafing within reach: the Fixer's whole job is to fix the car
/// and fight at the same time, so aiming and firing stay live while the channel runs (fire is polled separately
/// from this action). It IS cancelled if you walk away from the hull, or the vehicle is moving faster than the
/// kit allows (2 m/s baseline; 8 m/s with Patchwork-style moving repair).
///
/// REAL WIRING: make the real vehicle class implement IRepairable (Ports.cs) and assign it to Target.
/// </summary>
public partial class RepairSystem : TrainingMechanic
{
    public const float Range = 3.2f;

    public int Charges { get; private set; }
    public readonly HoldChannel Channel = new();
    public IRepairable? Target { get; set; }

    public event Action<RepairResult>? Repaired;
    public event Action<string>? RepairInterrupted;

    private bool _needRelease;

    public void SetCharges(int charges) => Charges = charges;

    public override void _Ready()
    {
        Charges = Session.Kit.RepairCharges;
        Channel.Completed += OnCompleted;
        Channel.Interrupted += OnInterrupted;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        bool hold = Input.IsActionPressed(TrainingInput.AbilityPrimary);
        if (!hold) _needRelease = false;

        if (Channel.Active)
        {
            string? problem = Validate(hold);
            if (problem != null) Channel.Interrupt(problem);
            else Channel.Tick(dt);
        }
        else if (hold && !_needRelease && Session.DrillLive)
        {
            TryBegin();
        }

        if (Channel.Active)
        {
            ShowChannel(Channel.Progress, Channel.Label);
            ShowPrompt(null);
            return;
        }
        HideChannel();

        string key = TrainingInput.KeyText(TrainingInput.AbilityPrimary);
        if (Target == null || Target.IsDestroyed) ShowPrompt(null);
        else if (Charges <= 0) ShowPrompt("Out of repair charges");
        else if (!Target.NeedsRepair) ShowPrompt("Vehicle is fully repaired");
        else if (Target.DistanceToHull(Session.Player.GlobalPosition) > Range) ShowPrompt("Get within reach of the vehicle");
        else if (Target.CurrentSpeed > Session.Kit.RepairMaxVehicleSpeed) ShowPrompt("Vehicle is moving - call SLOW DOWN [" + TrainingInput.KeyText(TrainingInput.CalloutQuick) + "]");
        else ShowPrompt($"Hold [{key}] to repair   ({Charges} charges)");
    }

    private void TryBegin()
    {
        if (Target == null || Target.IsDestroyed || !Target.NeedsRepair) return;
        if (Charges <= 0)
        {
            Session.Toast("Out of repair charges", new Color(1.0f, 0.5f, 0.4f));
            _needRelease = true;
            return;
        }
        if (Target.DistanceToHull(Session.Player.GlobalPosition) > Range) return;
        if (Target.CurrentSpeed > Session.Kit.RepairMaxVehicleSpeed)
        {
            Session.Toast("Vehicle is moving too fast to repair", new Color(1.0f, 0.75f, 0.35f));
            _needRelease = true;
            return;
        }

        string what = Target.FlatTireCount > 0 ? "TIRE" : "BODY";
        Channel.Begin(Session.Kit.RepairSeconds, $"REPAIRING {what}");
        Session.Callouts.Send(CalloutType.Repairing, "You", true, Target.WorldPosition);
    }

    private string? Validate(bool hold)
    {
        if (!hold) return "released too early";
        if (Target == null || Target.IsDestroyed) return "vehicle destroyed";
        if (Target.DistanceToHull(Session.Player.GlobalPosition) > Range + 0.8f) return "you left the vehicle";
        if (Target.CurrentSpeed > Session.Kit.RepairMaxVehicleSpeed) return "vehicle started moving";
        return null;
    }

    private void OnCompleted()
    {
        if (Target == null) return;
        RepairResult result = Target.ApplyRepairCharge(TrainingKit.RepairHpPerCharge);
        Charges--;
        _needRelease = true;
        string text = result == RepairResult.TireFixed ? "Tire fixed" : result == RepairResult.BodyRepaired ? "Body repaired +50 HP" : "Nothing to repair";
        Session.Toast(text, new Color(0.5f, 1.0f, 0.6f));
        Repaired?.Invoke(result);
    }

    private void OnInterrupted(string reason)
    {
        _needRelease = true;
        Session.Toast($"Repair interrupted: {reason}", new Color(1.0f, 0.55f, 0.4f));
        RepairInterrupted?.Invoke(reason);
    }
}
