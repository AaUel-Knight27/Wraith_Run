using Godot;
using System.Collections.Generic;

/// <summary>Build speed on a straight, then stop inside the marked box. Trains judging braking distance - the skill
/// behind every seat swap, repair stop and ambush. Reach at least 16 m/s before you brake.</summary>
public partial class BrakeBoxDrill : TrainingDrill
{
    private const float BrakeZoneZ = -70.0f;
    private const float BoxCenterZ = -130.0f;
    private const float BoxHalfLength = 7.0f;
    private const float BoxHalfWidth = 3.5f;
    private const float RequiredSpeed = 16.0f;
    private const float ParSeconds = 24.0f;

    private Transform3D _anchor;
    private TrainingCar _car = null!;
    private float _topSpeed;
    private bool _passedBrakeZone;
    private float _finalOffset;
    private float _stoppedAt;

    public override string Objective => "Accelerate down the straight (W), reach 16 m/s (58 km/h), then brake (S / C) and stop with the car inside the green box.";
    public override string StatusLine => $"Speed {_car.CurrentSpeed:0.0} m/s   top {_topSpeed:0.0} m/s   {(_topSpeed >= RequiredSpeed ? "speed OK" : "need 16 m/s")}";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.DrivingCourse);
        _car = Session.SpawnCar(_anchor * new Vector3(0, 0, 12.0f), DrillHelpers.YawDegrees(_anchor), c =>
        {
            c.PreferredSeat = TrainingCar.Seat.Driver;
            c.AllowEnterExit = false;
        });
        _car.EnterSeat(Session, TrainingCar.Seat.Driver);

        DrillHelpers.Place(Session, _anchor, TrainingUtil.Box(new Vector3(BoxHalfWidth * 2.0f, 0.04f, BoxHalfLength * 2.0f), new Color(0.3f, 1.0f, 0.4f, 1.0f), Vector3.Zero, "StopBox"), new Vector3(0, 0.03f, BoxCenterZ));
        DrillHelpers.Place(Session, _anchor, TrainingUtil.Label("STOP HERE", Vector3.Zero, new Color(0.5f, 1.0f, 0.6f), 80, false), new Vector3(0, 4.0f, BoxCenterZ));
        DrillHelpers.Place(Session, _anchor, TrainingUtil.Label("BRAKE ZONE", Vector3.Zero, new Color(1.0f, 0.8f, 0.4f), 72, false), new Vector3(0, 4.0f, BrakeZoneZ));
        DrillHelpers.Place(Session, _anchor, TrainingUtil.Box(new Vector3(BoxHalfWidth * 2.0f, 0.04f, 0.4f), new Color(1.0f, 0.8f, 0.3f), Vector3.Zero, "BrakeLine"), new Vector3(0, 0.03f, BrakeZoneZ));
    }

    protected override void OnEnd()
    {
        if (IsInstanceValid(_car)) _car.ReleasePlayer();
    }

    protected override void OnTick(float dt)
    {
        _topSpeed = Mathf.Max(_topSpeed, _car.CurrentSpeed);
        Vector3 local = _anchor.AffineInverse() * _car.GlobalPosition;
        if (local.Z < BrakeZoneZ) _passedBrakeZone = true;

        if (local.Z < BoxCenterZ - BoxHalfLength - 12.0f)
        {
            Finish(false, "Overshot the box.");
            return;
        }
        if (Elapsed > 60.0f)
        {
            Finish(false, "Out of time.");
            return;
        }

        if (_passedBrakeZone && _car.CurrentSpeed < 0.4f)
        {
            _finalOffset = local.Z - BoxCenterZ;
            _stoppedAt = local.Z;
            bool inside = Mathf.Abs(_finalOffset) <= BoxHalfLength && Mathf.Abs(local.X) <= BoxHalfWidth;
            Finish(inside, inside ? "Stopped in the box." : (_finalOffset > 0.0f ? $"Stopped {_finalOffset - BoxHalfLength:0.0} m short of the box." : "Stopped past the box."));
        }
    }

    protected override float ComputeRating()
    {
        float precision = Mathf.Clamp(100.0f - Mathf.Abs(_finalOffset) / BoxHalfLength * 60.0f, 0.0f, 100.0f);
        float speed = Mathf.Clamp(_topSpeed / RequiredSpeed, 0.0f, 1.0f) * 100.0f;
        float time = TimeScore(Elapsed, ParSeconds);
        return precision * 0.6f + speed * 0.25f + time * 0.15f;
    }

    protected override void BuildResults()
    {
        Row("Distance from box centre", $"{Mathf.Abs(_finalOffset):0.0} m");
        Row("Top speed", $"{_topSpeed:0.0} m/s ({_topSpeed * 3.6f:0} km/h)");
        Row("Run time", TrainingUtil.Clock(Elapsed));
        Row("Stopped at", $"{-_stoppedAt:0} m down the road");
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (_topSpeed < RequiredSpeed) tips.Add("You never reached 16 m/s, so the stop was too easy. Build real speed - then judge the braking point.");
        if (_finalOffset > BoxHalfLength) tips.Add("You stopped short. Brake later, and use the yellow line as a reference for your brake point at speed.");
        if (_finalOffset < -BoxHalfLength) tips.Add("You stopped long. At this speed the car needs roughly 15-20 m to stop - start braking earlier.");
        if (tips.Count == 0) tips.Add("Well judged. This is the same skill that makes a clean seat swap or a safe repair stop.");
        return tips.ToArray();
    }
}
