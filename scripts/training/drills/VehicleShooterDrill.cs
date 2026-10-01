using Godot;
using System.Collections.Generic;

/// <summary>Shoot from the shooter's seat while an AI driver takes the car down a route. Targets pop up beside the road
/// as you approach; a few are friendly silhouettes you must not hit. The car sways at speed, so your aim moves.</summary>
public partial class VehicleShooterDrill : TrainingDrill
{
    private const int TargetCount = 12;
    private const float TimeLimit = 75.0f;

    private sealed class Spawn
    {
        public float LocalZ;
        public float LocalX;
        public bool Friendly;
        public bool Spawned;
    }

    private Transform3D _anchor;
    private readonly RandomNumberGenerator _rng = new();
    private readonly List<Spawn> _spawns = new();
    private TrainingCar _car = null!;
    private int _resolved;
    private int _killed;

    public override string Objective => "You are the SHOOTER. Your driver follows the road. Hit the red targets as they pop up beside it - never the green ones.";
    public override string StatusLine => $"Down {_killed}   friendly hits {Session.Stats.FriendlyHits}   accuracy {Percent(Session.Stats.Accuracy)}";

    protected override void OnBegin()
    {
        _rng.Randomize();
        _anchor = Session.Anchor(TrainingAnchors.DrivingCourse);
        Session.Weapons.EquipByName("AK-47");

        float yaw = Mathf.RadToDeg(_anchor.Basis.GetEuler().Y);
        _car = Session.SpawnCar(_anchor * new Vector3(0, 0, 12.0f), yaw, c =>
        {
            c.AiDriverPresent = true;
            c.AutopilotEnabled = true;
            c.PreferredSeat = TrainingCar.Seat.Shooter;
            c.AllowEnterExit = false;
            c.CruiseSpeed = 9.0f;
            c.Fuel = c.FuelCapacity;
        });
        foreach (Vector3 waypoint in new[] { new Vector3(0, 0, -30), new Vector3(0, 0, -80), new Vector3(6, 0, -120), new Vector3(0, 0, -160), new Vector3(0, 0, -200) })
            _car.Route.Add(_anchor * waypoint);
        _car.EnterSeat(Session, TrainingCar.Seat.Shooter);

        for (int i = 0; i < TargetCount; i++)
        {
            bool friendly = i == 3 || i == 7 || i == 10;
            float side = _rng.Randf() < 0.5f ? -1.0f : 1.0f;
            _spawns.Add(new Spawn { LocalZ = -40.0f - i * 13.0f, LocalX = side * _rng.RandfRange(9.0f, 16.0f), Friendly = friendly });
        }
    }

    protected override void OnEnd()
    {
        if (IsInstanceValid(_car)) _car.ReleasePlayer();
    }

    protected override void OnTick(float dt)
    {
        float carZ = (_anchor.AffineInverse() * _car.GlobalPosition).Z;
        foreach (Spawn spawn in _spawns)
        {
            if (spawn.Spawned || carZ > spawn.LocalZ + 38.0f) continue;
            spawn.Spawned = true;
            bool friendly = spawn.Friendly;
            Session.SpawnTarget(_anchor * new Vector3(spawn.LocalX, 0, spawn.LocalZ), t =>
            {
                t.NoShoot = friendly;
                t.Lifetime = 5.5f;
                t.Killed += (target, shot) => { _killed++; _resolved++; };
                t.Expired += target => _resolved++;
            });
        }

        if (_car.RouteFinished || Elapsed >= TimeLimit)
        {
            int required = 7;
            Finish(_killed >= required, _killed >= required ? "Good shooting on the move." : "Too many targets slipped by.");
        }
    }

    protected override float ComputeRating()
    {
        TrainingStats s = Session.Stats;
        int enemies = TargetCount - 3;
        float kills = Mathf.Min(1.0f, (float)_killed / enemies);
        float accuracy = Mathf.Min(1.0f, s.Accuracy / 0.4f);
        float discipline = Mathf.Clamp(1.0f - s.FriendlyHits * 0.34f, 0.0f, 1.0f);
        return 100.0f * (0.6f * kills + 0.25f * accuracy + 0.15f * discipline);
    }

    protected override void BuildResults()
    {
        TrainingStats s = Session.Stats;
        Row("Targets down", $"{_killed} / {TargetCount - 3}");
        Row("Accuracy", Percent(s.Accuracy));
        Row("Friendly hits", s.FriendlyHits.ToString());
        Row("Shots fired", s.Shots.ToString());
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        TrainingStats s = Session.Stats;
        if (s.Accuracy < 0.3f) tips.Add("The car's motion pushes your aim around. Fire short bursts and re-centre between them instead of spraying.");
        if (s.FriendlyHits > 0) tips.Add("Green silhouettes are friendlies. Identify before you fire - a fast wrong shot costs more than a slow right one.");
        return tips.ToArray();
    }
}
