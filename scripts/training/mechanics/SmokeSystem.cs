using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// MEDIC (practice loadout) - throw smoke with [F] at your crosshair (up to 30 m). The baseline medic has no smoke
/// (only Ashen Corvus does in the roster), so drills that teach it lend 2 charges - see TrainingKit.PracticeSmokeCharges.
/// Enemy actors lose line of sight through it, so smoke + heal is a real tactic in training.
/// </summary>
public partial class SmokeSystem : TrainingMechanic
{
    public int Charges { get; private set; }
    public event Action<SmokeCloud>? Thrown;

    public void SetCharges(int charges) => Charges = charges;

    public override void _Process(double delta)
    {
        if (!Session.DrillLive || !Input.IsActionJustPressed(TrainingInput.AbilitySecondary)) return;
        if (Charges <= 0)
        {
            Session.Toast("No smoke left", new Color(1.0f, 0.5f, 0.4f));
            return;
        }

        Vector3 point;
        if (Session.AimRay(30.0f, out Vector3 hit, out _, out _)) point = hit;
        else point = Session.Player.GlobalPosition - Session.Player.GlobalTransform.Basis.Z * 12.0f;

        Charges--;
        var cloud = new SmokeCloud();
        Session.Props.AddChild(cloud);
        cloud.GlobalPosition = point + new Vector3(0, 1.4f, 0);
        Session.Toast($"Smoke out ({Charges} left)", new Color(0.8f, 0.85f, 1.0f));
        Thrown?.Invoke(cloud);
    }
}
