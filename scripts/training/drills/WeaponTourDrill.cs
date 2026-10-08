using Godot;
using System.Collections.Generic;

/// <summary>One stage per weapon in the loadout. Each stage forces that weapon and sets targets at the range it is meant
/// for - carbines far, pistols mid, the shotgun close, the knife at arm's length, the bazooka on soft targets - so you
/// feel what each one does. Scored on kills, time and accuracy per weapon.</summary>
public partial class WeaponTourDrill : TrainingDrill
{
    private sealed class Stage
    {
        public string Weapon = "";
        public int Count;
        public float MinDistance;
        public float MaxDistance;
        public float Limit;
        public float TargetHp = 100.0f;
        public string Tip = "";
        // results
        public int Killed;
        public float Time;
        public int Pellets;
        public int Hits;
        public bool Done;
    }

    private static readonly Stage[] Template =
    {
        new Stage { Weapon = "AK-47", Count = 5, MinDistance = 15, MaxDistance = 30, Limit = 22, Tip = "Full-auto rifle: burst, don't hold the trigger down." },
        new Stage { Weapon = "M4", Count = 5, MinDistance = 15, MaxDistance = 30, Limit = 22, Tip = "Steadier than the AK - good for tracking." },
        new Stage { Weapon = "P90", Count = 5, MinDistance = 10, MaxDistance = 24, Limit = 22, Tip = "High rate of fire, big magazine: strong at mid range." },
        new Stage { Weapon = "Glock 19", Count = 4, MinDistance = 8, MaxDistance = 16, Limit = 22, Tip = "Fast handling backup - aim for the head." },
        new Stage { Weapon = "Desert Eagle", Count = 4, MinDistance = 10, MaxDistance = 20, Limit = 24, Tip = "Slow and heavy: one clean hit matters." },
        new Stage { Weapon = "Shotgun", Count = 4, MinDistance = 4, MaxDistance = 9, Limit = 22, Tip = "Deadly up close, useless far away." },
    };

    private readonly List<Stage> _stages = new();
    private readonly RandomNumberGenerator _rng = new();
    private Transform3D _anchor;
    private int _index = -1;
    private float _stageTimer;
    private float _pause = 0.8f;
    private bool _active;
    private bool _refillPending;
    private int _pelletsAtStart;
    private int _hitsAtStart;
    private int _remaining;

    public override string Objective
    {
        get
        {
            if (_index < 0 || _index >= _stages.Count) return "The weapon tour starts in a moment.";
            Stage stage = _stages[_index];
            return $"Stage {_index + 1}/{_stages.Count}: {stage.Weapon}. Eliminate {stage.Count} targets.\n{stage.Tip}";
        }
    }

    public override string StatusLine => _active ? $"Targets left {_remaining}   stage time {Mathf.Max(0.0f, _stages[_index].Limit - _stageTimer):0}s" : "";

    protected override void OnBegin()
    {
        _rng.Randomize();
        _anchor = Session.Anchor(TrainingAnchors.AimRange);
        Session.PlacePlayer(_anchor);
        foreach (Stage t in Template)
            _stages.Add(new Stage { Weapon = t.Weapon, Count = t.Count, MinDistance = t.MinDistance, MaxDistance = t.MaxDistance, Limit = t.Limit, TargetHp = t.TargetHp, Tip = t.Tip });
    }

    protected override void OnTick(float dt)
    {
        if (_refillPending)
        {
            Session.Weapons.RefillAmmo();
            _refillPending = false;
        }

        if (!_active)
        {
            _pause -= dt;
            if (_pause > 0.0f) return;
            if (_index + 1 >= _stages.Count)
            {
                int cleared = 0;
                foreach (Stage s in _stages) if (s.Killed >= s.Count) cleared++;
                Finish(cleared >= 6, cleared >= 6 ? "You handled the whole arsenal." : "A few weapons still feel foreign - see the per-weapon rows.");
                return;
            }
            StartStage(_index + 1);
            return;
        }

        _stageTimer += dt;
        if (_stageTimer >= _stages[_index].Limit) EndStage("Time");
    }

    private void StartStage(int index)
    {
        _index = index;
        Stage stage = _stages[index];
        _stageTimer = 0.0f;
        _active = true;
        _remaining = stage.Count;
        _pelletsAtStart = Session.Stats.Pellets;
        _hitsAtStart = Session.Stats.Hits;

        Session.Weapons.EquipByName(stage.Weapon);
        _refillPending = true;
        Session.PlacePlayer(_anchor);

        for (int i = 0; i < stage.Count; i++)
        {
            Vector3 local;
            if (stage.MinDistance < 2.5f)
            {
                float angle = Mathf.DegToRad(-32.0f + i * 32.0f);
                local = new Vector3(Mathf.Sin(angle) * stage.MinDistance, 0, -Mathf.Cos(angle) * stage.MinDistance);
            }
            else
            {
                float distance = _rng.RandfRange(stage.MinDistance, stage.MaxDistance);
                local = new Vector3(_rng.RandfRange(-0.35f, 0.35f) * distance, 0, -distance);
            }
            float hp = stage.TargetHp;
            Session.SpawnTarget(_anchor * local, t =>
            {
                t.MaxHp = hp;
                t.Killed += OnKilled;
            });
        }
        Session.Toast(stage.Weapon.ToUpperInvariant(), new Color(1.0f, 0.85f, 0.5f), 1.6f);
    }

    private void OnKilled(TrainingTarget target, ShotInfo shot)
    {
        if (!_active) return;
        _stages[_index].Killed++;
        _remaining--;
        if (_remaining <= 0) EndStage("Cleared");
    }

    private void EndStage(string reason)
    {
        if (!_active) return;
        Stage stage = _stages[_index];
        stage.Time = _stageTimer;
        stage.Pellets = Session.Stats.Pellets - _pelletsAtStart;
        stage.Hits = Session.Stats.Hits - _hitsAtStart;
        stage.Done = true;
        _active = false;
        _pause = 1.4f;
        _ = reason;

        // Remove leftover targets so the next stage starts clean.
        foreach (Node child in Session.Props.GetChildren())
            if (child is TrainingTarget) child.QueueFree();
    }

    protected override float ComputeRating()
    {
        float total = 0.0f;
        foreach (Stage s in _stages)
        {
            float kills = (float)s.Killed / s.Count;
            float time = s.Done ? Mathf.Clamp(1.0f - s.Time / s.Limit, 0.0f, 1.0f) : 0.0f;
            float accuracy = s.Pellets > 0 ? Mathf.Min(1.0f, ((float)s.Hits / s.Pellets) / 0.6f) : (s.Weapon == "Karambit" ? 1.0f : 0.0f);
            total += 100.0f * (0.6f * kills + 0.2f * time + 0.2f * accuracy);
        }
        return total / _stages.Count;
    }

    protected override void BuildResults()
    {
        foreach (Stage s in _stages)
        {
            string acc = s.Pellets > 0 ? $"  acc {Percent((float)s.Hits / s.Pellets)}" : "";
            Row(s.Weapon, s.Done ? $"{s.Killed}/{s.Count} in {s.Time:0.0}s{acc}" : "not reached");
        }
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        foreach (Stage s in _stages)
            if (s.Done && s.Killed < s.Count) tips.Add($"{s.Weapon}: {s.Tip}");
        if (tips.Count == 0) tips.Add("Every weapon cleared. Retry for speed, or move on to the reload and kill-style drills.");
        return tips.ToArray();
    }
}
