using Godot;
using System.Collections.Generic;

/// <summary>FIXER basics. Round 1: a parked, damaged car - two flat tires and heavy body damage. You have 3 repair
/// charges (a tire per charge, then +50 body HP), so none can be wasted. Round 2: the car is ROLLING and cannot be
/// repaired while it moves - call SLOW DOWN to your driver first, then fix it.</summary>
public partial class RepairBasicsDrill : TrainingDrill
{
    private const float RoundLimit = 70.0f;

    private Transform3D _anchor;
    private TrainingCar _car = null!;
    private RepairSystem _repair = null!;
    private int _round;
    private float _roundStart;
    private float _pause;
    private bool _between;
    private readonly float[] _roundTimes = new float[2];
    private int _interrupts;
    private int _spentTotal;

    public override CalloutType? ContextCallout => _round == 1 ? CalloutType.SlowDown : null;

    public override string Objective => _round == 0
        ? $"Round 1/2: the car has 2 flat tires and body damage. Stand beside it and hold [{TrainingInput.KeyText(TrainingInput.AbilityPrimary)}] to repair. Each charge fixes one tire, then +50 body HP."
        : $"Round 2/2: the car is MOVING - repairs need it under {TrainingKit.RepairSpeedLimitMps:0} m/s. Call SLOW DOWN [{TrainingInput.KeyText(TrainingInput.CalloutQuick)}] so the driver stops, then repair.";

    public override string StatusLine => $"Charges {_repair.Charges}   car HP {Mathf.CeilToInt(_car.Hp)}   flat tires {_car.FlatTireCount}";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.CombatYard);
        Session.PlacePlayer(_anchor, new Vector3(6.0f, 0, 4.0f));
        _repair = Session.AddMechanic<RepairSystem>();
        _repair.RepairInterrupted += reason => _interrupts++;
        BuildRound(0);
    }

    private void BuildRound(int round)
    {
        _round = round;
        _roundStart = Elapsed;
        _between = false;
        if (IsInstanceValid(_car)) _car.QueueFree();

        float yaw = DrillHelpers.YawDegrees(_anchor);
        _car = Session.SpawnCar(_anchor * new Vector3(0, 0, -14.0f), yaw, c =>
        {
            c.AllowEnterExit = false;
            c.CruiseSpeed = 6.0f;
            if (round == 1)
            {
                c.AiDriverPresent = true;
                c.AutopilotEnabled = true;
                c.LoopRoute = true;
            }
        });

        if (round == 0)
        {
            _car.PresetDamage(45.0f, 0, 1);
        }
        else
        {
            _car.PresetDamage(40.0f);
            foreach (Vector3 point in new[] { new Vector3(-16, 0, -14), new Vector3(0, 0, -30), new Vector3(16, 0, -14), new Vector3(0, 0, 2) })
                _car.Route.Add(_anchor * point);
        }

        _repair.Target = _car;
        _repair.SetCharges(Session.Kit.RepairCharges);
        Session.Toast(round == 0 ? "Round 1: parked car" : "Round 2: moving car", new Color(1.0f, 0.85f, 0.5f), 2.0f);
    }

    protected override void OnTick(float dt)
    {
        if (_between)
        {
            _pause -= dt;
            if (_pause <= 0.0f) BuildRound(1);
            return;
        }

        if (!_car.NeedsRepair)
        {
            _roundTimes[_round] = Elapsed - _roundStart;
            if (_round == 0)
            {
                _between = true;
                _pause = 2.5f;
                _spentTotal += Session.Kit.RepairCharges - _repair.Charges;
                Session.Toast("Round 1 done", new Color(0.6f, 1.0f, 0.6f));
            }
            else
            {
                _spentTotal += Session.Kit.RepairCharges - _repair.Charges;
                Finish(true, "Both cars back on the road.");
            }
            return;
        }

        if (_repair.Charges <= 0 && !_repair.Channel.Active)
        {
            Finish(false, "Out of repair charges with the car still damaged.");
            return;
        }
        if (Elapsed - _roundStart >= RoundLimit) Finish(false, "Out of time.");
    }

    protected override float ComputeRating()
    {
        float t0 = _roundTimes[0] > 0.0f ? TimeScore(_roundTimes[0], 40.0f) : 0.0f;
        float t1 = _roundTimes[1] > 0.0f ? TimeScore(_roundTimes[1], 45.0f) : 0.0f;
        float clean = Mathf.Clamp(100.0f - _interrupts * 20.0f, 0.0f, 100.0f);
        float done = ((_roundTimes[0] > 0.0f ? 1 : 0) + (_roundTimes[1] > 0.0f ? 1 : 0)) / 2.0f * 100.0f;
        return done * 0.4f + (t0 + t1) * 0.5f * 0.35f + clean * 0.25f;
    }

    protected override void BuildResults()
    {
        Row("Round 1 (parked)", _roundTimes[0] > 0.0f ? TrainingUtil.Clock(_roundTimes[0]) : "not finished");
        Row("Round 2 (moving)", _roundTimes[1] > 0.0f ? TrainingUtil.Clock(_roundTimes[1]) : "not finished");
        Row("Repair channels interrupted", _interrupts.ToString());
        Row("Charges spent", _spentTotal.ToString());
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (_interrupts > 0) tips.Add("A repair is cancelled if you walk away from the hull or the car starts moving. Stay within arm's reach and keep the button held.");
        tips.Add("Repairs need the vehicle nearly stopped. If your driver is still rolling, tell them: SLOW DOWN.");
        return tips.ToArray();
    }
}
