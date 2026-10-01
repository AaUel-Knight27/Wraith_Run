using Godot;
using System;

/// <summary>A hold-to-complete action: heal, repair, arm a charge, deploy a strip. Pure logic, no nodes.</summary>
public sealed class HoldChannel
{
    public bool Active { get; private set; }
    public float Duration { get; private set; }
    public float Elapsed { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public float Progress => Duration <= 0.0f ? 1.0f : Mathf.Clamp(Elapsed / Duration, 0.0f, 1.0f);
    public float Remaining => Mathf.Max(0.0f, Duration - Elapsed);

    public event Action? Completed;
    public event Action<string>? Interrupted;

    public void Begin(float duration, string label)
    {
        Duration = duration;
        Elapsed = 0.0f;
        Label = label;
        Active = true;
    }

    public void Tick(float dt)
    {
        if (!Active) return;
        Elapsed += dt;
        if (Elapsed < Duration) return;
        Active = false;
        Completed?.Invoke();
    }

    public void Interrupt(string reason)
    {
        if (!Active) return;
        Active = false;
        Interrupted?.Invoke(reason);
    }

    public void Reset() => Active = false;
}
