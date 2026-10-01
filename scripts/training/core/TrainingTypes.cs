using Godot;
using System;

/// <summary>The five playable classes from the roster (CharacterData.CharacterClass) as training tracks,
/// plus the shared fundamentals. "Shooter" is the roster's Operator class - the player-facing name.</summary>
public enum TrainingTrack { Fundamentals, Shooter, Driver, Fixer, Medic, Demolisher }

/// <summary>What a drill trains. Progress is averaged per skill so the hub can point at the weakest one.</summary>
public enum TrainingSkill { Aim, Recoil, Movement, Awareness, Navigation, Timing, Comms, ClassSkill }

public enum DrillOutcome { Running, Succeeded, Failed, Aborted }

/// <summary>How a seat swap is gated. Straight from FR-CA-04: full stop for Modes 1-5, brief slow-down
/// for Convoy Assault and 2v2 Duo Buggy.</summary>
public enum SeatSwapRule { FullStop, BriefSlowdown }

public static class TrainingTrackInfo
{
    public static string Title(TrainingTrack track) => track switch
    {
        TrainingTrack.Fundamentals => "FUNDAMENTALS",
        TrainingTrack.Shooter => "SHOOTER",
        TrainingTrack.Driver => "DRIVER",
        TrainingTrack.Fixer => "FIXER",
        TrainingTrack.Medic => "MEDIC",
        TrainingTrack.Demolisher => "DEMOLISHER",
        _ => track.ToString().ToUpperInvariant(),
    };

    public static string Blurb(TrainingTrack track) => track switch
    {
        TrainingTrack.Fundamentals => "Movement, aim, recoil, map reading and reading enemies. Everyone starts here.",
        TrainingTrack.Shooter => "Every weapon, reload discipline, kill styles, and shooting from a moving car.",
        TrainingTrack.Driver => "Handling, braking, fuel discipline, and the seat swap that decides fights.",
        TrainingTrack.Fixer => "Get the car moving again - and keep the enemy off it while you do.",
        TrainingTrack.Medic => "Triage, heal channels under fire, smoke cover, and answering your team calls.",
        TrainingTrack.Demolisher => "Charges, breaches, spike strips, and the callouts that keep friends alive.",
        _ => string.Empty,
    };

    public static string Icon(TrainingTrack track) => track switch
    {
        TrainingTrack.Fundamentals => "crosshair",
        TrainingTrack.Shooter => "rifle",
        TrainingTrack.Driver => "map",
        TrainingTrack.Fixer => "helmet-gear",
        TrainingTrack.Medic => "users",
        TrainingTrack.Demolisher => "bot",
        _ => "user",
    };

    /// <summary>Roster class this track corresponds to (Fundamentals has none).</summary>
    public static CharacterData.CharacterClass? RosterClass(TrainingTrack track) => track switch
    {
        TrainingTrack.Shooter => CharacterData.CharacterClass.Operator,
        TrainingTrack.Driver => CharacterData.CharacterClass.Driver,
        TrainingTrack.Fixer => CharacterData.CharacterClass.Fixer,
        TrainingTrack.Medic => CharacterData.CharacterClass.Medic,
        TrainingTrack.Demolisher => CharacterData.CharacterClass.Demolisher,
        _ => null,
    };
}

/// <summary>Grades shown on the results screen.</summary>
public static class TrainingGrade
{
    public static string Letter(float rating) =>
        rating >= 95.0f ? "S" : rating >= 85.0f ? "A" : rating >= 70.0f ? "B" : rating >= 50.0f ? "C" : "D";

    public static Color GradeColor(string letter) => letter switch
    {
        "S" => new Color(1.0f, 0.85f, 0.25f),
        "A" => new Color(0.45f, 0.9f, 0.45f),
        "B" => new Color(0.5f, 0.8f, 1.0f),
        "C" => new Color(0.95f, 0.75f, 0.35f),
        _ => new Color(0.95f, 0.35f, 0.3f),
    };
}
