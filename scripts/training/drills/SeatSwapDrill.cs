using Godot;
using System.Collections.Generic;

/// <summary>
/// The seat swap, both ways it exists in the design: a FULL STOP (under 2 m/s) and a BRIEF SLOWDOWN (under 8 m/s).
/// You drive to the zone and stop to swap to the shooter's seat (2 s swap), clear targets from the parked car; then the
/// AI driver takes you round a circuit while you shoot, and you must call SLOW DOWN to your driver and swap back.
/// Trains the driver-shooter hand-off and the callout that makes it safe.
/// </summary>
public partial class SeatSwapDrill : TrainingDrill
{
    private enum Phase { DriveToZone, ShootParked, ShootMoving, SlowAndSwapBack }

    private Transform3D _anchor;
    private TrainingCar _car = null!;
    private Phase _phase = Phase.DriveToZone;
    private int _refused;
    private int _swaps;
    private int _remaining;
    private float _phaseStartedAt;
    private float _fullStopAt;
    private float _briefAt;

    public override CalloutType? ContextCallout => _phase == Phase.SlowAndSwapBack || _phase == Phase.ShootMoving ? CalloutType.SlowDown : null;

    public override string Objective => _phase switch
    {
        Phase.DriveToZone => $"Drive to the SWAP ZONE, stop (under {TrainingKit.FullStopSpeed:0} m/s) and press [{TrainingInput.KeyText(TrainingInput.Interact)}] to swap to the shooter's seat.",
        Phase.ShootParked => "You are the SHOOTER. The car is parked. Eliminate the targets.",
        Phase.ShootMoving => $"Your driver is moving. Shoot the new targets from the moving car. ({_remaining} left)",
        _ => $"Now call SLOW DOWN [{TrainingInput.KeyText(TrainingInput.CalloutQuick)}] to your driver and, once under {TrainingKit.BriefSlowdownSpeed:0} m/s, press [{TrainingInput.KeyText(TrainingInput.Interact)}] to swap back.",
    };

    public override string StatusLine => $"Swaps {_swaps}/2   refused {_refused}";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.DrivingCourse);
        _car = Session.SpawnCar(_anchor * new Vector3(0, 0, 12.0f), DrillHelpers.YawDegrees(_anchor), c =>
        {
            c.PreferredSeat = TrainingCar.Seat.Driver;
            c.AiShooterPresent = true;
            c.AllowEnterExit = false;
            c.AllowSwap = true;
            c.SwapRule = SeatSwapRule.FullStop;
            c.CruiseSpeed = 8.5f;
            c.LoopRoute = true;
        });
        foreach (Vector3 point in new[] { new Vector3(0, 0, -70), new Vector3(22, 0, -95), new Vector3(0, 0, -122), new Vector3(-22, 0, -95) })
            _car.Route.Add(_anchor * point);
        _car.EnterSeat(Session, TrainingCar.Seat.Driver);
        _car.SeatSwapped += OnSeatSwapped;
        _car.SwapRefused += (c, limit) => _refused++;

        DrillHelpers.Place(Session, _anchor, TrainingUtil.GroundDisc(7.0f, new Color(1.0f, 0.8f, 0.3f), Vector3.Zero, 0.4f), new Vector3(0, 0.05f, -40.0f));
        DrillHelpers.Place(Session, _anchor, TrainingUtil.Label("SWAP ZONE", Vector3.Zero, new Color(1.0f, 0.85f, 0.4f), 80, false), new Vector3(0, 4.0f, -40.0f));
    }

    protected override void OnEnd()
    {
        if (IsInstanceValid(_car)) _car.ReleasePlayer();
    }

    protected override void OnTick(float dt)
    {
        if (Elapsed > 240.0f) Finish(false, "Out of time.");
    }

    private void OnSeatSwapped(TrainingCar car, TrainingCar.Seat seat)
    {
        _swaps++;
        if (_phase == Phase.DriveToZone && seat == TrainingCar.Seat.Shooter)
        {
            _fullStopAt = Elapsed;
            _phase = Phase.ShootParked;
            car.Halted = true;
            SpawnEnemies(new[] { new Vector3(-12, 0, -72), new Vector3(0, 0, -78), new Vector3(12, 0, -72) });
        }
        else if (_phase == Phase.SlowAndSwapBack && seat == TrainingCar.Seat.Driver)
        {
            _briefAt = Elapsed;
            Finish(true, "Both swaps done.");
        }
    }

    private void SpawnEnemies(Vector3[] locals)
    {
        _remaining = locals.Length;
        foreach (Vector3 local in locals)
        {
            TrainingActor dummy = Session.SpawnActor(TrainingActor.FactionType.Enemy, _anchor * local, "Dummy", a =>
            {
                a.MaxHp = 50.0f;
                a.CanShoot = false;
                a.Mode = TrainingActor.BehaviorType.Idle;
            });
            dummy.Died += OnDummyDied;
        }
    }

    private void OnDummyDied(TrainingActor actor, ShotInfo? shot)
    {
        _remaining--;
        if (_remaining > 0) return;

        if (_phase == Phase.ShootParked)
        {
            _phase = Phase.ShootMoving;
            _car.SwapRule = SeatSwapRule.BriefSlowdown;
            _car.Halted = false;
            SpawnEnemies(new[] { new Vector3(40, 0, -100), new Vector3(-40, 0, -100), new Vector3(0, 0, -150) });
            Session.Toast("Driver is rolling - shoot from the moving car", new Color(0.7f, 0.9f, 1.0f), 2.5f);
        }
        else if (_phase == Phase.ShootMoving)
        {
            _phase = Phase.SlowAndSwapBack;
            Session.Toast("Targets down - call SLOW DOWN, then swap back", new Color(1.0f, 0.85f, 0.4f), 3.0f);
        }
    }

    protected override float ComputeRating()
    {
        float clean = Mathf.Clamp(100.0f - _refused * 15.0f, 0.0f, 100.0f);
        float time = TimeScore(Elapsed, 100.0f);
        float progress = _swaps / 2.0f * 100.0f;
        return progress * 0.4f + clean * 0.3f + time * 0.3f;
    }

    protected override void BuildResults()
    {
        Row("Seat swaps completed", $"{_swaps} / 2");
        Row("Swaps refused (too fast)", _refused.ToString());
        Row("Total time", TrainingUtil.Clock(Elapsed));
        if (_fullStopAt > 0.0f) Row("Reached the full-stop swap at", TrainingUtil.Clock(_fullStopAt));
        if (_briefAt > 0.0f) Row("Finished the brief-slowdown swap at", TrainingUtil.Clock(_briefAt));
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (_refused > 0) tips.Add("A refused swap means you pressed the key while too fast. Ease off early - the 2 m/s full stop takes longer to reach than it feels.");
        tips.Add("During a real swap you are helpless for 2 seconds. Do it behind cover, after telling your team, never mid-fight.");
        return tips.ToArray();
    }
}
