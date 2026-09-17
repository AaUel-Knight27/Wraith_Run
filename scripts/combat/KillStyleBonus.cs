using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// The single shared kill-scoring calculator every mode is meant to call through
/// (Complete Development Plan v2.0, Part 6 / FR-SC-01). It is deliberately pure C# with no Godot
/// types so the bonus table can be reasoned about (and verified) without running the engine.
///
/// The base kill value is owned by the game mode (Part 5 lists a different number per mode); this
/// calculator only adds the style bonuses on top of whatever base it is handed.
/// </summary>
public static class KillStyleBonus
{
    // Part 6 bonus table. Identical in every mode, no exceptions.
    public const int HeadshotBonus = 50;
    public const int HipFireBonus = 25;
    public const int NoScope360Bonus = 75;
    public const int VergeOfDeathBonus = 50;

    /// <summary>One-shot multi-kill is a killer-side event, not a victim-side one: it is added once
    /// per multi-kill event on top of the sum of every victim's own base kill value.</summary>
    public const int MultiKillBonus = 100;

    /// <summary>Assists go to the non-killing damager and never stack onto the killer's total.
    /// The floor is fixed by Part 6; above it the value scales with damage contribution.</summary>
    public const int AssistBonusMinimum = 25;

    /// <summary>Fraction of contributed damage paid out as assist points, above the fixed floor.</summary>
    public const float AssistDamageToPointsRatio = 0.25f;

    /// <summary>Stand-in base kill value until GameModeBase lands and each mode supplies its own
    /// (Part 5: e.g. 100 for the original modes, 150/100 for Duo Buggy roles).</summary>
    public const int DefaultBaseKillPoints = 100;

    /// <summary>"Verge of death" threshold - the killer's own HP below ~20% at the moment of the kill.</summary>
    public const float VergeOfDeathHealthFraction = 0.20f;

    /// <summary>360 no-scope: yaw swept in the window before the kill that reads as a full spin.
    /// Part 6 says "roughly ... 300-360 degrees", so 300 is the floor of that range.</summary>
    public const float NoScopeSpinDegrees = 300.0f;

    /// <summary>Rolling window the spin is measured over (FR-SC-03: "a rolling ~1s window").</summary>
    public const float NoScopeWindowSeconds = 1.0f;

    /// <summary>How close together two kills must land to count as one multi-kill event. The design
    /// doc does not name a number, so this is an explicit implementation assumption.</summary>
    public const float MultiKillWindowSeconds = 3.0f;

    /// <summary>
    /// Total points for one confirmed kill: base value plus every applicable style bonus, added
    /// together with no cap (FR-SC-02). Unknown/absent styles contribute nothing.
    /// </summary>
    public static int Evaluate(int basePoints, KillStyle styles)
    {
        int total = Math.Max(0, basePoints);
        if (Has(styles, KillStyle.Headshot)) total += HeadshotBonus;
        if (Has(styles, KillStyle.HipFire)) total += HipFireBonus;
        if (Has(styles, KillStyle.NoScope360)) total += NoScope360Bonus;
        if (Has(styles, KillStyle.VergeOfDeath)) total += VergeOfDeathBonus;
        return total;
    }

    /// <summary>Points a non-killing damager earns. Always at least the fixed floor, then scales with
    /// the damage that player actually contributed.</summary>
    public static int EvaluateAssist(float contributedDamage)
    {
        if (contributedDamage <= 0.0f) return 0;
        int scaled = (int)Math.Round(contributedDamage * AssistDamageToPointsRatio);
        return Math.Max(AssistBonusMinimum, scaled);
    }

    /// <summary>True if a single kill should count as a spin kill. Requires the aim-down-sights check
    /// to be passed in by the caller - a scoped shot is never a no-scope.</summary>
    public static bool IsFullSpin(bool aimingDownSights, float accumulatedYawDegrees) =>
        !aimingDownSights && accumulatedYawDegrees >= NoScopeSpinDegrees;

    /// <summary>Human-readable bonus list for the kill feed, e.g. "HEADSHOT + HIP-FIRE". Empty when
    /// the kill earned no style bonus at all.</summary>
    public static string Describe(KillStyle styles)
    {
        var labels = new List<string>(4);
        if (Has(styles, KillStyle.Headshot)) labels.Add("HEADSHOT");
        if (Has(styles, KillStyle.HipFire)) labels.Add("HIP-FIRE");
        if (Has(styles, KillStyle.NoScope360)) labels.Add("360 NO-SCOPE");
        if (Has(styles, KillStyle.VergeOfDeath)) labels.Add("VERGE OF DEATH");
        return string.Join(" + ", labels);
    }

    /// <summary>Full feed line for one kill, shared by every peer so all clients agree on the text.</summary>
    public static string DescribeKill(long killerPeerId, long victimPeerId, KillStyle styles, int totalPoints)
    {
        string bonus = Describe(styles);
        var text = new StringBuilder($"Player {killerPeerId} killed Player {victimPeerId}");
        if (bonus.Length > 0) text.Append(' ').Append(bonus);
        return text.Append("  +").Append(totalPoints).ToString();
    }

    private static bool Has(KillStyle styles, KillStyle flag) => (styles & flag) == flag;
}