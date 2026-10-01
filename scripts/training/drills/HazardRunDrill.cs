using Godot;
using System.Collections.Generic;

/// <summary>A fuel-limited road run past spike strips. You have just enough fuel to finish - boost burns three times as
/// much - and three strips across the road will stop you dead if you drive over them. Trains fuel discipline and
/// reading the road ahead.</summary>
public partial class HazardRunDrill : TrainingDrill
{
    private const float RouteLength = 260.0f;
    private const float StartFuel = 17.0f;
    private const float ParSeconds = 24.0f;
    private const float TimeLimit = 70.0f;

    private Transform3D _anchor;
    private TrainingCar _car = null!;
    private bool _struck;
    private float _boostSeconds;
    private float _endFuel;

    public override string Objective => "Reach the finish line before the fuel runs out. SHIFT boosts (3x fuel). Steer around the SPIKE STRIPS - hitting one stops the car.";
    public override string StatusLine => $"Fuel {Mathf.CeilToInt(_car.Fuel)} / {StartFuel:0}   boost used {_boostSeconds:0.0}s";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.DrivingCourse);
        _car = Session.SpawnCar(_anchor * new Vector3(0, 0, 12.0f), DrillHelpers.YawDegrees(_anchor), c =>
        {
            c.PreferredSeat = TrainingCar.Seat.Driver;
            c.AllowEnterExit = false;
            c.Fuel = StartFuel;
        });
        _car.EnterSeat(Session, TrainingCar.Seat.Driver);

        float[] xs = { -3.0f, 3.5f, -2.5f };
        float[] zs = { -70.0f, -135.0f, -195.0f };
        for (int i = 0; i < 3; i++)
        {
            var strip = new SpikeStrip { HalfWidth = 2.6f };
            strip.StoppedVehicle += car => _struck = true;
            DrillHelpers.Place(Session, _anchor, strip, new Vector3(xs[i], 0, zs[i]));
        }

        Vector3 finish = new Vector3(0, 0.05f, -RouteLength);
        DrillHelpers.Place(Session, _anchor, TrainingUtil.GroundDisc(9.0f, new Color(0.3f, 1.0f, 0.4f), Vector3.Zero, 0.4f), finish);
        DrillHelpers.Place(Session, _anchor, TrainingUtil.Label("FINISH", Vector3.Zero, new Color(0.5f, 1.0f, 0.6f), 96, false), finish + new Vector3(0, 4.0f, 0));
    }

    protected override void OnEnd()
    {
        if (IsInstanceValid(_car))
        {
            _endFuel = _car.Fuel;
            _car.ReleasePlayer();
        }
    }

    protected override void OnTick(float dt)
    {
        if (Input.IsActionPressed("sprint") && _car.Fuel > 0.0f && _car.Drive.Throttle > 0.1f) _boostSeconds += dt;

        float carZ = (_anchor.AffineInverse() * _car.GlobalPosition).Z;
        if (_struck)
        {
            Finish(false, "Spike strip! All four tires are gone.");
            return;
        }
        if (carZ <= -RouteLength)
        {
            _endFuel = _car.Fuel;
            Finish(true, "Made it.");
            return;
        }
        if (_car.Fuel <= 0.0f && _car.CurrentSpeed < 0.5f)
        {
            Finish(false, "Out of fuel.");
            return;
        }
        if (Elapsed >= TimeLimit) Finish(false, "Out of time.");
    }

    protected override float ComputeRating()
    {
        float time = TimeScore(Elapsed, ParSeconds);
        float fuel = Mathf.Clamp(_endFuel / 5.0f, 0.0f, 1.0f) * 100.0f;
        float clean = _struck ? 0.0f : 100.0f;
        return time * 0.5f + fuel * 0.25f + clean * 0.25f;
    }

    protected override void BuildResults()
    {
        Row("Time", TrainingUtil.Clock(Elapsed));
        Row("Fuel left", $"{_endFuel:0.0} / {StartFuel:0}");
        Row("Boost used", $"{_boostSeconds:0.0} s");
        Row("Spike strips", _struck ? "hit one" : "avoided all");
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (_struck) tips.Add("Strips are marked and 5 m wide on a 14 m road. Pick your side early - swerving late is how you clip the edge.");
        if (_endFuel < 1.0f) tips.Add("You finished on fumes. Boost only on the long straights; every second of boost costs as much fuel as three seconds of driving.");
        if (Elapsed > ParSeconds * 1.3f) tips.Add("Slow. You have more fuel than you think if you skip the boost between strips.");
        return tips.ToArray();
    }
}
