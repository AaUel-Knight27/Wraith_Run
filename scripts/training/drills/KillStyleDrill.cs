using Godot;
using System.Collections.Generic;

/// <summary>Practise the five kill-style bonuses on demand: headshot, hip-fire, 360 no-scope, multi-kill, and a kill on
/// the verge of death (the drill drops your health to 15% for that one). Styles stack, so combos are worth more.</summary>
public partial class KillStyleDrill : TrainingDrill
{
    private sealed class Step
    {
        public string Name = "";
        public string How = "";
        public KillStyle Required;
        public bool Multi;
        public bool Verge;
        public bool Done;
        public int Points;
    }

    private const float StepLimit = 30.0f;

    private readonly List<Step> _steps = new()
    {
        new Step { Name = "HEADSHOT", How = "Kill a target with a headshot.", Required = KillStyle.Headshot },
        new Step { Name = "HIP-FIRE", How = "Kill without aiming down sights (do not hold aim).", Required = KillStyle.HipFire },
        new Step { Name = "360 NO-SCOPE", How = "Spin a full 360 with the mouse without aiming, and get the kill as you finish the spin.", Required = KillStyle.NoScope360 },
        new Step { Name = "MULTI-KILL", How = "Two targets: kill both within 3 seconds.", Multi = true },
        new Step { Name = "VERGE OF DEATH", How = "Your health is at 15%. Get a kill anyway.", Required = KillStyle.VergeOfDeath, Verge = true },
    };

    private Transform3D _anchor;
    private int _index = -1;
    private float _stepTimer;
    private float _pause = 0.8f;
    private bool _active;
    private int _multiKills;
    private float _lastKillAt = -99.0f;
    private int _totalPoints;

    public override string Objective
    {
        get
        {
            if (_index < 0 || _index >= _steps.Count) return "Get ready.";
            return $"Style {_index + 1}/{_steps.Count}: {_steps[_index].Name}\n{_steps[_index].How}";
        }
    }

    public override string StatusLine => $"Bonus points earned: {_totalPoints}";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.AimRange);
        Session.PlacePlayer(_anchor);
        Session.Weapons.EquipByName("M4");
        Session.TargetKilled += OnTargetKilled;
    }

    protected override void OnEnd()
    {
        if (TrainingSession.Instance != null) Session.TargetKilled -= OnTargetKilled;
        if (IsInstanceValid(Session.Player)) TrainingDamage.FullHealTrainee(Session.PlayerHealth);
    }

    protected override void OnTick(float dt)
    {
        if (!_active)
        {
            _pause -= dt;
            if (_pause > 0.0f) return;
            if (_index + 1 >= _steps.Count)
            {
                int done = 0;
                foreach (Step s in _steps) if (s.Done) done++;
                Finish(done >= 3, done >= 3 ? "You can hit the bonuses on demand." : "Keep practising the ones you missed.");
                return;
            }
            StartStep(_index + 1);
            return;
        }

        _stepTimer += dt;
        if (_stepTimer >= StepLimit)
        {
            Session.Toast("Time - moving on", new Color(1.0f, 0.7f, 0.4f));
            EndStep(false);
        }
    }

    private void StartStep(int index)
    {
        _index = index;
        Step step = _steps[index];
        _stepTimer = 0.0f;
        _active = true;
        _multiKills = 0;
        _lastKillAt = -99.0f;

        Session.PlacePlayer(_anchor);
        if (step.Verge) TrainingDamage.SetTraineeHealthFraction(Session.PlayerHealth, 0.15f);
        else TrainingDamage.FullHealTrainee(Session.PlayerHealth);

        if (step.Multi)
        {
            SpawnTarget(new Vector3(-0.9f, 0, -15.0f));
            SpawnTarget(new Vector3(0.9f, 0, -15.0f));
        }
        else
        {
            float distance = step.Name == "HIP-FIRE" ? 8.0f : step.Name == "360 NO-SCOPE" ? 10.0f : 14.0f;
            SpawnTarget(new Vector3(0, 0, -distance));
        }
    }

    private void SpawnTarget(Vector3 local)
    {
        Session.SpawnTarget(_anchor * local, t => t.MaxHp = 60.0f);
    }

    private void OnTargetKilled(Node3D target, ShotInfo shot)
    {
        if (!_active || _index < 0) return;
        Step step = _steps[_index];
        int points = KillStyleBonus.Evaluate(KillStyleBonus.DefaultBaseKillPoints, shot.Styles);

        if (step.Multi)
        {
            float now = Elapsed;
            _multiKills = now - _lastKillAt <= KillStyleBonus.MultiKillWindowSeconds ? _multiKills + 1 : 1;
            _lastKillAt = now;
            if (_multiKills >= 2)
            {
                points += KillStyleBonus.MultiKillBonus;
                Complete(step, points);
                return;
            }

            // First kill of the pair: if no target is left standing and the window was missed, offer a fresh pair.
            int standing = 0;
            foreach (Node child in Session.Props.GetChildren())
                if (child is TrainingTarget t && !t.IsDown) standing++;
            if (standing == 0)
            {
                Session.Toast("Too slow - both within 3 seconds. Again.", new Color(1.0f, 0.75f, 0.4f), 2.0f);
                SpawnTarget(new Vector3(-0.9f, 0, -15.0f));
                SpawnTarget(new Vector3(0.9f, 0, -15.0f));
                _multiKills = 0;
            }
            return;
        }

        if ((shot.Styles & step.Required) == step.Required)
        {
            Complete(step, points);
        }
        else
        {
            Session.Toast($"Killed, but not a {step.Name.ToLowerInvariant()} ({KillStyleBonus.Describe(shot.Styles)}). Try again.", new Color(1.0f, 0.75f, 0.4f), 2.0f);
            // Put a fresh target up so the step can be retried within its time limit.
            SpawnTarget(new Vector3(0, 0, step.Name == "HIP-FIRE" ? -8.0f : step.Name == "360 NO-SCOPE" ? -10.0f : -14.0f));
        }
    }

    private void Complete(Step step, int points)
    {
        step.Done = true;
        step.Points = points;
        _totalPoints += points;
        Session.Toast($"{step.Name}! +{points} points", new Color(0.6f, 1.0f, 0.6f), 2.0f);
        EndStep(true);
    }

    private void EndStep(bool ok)
    {
        _active = false;
        _pause = 1.6f;
        _ = ok;
        foreach (Node child in Session.Props.GetChildren())
            if (child is TrainingTarget) child.QueueFree();
        TrainingDamage.FullHealTrainee(Session.PlayerHealth);
    }

    protected override float ComputeRating()
    {
        int done = 0;
        foreach (Step s in _steps) if (s.Done) done++;
        return 100.0f * done / _steps.Count;
    }

    protected override void BuildResults()
    {
        foreach (Step s in _steps) Row(s.Name, s.Done ? $"+{s.Points} pts" : "missed");
        Row("Total bonus points", _totalPoints.ToString());
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        foreach (Step s in _steps)
            if (!s.Done) tips.Add($"{s.Name}: {s.How}");
        if (tips.Count == 0) tips.Add("All five hit. In a real match they stack on one kill - a hip-fire headshot is worth more than either alone.");
        return tips.ToArray();
    }
}
