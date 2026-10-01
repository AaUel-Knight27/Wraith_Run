using Godot;
using System.Collections.Generic;

/// <summary>The Fixer's real job: fix the car AND fight at the same time. Three waves of enemies shoot at the vehicle's
/// tires and at you. You can aim and fire while you hold the repair button (only leaving the car's side stops it), so
/// keep the car mobile between and during waves while you take the attackers down.</summary>
public partial class RepairUnderFireDrill : TrainingDrill
{
    private static readonly int[] WaveSizes = { 2, 3, 3 };

    private Transform3D _anchor;
    private readonly RandomNumberGenerator _rng = new();
    private TrainingCar _car = null!;
    private RepairSystem _repair = null!;
    private readonly List<TrainingActor> _enemies = new();
    private int _wave = -1;
    private bool _waveActive;
    private float _grace;
    private float _pause = 1.0f;
    private float _downtime;
    private float _liveTime;
    private int _kills;
    private int _total;

    public override string Objective => _waveActive
        ? $"Wave {_wave + 1}/{WaveSizes.Length}: enemies are shooting the car's tires. Hold [{TrainingInput.KeyText(TrainingInput.AbilityPrimary)}] beside the car to repair - and keep shooting back while you do."
        : "Wave clear. Finish the repairs before the next wave arrives.";

    public override string StatusLine => $"Car {(_car.IsImmobilized ? "IMMOBILE" : "mobile")}  HP {Mathf.CeilToInt(_car.Hp)}  tires {4 - _car.FlatTireCount}/4   charges {_repair.Charges}   kills {_kills}/{_total}";

    protected override void OnBegin()
    {
        _rng.Randomize();
        foreach (int n in WaveSizes) _total += n;
        _anchor = Session.Anchor(TrainingAnchors.CombatYard);
        Session.PlacePlayer(_anchor, new Vector3(4.0f, 0, 2.0f));

        _car = Session.SpawnCar(_anchor * new Vector3(0, 0, -8.0f), DrillHelpers.YawDegrees(_anchor), c => c.AllowEnterExit = false);
        _repair = Session.AddMechanic<RepairSystem>();
        _repair.Target = _car;
    }

    protected override void OnTick(float dt)
    {
        _liveTime += dt;
        if (_car.IsImmobilized) _downtime += dt;

        if (_car.IsDestroyed)
        {
            Finish(false, "The vehicle was destroyed.");
            return;
        }

        if (_waveActive) return;

        if (_wave >= 0 && _car.NeedsRepair && _grace > 0.0f && _repair.Charges > 0)
        {
            _grace -= dt;
            return;
        }
        _pause -= dt;
        if (_pause > 0.0f) return;

        if (_wave + 1 >= WaveSizes.Length)
        {
            float uptime = 1.0f - _downtime / Mathf.Max(1.0f, _liveTime);
            bool ok = uptime >= 0.4f && _kills >= _total - 2;
            Finish(ok, ok ? "The car kept moving and the attackers are down." : "The car spent too long dead in the road - or too many attackers survived.");
            return;
        }
        StartWave(_wave + 1);
    }

    public override void OnTraineeDowned() => Finish(false, "You were downed while fighting and fixing.");

    private void StartWave(int index)
    {
        _wave = index;
        _waveActive = true;
        _repair.SetCharges(Session.Kit.RepairCharges);
        int count = WaveSizes[index];
        Vector3 axis = _anchor.Basis * Vector3.Right;
        for (int i = 0; i < count; i++)
        {
            float x = (i - (count - 1) * 0.5f) * 14.0f + _rng.RandfRange(-3.0f, 3.0f);
            Vector3 world = _anchor * new Vector3(x, 0, -_rng.RandfRange(34.0f, 42.0f));
            TrainingActor enemy = Session.SpawnActor(TrainingActor.FactionType.Enemy, world, "Raider", a =>
            {
                a.Mode = TrainingActor.BehaviorType.Strafe;
                a.Anchor = world;
                a.Axis = axis;
                a.Amplitude = 3.0f;
                a.Period = _rng.RandfRange(2.8f, 3.8f);
                a.PhaseOffset = _rng.RandfRange(0.0f, Mathf.Tau);
                a.CanShoot = true;
                a.ShootsCars = true;
                a.Accuracy = 0.5f;
                a.ShotDamage = 4.0f;
                a.FireInterval = 0.9f;
                a.TellSeconds = 1.0f;
                a.ShootRange = 80.0f;
            });
            enemy.Died += OnEnemyDied;
            _enemies.Add(enemy);
        }
        Session.Toast($"Wave {index + 1}", new Color(1.0f, 0.7f, 0.4f), 1.6f);
    }

    private void OnEnemyDied(TrainingActor actor, ShotInfo? shot)
    {
        if (shot.HasValue) _kills++;
        foreach (TrainingActor e in _enemies)
            if (!e.IsDead) return;
        _enemies.Clear();
        _waveActive = false;
        _pause = 2.0f;
        _grace = 14.0f;
    }

    protected override float ComputeRating()
    {
        float uptime = 1.0f - _downtime / Mathf.Max(1.0f, _liveTime);
        float kills = _total > 0 ? (float)_kills / _total : 0.0f;
        float safety = Mathf.Clamp(1.0f - Session.Stats.DamageTaken / 120.0f, 0.0f, 1.0f);
        return 100.0f * (0.4f * Mathf.Clamp(uptime, 0.0f, 1.0f) + 0.35f * kills + 0.25f * safety);
    }

    protected override void BuildResults()
    {
        float uptime = 1.0f - _downtime / Mathf.Max(1.0f, _liveTime);
        Row("Car mobile", Percent(Mathf.Clamp(uptime, 0.0f, 1.0f)));
        Row("Attackers down", $"{_kills} / {_total}");
        Row("Damage taken", $"{Session.Stats.DamageTaken:0}");
        Row("Car HP at end", $"{Mathf.CeilToInt(_car.Hp)}");
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        float uptime = 1.0f - _downtime / Mathf.Max(1.0f, _liveTime);
        if (uptime < 0.6f) tips.Add("The car sat dead too long. Start a repair the moment a tire goes - then fire from beside the car while the channel runs.");
        if (_kills < _total) tips.Add("You can shoot while repairing. Killing the shooters is repair too: fewer bullets means fewer flat tires.");
        return tips.ToArray();
    }
}
