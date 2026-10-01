using Godot;
using System;
using System.Collections.Generic;

/// <summary>A planted, armed charge. Owned and timed by ExplosiveSystem.</summary>
public partial class PlacedCharge : Node3D
{
    public float Radius { get; set; } = 6.0f;
    public float FuseRemaining { get; set; }
    public bool AutoFuse { get; set; } = true;
    public bool IsSpent { get; set; }
    /// <summary>Callout-clock time at which FIRE IN THE HOLE was sent while this charge was live (-1 = never).</summary>
    public float CalledOutClock { get; set; } = -1.0f;
    public bool CalledOut => CalledOutClock >= 0.0f;
    /// <summary>Seconds of warning the callout gave before the blast (filled in at detonation).</summary>
    public float CalledOutLead { get; set; }

    private Label3D _label = null!;
    private MeshInstance3D _blink = null!;
    private float _clock;

    public override void _Ready()
    {
        AddToGroup("placed_charge");
        AddChild(TrainingUtil.Box(new Vector3(0.3f, 0.2f, 0.2f), new Color(0.2f, 0.2f, 0.2f), Vector3.Zero, "Brick"));
        _blink = TrainingUtil.Box(new Vector3(0.08f, 0.08f, 0.08f), new Color(1.0f, 0.2f, 0.1f), new Vector3(0, 0.14f, 0), "Blink");
        AddChild(_blink);
        _label = TrainingUtil.Label("", new Vector3(0, 0.9f, 0), new Color(1.0f, 0.6f, 0.2f), 36, true);
        AddChild(_label);
        AddChild(TrainingUtil.GroundDisc(Radius, new Color(1.0f, 0.35f, 0.15f), Vector3.Zero, 0.16f));
    }

    public override void _Process(double delta)
    {
        _clock += (float)delta;
        _blink.Visible = Mathf.Sin(_clock * 12.0f) > -0.2f;
        _label.Text = AutoFuse ? $"ARMED  {FuseRemaining:0.0}s" : "ARMED - detonate by hand";
    }
}
