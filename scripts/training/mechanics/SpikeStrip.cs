using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// A deployed spike strip. Any TrainingCar (group "training_car") that rolls across it punctures all four tires
/// and stops, entering the same repair-window state a tire shot causes (FR-CA-09). It stops the SAME vehicle only
/// once per 45 s (roster: Tripwire Dolan).
/// </summary>
public partial class SpikeStrip : Node3D
{
    public float HalfWidth { get; set; } = 2.4f;
    public float HalfDepth { get; set; } = 0.7f;
    public float CooldownSeconds { get; set; } = TrainingKit.SpikeCooldownSeconds;
    public event Action<TrainingCar>? StoppedVehicle;

    private readonly Dictionary<ulong, float> _lastHit = new();
    private float _clock;

    public override void _Ready()
    {
        AddChild(TrainingUtil.Box(new Vector3(HalfWidth * 2.0f, 0.05f, HalfDepth * 2.0f), new Color(0.12f, 0.12f, 0.12f), new Vector3(0, 0.03f, 0), "Strip"));
        for (int i = 0; i < 12; i++)
        {
            float x = Mathf.Lerp(-HalfWidth + 0.2f, HalfWidth - 0.2f, i / 11.0f);
            AddChild(TrainingUtil.Box(new Vector3(0.06f, 0.14f, 0.06f), new Color(0.75f, 0.75f, 0.78f), new Vector3(x, 0.1f, 0), $"Spike{i}"));
        }
        AddChild(TrainingUtil.Label("SPIKE STRIP", new Vector3(0, 0.9f, 0), new Color(1.0f, 0.6f, 0.3f), 28, false));
    }

    public override void _PhysicsProcess(double delta)
    {
        _clock += (float)delta;
        foreach (Node node in GetTree().GetNodesInGroup("training_car"))
        {
            if (node is not TrainingCar car || car.IsDestroyed) continue;
            Vector3 local = GlobalTransform.AffineInverse() * car.GlobalPosition;
            if (Mathf.Abs(local.X) > HalfWidth || Mathf.Abs(local.Z) > HalfDepth + 1.2f) continue;
            if (Mathf.Abs(car.CurrentSpeed) < 0.5f) continue;

            ulong id = car.GetInstanceId();
            if (_lastHit.TryGetValue(id, out float last) && _clock - last < CooldownSeconds) continue;
            _lastHit[id] = _clock;
            car.PunctureAllTires();
            StoppedVehicle?.Invoke(car);
        }
    }
}
