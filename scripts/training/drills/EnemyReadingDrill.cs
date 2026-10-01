using Godot;
using System.Collections.Generic;

/// <summary>Six rounds, one enemy behaviour each: a strafer, a peeker (with a tell), a crosser, a flanker, a rusher,
/// then a strafer and a peeker together. The first two rounds label what you are reading; after that you read
/// it yourself. Score is rounds won, time-to-kill, and damage taken.</summary>
public partial class EnemyReadingDrill : TrainingDrill
{
    private const int RoundCount = 6;
    private const float RoundLimit = 16.0f;
    private const float Intermission = 2.2f;

    private static readonly string[] Names = { "STRAFER", "PEEKER", "CROSSER", "FLANKER", "RUSHER", "STRAFER + PEEKER" };
    private static readonly string[] Hints =
    {
        "Hint: he moves in a steady rhythm. Aim where he is GOING, and fire as he changes direction.",
        "Hint: a ! flashes before he steps out. Pre-aim the pillar edge and shoot the instant he shows.",
        "Hint: he crosses fast. Lead him - your shot needs to arrive where he will be.",
        "", "", "",
    };

    private Transform3D _anchor;
    private readonly RandomNumberGenerator _rng = new();
    private readonly List<TrainingActor> _actors = new();
    private readonly Dictionary<TrainingActor, float> _exposedAt = new();
    private int _round = -1;
    private float _roundTimer;
    private float _pause = 0.8f;
    private bool _roundActive;
    private int _roundsWon;
    private int _downs;

    public override string Objective
    {
        get
        {
            if (_round < 0) return "Get ready.";
            string head = $"Round {_round + 1}/{RoundCount}: {Names[_round]}";
            return string.IsNullOrEmpty(Hints[_round]) ? head + "\nRead the enemy yourself - no labels." : head + "\n" + Hints[_round];
        }
    }

    public override string StatusLine => $"Won {_roundsWon}   time left {(_roundActive ? Mathf.Max(0.0f, _roundTimer) : 0.0f):0}s   dmg taken {Session.Stats.DamageTaken:0}";

    protected override void OnBegin()
    {
        _rng.Randomize();
        _anchor = Session.Anchor(TrainingAnchors.CombatYard);
        Session.PlacePlayer(_anchor);
    }

    protected override void OnTick(float dt)
    {
        if (!_roundActive)
        {
            _pause -= dt;
            if (_pause > 0.0f) return;
            if (_round + 1 >= RoundCount)
            {
                Finish(_roundsWon >= 4, _roundsWon >= 4 ? "You read them well." : "Too many enemies got away with it - see the coach notes.");
                return;
            }
            StartRound(_round + 1);
            return;
        }

        _roundTimer -= dt;
        if (_roundTimer <= 0.0f)
        {
            Session.Toast("Too slow - he got away", new Color(1.0f, 0.7f, 0.4f));
            EndRound(false);
        }
    }

    public override void OnTraineeDowned()
    {
        if (!_roundActive) return;
        _downs++;
        Session.Toast("You were downed - next round", new Color(1.0f, 0.5f, 0.4f));
        EndRound(false);
        TrainingDamage.FullHealTrainee(Session.PlayerHealth);
    }

    private TrainingActor Enemy(Vector3 local, System.Action<TrainingActor>? configure = null)
    {
        TrainingActor actor = Session.SpawnActor(TrainingActor.FactionType.Enemy, _anchor * local, "Bot", a =>
        {
            a.CanShoot = true;
            a.Accuracy = 0.4f;
            a.ShotDamage = 5.0f;
            a.FireInterval = 0.9f;
            a.TellSeconds = 0.6f;
            a.ShootRange = 80.0f;
            a.MoveSpeed = 4.5f;
            configure?.Invoke(a);
        });
        actor.Died += OnActorDied;
        actor.BecameExposed += a => _exposedAt[a] = a.AliveSeconds;
        _actors.Add(actor);
        return actor;
    }

    private void StartRound(int index)
    {
        _round = index;
        _roundTimer = RoundLimit;
        _roundActive = true;
        bool hints = index < 2;
        Vector3 right = _anchor.Basis * Vector3.Right;
        float side = _rng.Randf() < 0.5f ? -1.0f : 1.0f;

        switch (index)
        {
            case 0:
                AddStrafer(new Vector3(0, 0, -26.0f));
                break;
            case 1:
                AddPeeker(new Vector3(0, 0, -24.0f), side, hints);
                break;
            case 2:
                Enemy(new Vector3(-26.0f * side, 0, -24.0f), a =>
                {
                    a.Mode = TrainingActor.BehaviorType.MoveTo;
                    a.MoveSpeed = 6.5f;
                    a.Destination = _anchor * new Vector3(26.0f * side, 0, -24.0f);
                    a.Accuracy = 0.3f;
                }).Arrived += a =>
                {
                    Session.Toast("He crossed - lead him more", new Color(1.0f, 0.7f, 0.4f));
                    EndRound(false);
                };
                break;
            case 3:
                Enemy(new Vector3(34.0f * side, 0, -16.0f), a =>
                {
                    a.Mode = TrainingActor.BehaviorType.MoveTo;
                    a.Destination = _anchor * new Vector3(13.0f * side, 0, 2.0f);
                    a.NextMode = TrainingActor.BehaviorType.Rush;
                    a.MoveSpeed = 5.0f;
                });
                Session.Toast("Contact - somewhere around you", new Color(1.0f, 0.85f, 0.4f));
                break;
            case 4:
                Enemy(new Vector3(0, 0, -42.0f), a =>
                {
                    a.Mode = TrainingActor.BehaviorType.Rush;
                    a.MoveSpeed = 5.6f;
                    a.Accuracy = 0.3f;
                });
                break;
            default:
                AddStrafer(new Vector3(-10.0f * side, 0, -30.0f));
                AddPeeker(new Vector3(10.0f * side, 0, -24.0f), -side, false);
                break;
        }
        _ = right;
    }

    private void AddStrafer(Vector3 local)
    {
        Vector3 axis = _anchor.Basis * Vector3.Right;
        Vector3 world = _anchor * local;
        Enemy(local, a =>
        {
            a.Mode = TrainingActor.BehaviorType.Strafe;
            a.Anchor = world;
            a.Axis = axis;
            a.Amplitude = 5.0f;
            a.Period = 3.2f;
            a.PhaseOffset = _rng.RandfRange(0.0f, Mathf.Tau);
        });
    }

    private void AddPeeker(Vector3 pillarLocal, float side, bool showTell)
    {
        StaticBody3D pillar = TrainingUtil.SolidBox(new Vector3(1.4f, 2.8f, 1.0f), new Color(0.35f, 0.36f, 0.4f), Vector3.Zero, "Cover");
        Session.Props.AddChild(pillar);
        pillar.GlobalPosition = _anchor * (pillarLocal + new Vector3(0, 1.4f, 0));
        pillar.GlobalRotation = new Vector3(0, _anchor.Basis.GetEuler().Y, 0);

        Vector3 behind = pillarLocal + new Vector3(0, 0, -1.4f);
        Vector3 world = _anchor * behind;
        Vector3 axis = _anchor.Basis * new Vector3(side, 0, 0);
        Enemy(behind, a =>
        {
            a.Mode = TrainingActor.BehaviorType.Peek;
            a.Anchor = world;
            a.Axis = axis;
            a.Amplitude = 1.8f;
            a.HideSeconds = 1.5f;
            a.ExposeSeconds = 1.6f;
            a.TellSeconds = 0.7f;
            a.ShowsPeekTell = showTell;
        });
    }

    private void OnActorDied(TrainingActor actor, ShotInfo? shot)
    {
        float since = _exposedAt.TryGetValue(actor, out float exposed) ? actor.AliveSeconds - exposed : actor.AliveSeconds;
        if (shot.HasValue) Session.Stats.ReactionTimes.Add(Mathf.Max(0.05f, since));

        foreach (TrainingActor other in _actors)
            if (!other.IsDead) return;
        if (_roundActive)
        {
            _roundsWon++;
            Session.Toast("Round won", new Color(0.6f, 1.0f, 0.6f), 1.4f);
            EndRound(true);
        }
    }

    private void EndRound(bool won)
    {
        if (!_roundActive) return;
        _roundActive = false;
        _pause = Intermission;
        foreach (TrainingActor actor in _actors)
            if (IsInstanceValid(actor) && !actor.IsDead) actor.QueueFree();
        _actors.Clear();
        _exposedAt.Clear();
        _ = won;
    }

    protected override float ComputeRating()
    {
        TrainingStats s = Session.Stats;
        float rounds = (float)_roundsWon / RoundCount;
        float speed = s.ReactionTimes.Count == 0 ? 0.0f : Mathf.Clamp(1.0f - (s.AverageReaction - 1.5f) / 2.5f, 0.0f, 1.0f);
        float safety = Mathf.Clamp(1.0f - s.DamageTaken / 120.0f, 0.0f, 1.0f);
        return 100.0f * (0.6f * rounds + 0.25f * speed + 0.15f * safety);
    }

    protected override void BuildResults()
    {
        TrainingStats s = Session.Stats;
        Row("Rounds won", $"{_roundsWon} / {RoundCount}");
        Row("Average time to kill", s.ReactionTimes.Count > 0 ? $"{s.AverageReaction:0.00} s" : "-");
        Row("Damage taken", $"{s.DamageTaken:0}");
        Row("Times downed", _downs.ToString());
        Row("Accuracy", Percent(s.Accuracy));
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        TrainingStats s = Session.Stats;
        if (_roundsWon < RoundCount) tips.Add("Every enemy here follows a pattern. When you lose a round, name the pattern (rhythm, peek, cross, flank, rush) before you retry.");
        if (s.ReactionTimes.Count > 0 && s.AverageReaction > 2.5f) tips.Add("Your kills are slow. Pre-aim where the enemy will appear instead of reacting to where he already is.");
        if (s.DamageTaken > 80.0f) tips.Add("You took a lot of fire. Enemies wait a beat before shooting - use that tell to shoot first, or break line of sight.");
        return tips.ToArray();
    }
}
