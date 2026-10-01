using Godot;
using System.Collections.Generic;

/// <summary>Weave a line of cones as fast as you can without touching them. A touched cone costs 2 seconds.
/// Trains steering, throttle control and picking a line.</summary>
public partial class SlalomDrill : TrainingDrill
{
    private const int ConeCount = 8;
    private const float ConeSpacing = 20.0f;
    private const float FirstConeZ = -30.0f;
    private const float HitRadius = 1.9f;
    private const float ParSeconds = 26.0f;
    private const float TimeLimit = 75.0f;

    private sealed class ConeState
    {
        public MeshInstance3D Node = null!;
        public Vector3 World;
        public bool Hit;
    }

    private Transform3D _anchor;
    private TrainingCar _car = null!;
    private readonly List<ConeState> _cones = new();
    private int _hits;
    private float _finishLocalZ;

    public override string Objective => "Drive the line of cones: W to accelerate, A/D to steer, S or C to brake, Space handbrake. Weave through without touching any cone (+2 s each).";
    public override string StatusLine => $"Cones hit {_hits}   penalty +{_hits * 2}s";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.DrivingCourse);
        _finishLocalZ = FirstConeZ - (ConeCount - 1) * ConeSpacing - 25.0f;

        _car = Session.SpawnCar(_anchor * new Vector3(0, 0, 12.0f), DrillHelpers.YawDegrees(_anchor), c =>
        {
            c.PreferredSeat = TrainingCar.Seat.Driver;
            c.AllowEnterExit = false;
        });
        _car.EnterSeat(Session, TrainingCar.Seat.Driver);

        for (int i = 0; i < ConeCount; i++)
        {
            Vector3 local = new Vector3(i % 2 == 0 ? -1.5f : 1.5f, 0.45f, FirstConeZ - i * ConeSpacing);
            MeshInstance3D cone = DrillHelpers.Place(Session, _anchor, DrillHelpers.Cone(new Color(1.0f, 0.5f, 0.1f)), local);
            _cones.Add(new ConeState { Node = cone, World = _anchor * new Vector3(local.X, 0, local.Z) });
        }

        Vector3 finish = new Vector3(0, 0.05f, _finishLocalZ);
        DrillHelpers.Place(Session, _anchor, TrainingUtil.GroundDisc(9.0f, new Color(0.3f, 1.0f, 0.4f), Vector3.Zero, 0.4f), finish);
        DrillHelpers.Place(Session, _anchor, TrainingUtil.Label("FINISH", Vector3.Zero, new Color(0.5f, 1.0f, 0.6f), 96, false), finish + new Vector3(0, 4.0f, 0));
    }

    protected override void OnEnd()
    {
        if (IsInstanceValid(_car)) _car.ReleasePlayer();
    }

    protected override void OnTick(float dt)
    {
        Vector3 carFlat = TrainingUtil.Flat(_car.GlobalPosition);
        foreach (ConeState cone in _cones)
        {
            if (cone.Hit) continue;
            if (carFlat.DistanceTo(TrainingUtil.Flat(cone.World)) > HitRadius) continue;
            cone.Hit = true;
            _hits++;
            cone.Node.MaterialOverride = TrainingUtil.Mat(new Color(0.35f, 0.35f, 0.35f));
            cone.Node.RotationDegrees = new Vector3(80.0f, 0, 0);
            Session.Toast("Cone hit +2 s", new Color(1.0f, 0.6f, 0.4f), 1.2f);
        }

        float carZ = (_anchor.AffineInverse() * _car.GlobalPosition).Z;
        if (carZ <= _finishLocalZ) Finish(true, _hits == 0 ? "Clean run." : $"Finished with {_hits} cone hit{(_hits == 1 ? "" : "s")}.");
        else if (Elapsed >= TimeLimit) Finish(false, "Out of time.");
    }

    private float EffectiveTime => Elapsed + _hits * 2.0f;

    protected override float ComputeRating()
    {
        float time = TimeScore(EffectiveTime, ParSeconds);
        float clean = Mathf.Clamp(100.0f - _hits * 20.0f, 0.0f, 100.0f);
        return time * 0.7f + clean * 0.3f;
    }

    protected override void BuildResults()
    {
        Row("Run time", TrainingUtil.Clock(Elapsed));
        Row("Cone penalty", $"+{_hits * 2} s");
        Row("Final time", TrainingUtil.Clock(EffectiveTime));
        Row("Par", TrainingUtil.Clock(ParSeconds));
        Row("Speed at finish", $"{Mathf.Abs(_car.SignedSpeed) * 3.6f:0} km/h");
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (_hits > 0) tips.Add("Look at the gap after the next cone, not at the cone you are passing. Start turning early and hold a smooth arc.");
        if (Elapsed > ParSeconds) tips.Add("Lift off the throttle before a turn, not during it. Steering bleeds speed - carry speed on the straights between cones.");
        if (tips.Count == 0) tips.Add("Smooth and quick. Try the braking box next.");
        return tips.ToArray();
    }
}
