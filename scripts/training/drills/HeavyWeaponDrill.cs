using Godot;
using System.Collections.Generic;

/// <summary>Two ways to stop a vehicle. Round 1: small arms - shoot a TIRE and it is immobilised (a repair window, the
/// car is recoverable). Round 2: the Bazooka, the heavy weapon - one hit destroys the vehicle outright.
/// Trains target selection: tires to stop it, heavy weapons only when you mean to kill it.</summary>
public partial class HeavyWeaponDrill : TrainingDrill
{
    private const float RoundLimit = 45.0f;

    private Transform3D _anchor;
    private TrainingCar? _car;
    private int _round = -1;
    private float _roundStart;
    private bool _active;
    private float _pause = 1.0f;
    private int _shotsAtStart;
    private readonly float[] _times = new float[2];
    private readonly int[] _shots = new int[2];
    private readonly bool[] _won = new bool[2];

    public override string Objective => _round == 1
        ? "Round 2/2: BAZOOKA. Destroy the moving car outright with a single hit (lead it - the rocket needs to arrive where it will be)."
        : "Round 1/2: M4. Stop the moving car by shooting its TIRES (low on the body). One flat tire immobilises it.";

    public override string StatusLine => _active && _car != null && IsInstanceValid(_car) ? $"Car: {(_car.IsDestroyed ? "DESTROYED" : _car.IsImmobilized ? "STOPPED" : "moving")}   shots {Session.Stats.Shots - _shotsAtStart}" : "";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.Road);
        Session.PlacePlayer(_anchor, new Vector3(0, 0, 0), 0.0f);
        DrillHelpers.Place(Session, _anchor, TrainingUtil.Box(new Vector3(90.0f, 0.03f, 12.0f), new Color(0.12f, 0.12f, 0.13f), Vector3.Zero, "CrossRoad"), new Vector3(0, 0.02f, -45.0f));
    }

    protected override void OnEnd()
    {
        if (_car != null && IsInstanceValid(_car)) _car.QueueFree();
    }

    protected override void OnTick(float dt)
    {
        if (!_active)
        {
            _pause -= dt;
            if (_pause > 0.0f) return;
            if (_round + 1 >= 2)
            {
                int wins = (_won[0] ? 1 : 0) + (_won[1] ? 1 : 0);
                Finish(wins == 2, wins == 2 ? "You know which tool stops a car and which one kills it." : "One of the two got away.");
                return;
            }
            StartRound(_round + 1);
            return;
        }

        if (_car == null || !IsInstanceValid(_car)) return;
        bool done = _round == 0 ? _car.IsImmobilized : _car.IsDestroyed;
        if (done) EndRound(true);
        else if (Elapsed - _roundStart >= RoundLimit) EndRound(false);
    }

    private void StartRound(int index)
    {
        _round = index;
        _active = true;
        _roundStart = Elapsed;
        _shotsAtStart = Session.Stats.Shots;
        Session.Weapons.EquipByName(index == 0 ? "M4" : "Bazooka");
        Session.Weapons.RefillAmmo();
        if (_car != null && IsInstanceValid(_car)) _car.QueueFree();

        _car = Session.SpawnCar(_anchor * new Vector3(-35.0f, 0, -45.0f), DrillHelpers.YawDegrees(_anchor) - 90.0f, c =>
        {
            c.Hostile = true;
            c.AllowEnterExit = false;
            c.AutopilotEnabled = true;
            c.LoopRoute = true;
            c.CruiseSpeed = 8.0f;
        });
        _car.Route.Add(_anchor * new Vector3(35.0f, 0, -45.0f));
        _car.Route.Add(_anchor * new Vector3(-35.0f, 0, -45.0f));
        Session.Toast(index == 0 ? "Round 1: stop it" : "Round 2: destroy it", new Color(1.0f, 0.85f, 0.5f), 2.0f);
    }

    private void EndRound(bool won)
    {
        _active = false;
        _pause = 3.0f;
        _won[_round] = won;
        _times[_round] = Elapsed - _roundStart;
        _shots[_round] = Session.Stats.Shots - _shotsAtStart;
        Session.Toast(won ? (_round == 0 ? "Stopped - recoverable" : "Destroyed") : "Time - it got away", won ? new Color(0.6f, 1.0f, 0.6f) : new Color(1.0f, 0.65f, 0.4f), 2.2f);
    }

    protected override float ComputeRating()
    {
        // 50 points per round: half for winning, a quarter for speed, a quarter for not wasting shots.
        float total = 0.0f;
        for (int i = 0; i < 2; i++)
        {
            if (!_won[i]) continue;
            float speed = TimeScore(_times[i], 15.0f) / 100.0f;
            float spare = Mathf.Max(0, _shots[i] - (i == 0 ? 6 : 2));
            float efficiency = Mathf.Clamp(1.0f - spare * 0.08f, 0.0f, 1.0f);
            total += 50.0f * (0.5f + 0.25f * speed + 0.25f * efficiency);
        }
        return Mathf.Clamp(total, 0.0f, 100.0f);
    }

    protected override void BuildResults()
    {
        Row("Round 1: stop with M4", _won[0] ? $"stopped in {_times[0]:0.0}s, {_shots[0]} shots" : "failed");
        Row("Round 2: destroy with Bazooka", _won[1] ? $"destroyed in {_times[1]:0.0}s, {_shots[1]} shots" : "failed");
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (!_won[0]) tips.Add("Body shots only dent the car. Aim at the wheels - low on the side of the body - to immobilise it.");
        if (!_won[1]) tips.Add("The rocket is slow to arrive. Aim ahead of the car along its direction of travel, not at where it is now.");
        tips.Add("Rule of thumb: stop a vehicle with tires when you want it recoverable (or repairable by its crew); use the heavy weapon when it must not move again.");
        return tips.ToArray();
    }
}
