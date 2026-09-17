using System;
using Godot;

/// <summary>
/// Rolling yaw-delta tracker that backs the 360 no-scope stunt bonus (FR-SC-03: "track killer yaw
/// delta over a rolling ~1s window prior to kill confirmation").
///
/// It samples the parent body's yaw once per physics frame into a fixed-size ring buffer, then
/// measures the window on demand - so the per-frame cost is one array write, not an O(n) scan.
/// Yaw is compared through Mathf.AngleDifference so a spin that crosses the +-180 degree seam still
/// measures as one continuous rotation.
///
/// Only the owning device's value is ever consulted by WeaponSwitcher; remote copies of the player
/// simply keep sampling an unused buffer.
/// </summary>
public partial class RotationWindowTracker : Node
{
    [Export] public float WindowSeconds { get; set; } = KillStyleBonus.NoScopeWindowSeconds;

    // 128 samples covers the default 1s window even at a 120Hz physics tick.
    private const int Capacity = 128;
    private const float MinimumWindowSeconds = 0.05f;

    private readonly double[] _sampleTimes = new double[Capacity];
    private readonly float[] _sampleYaws = new float[Capacity];
    private int _head;
    private int _count;
    private double _now;
    private Node3D? _body;

    public override void _Ready()
    {
        _body = GetParent() as Node3D;
        if (_body != null) return;
        GD.PushError("RotationWindowTracker expects a Node3D parent to read yaw from; spin bonus disabled.");
        SetPhysicsProcess(false);
    }

    public override void _PhysicsProcess(double delta)
    {
        _now += delta;
        _sampleTimes[_head] = _now;
        _sampleYaws[_head] = _body!.Rotation.Y;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>Absolute yaw swept inside the rolling window, in degrees.</summary>
    public float AccumulatedYawDegrees => MeasureWindow();

    /// <summary>Convenience wrapper for the full FR-SC-03 check, including the aim-state half of
    /// "no-scope" that this node cannot know on its own.</summary>
    public bool HasFullSpin(bool aimingDownSights) =>
        KillStyleBonus.IsFullSpin(aimingDownSights, AccumulatedYawDegrees);

    private float MeasureWindow()
    {
        if (_count < 2) return 0.0f;

        double oldestAllowed = _now - Math.Max(MinimumWindowSeconds, WindowSeconds);
        int newest = Wrap(_head - 1);
        float newerYaw = _sampleYaws[newest];
        float total = 0.0f;

        for (int step = 1; step < _count; step++)
        {
            int index = Wrap(_head - 1 - step);
            if (_sampleTimes[index] < oldestAllowed) break;
            float olderYaw = _sampleYaws[index];
            total += Math.Abs(Mathf.RadToDeg(Mathf.AngleDifference(newerYaw, olderYaw)));
            newerYaw = olderYaw;
        }
        return total;
    }

    private static int Wrap(int index) => ((index % Capacity) + Capacity) % Capacity;
}