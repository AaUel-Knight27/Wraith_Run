using Godot;
using System.Collections.Generic;

/// <summary>
/// Local-only training history: best grade, best time and run count per drill, plus a running average per skill so
/// the hub can say "your weakest area is Recoil - try these".
///
/// Saved to user://training_progress.cfg. It is a practice aid only: it never touches match stats, ranks or
/// unlocks (the design has no competitive progression - this is the same kind of purely local convenience as the
/// settings file).
/// </summary>
public static class TrainingProgress
{
    private const string Path = "user://training_progress.cfg";
    private static ConfigFile? _file;

    private static ConfigFile Cfg
    {
        get
        {
            if (_file != null) return _file;
            _file = new ConfigFile();
            _file.Load(Path);   // a missing file just leaves it empty
            return _file;
        }
    }

    private static string DrillSection(string id) => "drill_" + id;
    private static string SkillSection(TrainingSkill skill) => "skill_" + skill;

    public static float BestRating(string id) => Cfg.GetValue(DrillSection(id), "best", 0.0f).AsSingle();
    public static float LastRating(string id) => Cfg.GetValue(DrillSection(id), "last", 0.0f).AsSingle();
    public static int Runs(string id) => Cfg.GetValue(DrillSection(id), "runs", 0).AsInt32();
    /// <summary>Fastest successful run in seconds, or 0 if never completed.</summary>
    public static float BestTime(string id) => Cfg.GetValue(DrillSection(id), "time", 0.0f).AsSingle();

    public static void RecordRun(string id, float rating, float seconds, bool success, TrainingSkill[] skills)
    {
        string section = DrillSection(id);
        Cfg.SetValue(section, "runs", Runs(id) + 1);
        Cfg.SetValue(section, "last", rating);
        if (rating > BestRating(id)) Cfg.SetValue(section, "best", rating);
        float bestTime = BestTime(id);
        if (success && (bestTime <= 0.0f || seconds < bestTime)) Cfg.SetValue(section, "time", seconds);

        foreach (TrainingSkill skill in skills)
        {
            string skillSection = SkillSection(skill);
            int count = Cfg.GetValue(skillSection, "count", 0).AsInt32();
            float average = Cfg.GetValue(skillSection, "avg", 0.0f).AsSingle();
            // Recent runs matter more than old ones: an exponential moving average once there is history.
            average = count == 0 ? rating : average * 0.7f + rating * 0.3f;
            Cfg.SetValue(skillSection, "count", count + 1);
            Cfg.SetValue(skillSection, "avg", average);
        }
        Cfg.Save(Path);
    }

    /// <summary>Average rating for a skill, or null if it has never been practised.</summary>
    public static float? SkillAverage(TrainingSkill skill)
    {
        string section = SkillSection(skill);
        if (Cfg.GetValue(section, "count", 0).AsInt32() == 0) return null;
        return Cfg.GetValue(section, "avg", 0.0f).AsSingle();
    }

    public static TrainingSkill? WeakestSkill()
    {
        TrainingSkill? weakest = null;
        float lowest = float.MaxValue;
        foreach (TrainingSkill skill in System.Enum.GetValues<TrainingSkill>())
        {
            float? average = SkillAverage(skill);
            if (average == null || average.Value >= lowest) continue;
            lowest = average.Value;
            weakest = skill;
        }
        return weakest;
    }

    public static void ResetAll()
    {
        _file = new ConfigFile();
        _file.Save(Path);
    }

    /// <summary>What to practise next: a drill for the weakest skill that is not yet passed; else the first
    /// never-attempted drill; else null (everything is at least a B).</summary>
    public static DrillDefinition? Recommend(IReadOnlyList<DrillDefinition> all)
    {
        TrainingSkill? weakest = WeakestSkill();
        if (weakest.HasValue)
        {
            foreach (DrillDefinition drill in all)
                if (System.Array.IndexOf(drill.Skills, weakest.Value) >= 0 && BestRating(drill.Id) < 85.0f) return drill;
        }
        foreach (DrillDefinition drill in all)
            if (Runs(drill.Id) == 0) return drill;
        foreach (DrillDefinition drill in all)
            if (BestRating(drill.Id) < 85.0f) return drill;
        return null;
    }
}
