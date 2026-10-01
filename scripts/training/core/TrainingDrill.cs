using Godot;
using System;
using System.Collections.Generic;

/// <summary>Catalogue entry for a drill: what the hub shows, and how to build it.</summary>
public sealed class DrillDefinition
{
    public string Id = "";
    public string Title = "";
    public TrainingTrack Track;
    public string Description = "";
    /// <summary>One-line win condition shown on the card and the HUD.</summary>
    public string Goal = "";
    public TrainingSkill[] Skills = Array.Empty<TrainingSkill>();
    /// <summary>Controls worth remembering for this drill, shown before it starts.</summary>
    public string Controls = "";
    public float ParSeconds = 60.0f;
    public Func<TrainingDrill> Create = null!;
}

/// <summary>
/// One training exercise. The session builds it, gives it a 3-2-1, then ticks it until it calls Finish().
///
/// To add a drill: derive from this, override OnBegin (spawn props with the Session helpers, place the player),
/// OnTick (run the rules), Objective (HUD text), ComputeRating (0-100) and BuildResults (the results screen rows),
/// then add one DrillDefinition line in DrillRegistry. Nothing else needs to change.
/// </summary>
public abstract partial class TrainingDrill : Node
{
    protected TrainingSession Session => TrainingSession.Instance!;

    public DrillDefinition Definition { get; private set; } = null!;
    public DrillOutcome Outcome { get; private set; } = DrillOutcome.Running;
    public bool IsLive { get; private set; }
    public float Elapsed { get; private set; }
    public float Rating { get; private set; }
    public string ResultMessage { get; private set; } = string.Empty;
    public readonly List<(string label, string value)> Results = new();

    /// <summary>Text for the HUD objective panel (may be multi-line).</summary>
    public abstract string Objective { get; }
    /// <summary>Optional one-line live status (score, round, etc.).</summary>
    public virtual string StatusLine => string.Empty;
    /// <summary>The callout the [G] key sends in this drill (null = the generic "Enemy spotted").</summary>
    public virtual CalloutType? ContextCallout => null;

    protected abstract void OnBegin();
    protected virtual void OnLive() { }
    protected virtual void OnTick(float dt) { }
    protected virtual void OnEnd() { }
    /// <summary>0-100. Called once, when the drill finishes.</summary>
    protected abstract float ComputeRating();
    protected abstract void BuildResults();
    public virtual string[] CoachingTips() => Array.Empty<string>();

    /// <summary>The trainee's HP hit 1: they were "downed". Default = the drill fails. Round-based drills override this.</summary>
    public virtual void OnTraineeDowned() => Finish(false, "You were downed.");

    public void Begin(DrillDefinition definition)
    {
        Definition = definition;
        Name = "Drill_" + definition.Id;
        OnBegin();
    }

    public void GoLive()
    {
        if (IsLive || Outcome != DrillOutcome.Running) return;
        IsLive = true;
        OnLive();
    }

    public void Abort()
    {
        if (Outcome != DrillOutcome.Running) return;
        Outcome = DrillOutcome.Aborted;
        IsLive = false;
        OnEnd();
    }

    protected void Finish(bool success, string message)
    {
        if (Outcome != DrillOutcome.Running) return;
        Outcome = success ? DrillOutcome.Succeeded : DrillOutcome.Failed;
        IsLive = false;
        ResultMessage = message;
        float raw = Mathf.Clamp(ComputeRating(), 0.0f, 100.0f);
        // A failed drill can still earn partial credit for the progress skills, but never a passing grade.
        Rating = success ? raw : Mathf.Min(raw, 45.0f);
        BuildResults();
        OnEnd();
        Session.OnDrillFinished(this);
    }

    public override void _Process(double delta)
    {
        if (Outcome != DrillOutcome.Running || !IsLive) return;
        float dt = (float)delta;
        Elapsed += dt;
        OnTick(dt);
    }

    // ---- helpers for results ----------------------------------------------------------------
    protected void Row(string label, string value) => Results.Add((label, value));
    protected static string Percent(float fraction) => $"{Mathf.RoundToInt(fraction * 100.0f)}%";

    /// <summary>Score 0-100 for "faster than par is full marks, twice par is zero".</summary>
    protected static float TimeScore(float seconds, float parSeconds)
    {
        if (parSeconds <= 0.0f) return 100.0f;
        return Mathf.Clamp(100.0f - (seconds - parSeconds) / parSeconds * 100.0f, 0.0f, 100.0f);
    }
}
