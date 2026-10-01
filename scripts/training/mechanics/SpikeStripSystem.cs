using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// DEMOLISHER - deploy a spike strip. Aim at the ground within 10 m and hold [Q] for the deploy time (3 s, Tripwire
/// Dolan's figure). The baseline Demolisher carries none, so the drill that teaches it lends strips.
/// </summary>
public partial class SpikeStripSystem : TrainingMechanic
{
    public const float DeployReach = 10.0f;

    public int Strips { get; private set; }
    /// <summary>If set, deployed strips use this yaw (radians) instead of the player's facing - e.g. to lie across a known road.</summary>
    public float? FixedYaw { get; set; }
    public readonly HoldChannel Channel = new();
    public event Action<SpikeStrip>? Deployed;
    public event Action<TrainingCar>? StoppedVehicle;

    private bool _needRelease;
    private Vector3 _deployPoint;
    private float _deployYaw;
    private Vector3 _standPoint;

    public void SetStrips(int strips) => Strips = strips;

    public override void _Ready()
    {
        Channel.Completed += OnDeployed;
        Channel.Interrupted += OnInterrupted;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        bool hold = Input.IsActionPressed(TrainingInput.AbilityPrimary);
        if (!hold) _needRelease = false;

        if (Channel.Active)
        {
            if (!hold) Channel.Interrupt("released too early");
            else if (Session.Player.GlobalPosition.DistanceTo(_standPoint) > 2.0f) Channel.Interrupt("you moved");
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
        ShowPrompt(Strips > 0 ? $"Aim at the road, hold [{key}] to deploy a spike strip   ({Strips} left)" : "No spike strips left");
    }

    private void TryBegin()
    {
        if (Strips <= 0)
        {
            Session.Toast("No spike strips left", new Color(1.0f, 0.5f, 0.4f));
            _needRelease = true;
            return;
        }
        if (!Session.AimRay(DeployReach, out Vector3 point, out _, out Vector3 normal)) return;
        if (normal.Y < 0.7f) return;   // ground only, not walls

        _deployPoint = point;
        _deployYaw = FixedYaw ?? Session.Player.GlobalRotation.Y;
        _standPoint = Session.Player.GlobalPosition;
        Channel.Begin(TrainingKit.SpikeDeploySeconds, "DEPLOYING SPIKE STRIP");
    }

    private void OnDeployed()
    {
        Strips--;
        _needRelease = true;
        var strip = new SpikeStrip();
        strip.StoppedVehicle += car => StoppedVehicle?.Invoke(car);
        Session.Props.AddChild(strip);
        strip.GlobalPosition = _deployPoint;
        strip.Rotation = new Vector3(0, _deployYaw, 0);
        Session.Toast("Spike strip deployed", new Color(1.0f, 0.75f, 0.35f));
        Deployed?.Invoke(strip);
    }

    private void OnInterrupted(string reason)
    {
        _needRelease = true;
        Session.Toast($"Deploy interrupted: {reason}", new Color(1.0f, 0.55f, 0.4f));
    }
}
