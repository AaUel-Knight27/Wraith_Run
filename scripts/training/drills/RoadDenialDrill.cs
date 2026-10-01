using Godot;
using System.Collections.Generic;

/// <summary>DEMOLISHER road denial. An enemy car drives down a road at speed. You have ONE spike strip per round: aim at
/// the middle of the road ahead of it and hold to deploy (3 s), then clear the lane. A car that rolls over the strip loses
/// all four tires and stops (the same stopped state a shot tire causes). Trains timing, placement and leading a vehicle.</summary>
public partial class RoadDenialDrill : TrainingDrill
{
    private const int Rounds = 3;
    private const float CarStartZ = 90.0f;
    private const float CarEndZ = -130.0f;
    private const float PassedZ = -90.0f;

    private Transform3D _anchor;
    private SpikeStripSystem _spikes = null!;
    private TrainingCar? _car;
    private int _round = -1;
    private bool _active;
    private float _pause = 1.0f;
    private int _wins;
    private bool _roundResolved;
    private readonly List<bool> _results = new();

    public override string Objective => $"Round {Mathf.Max(1, _round + 1)}/{Rounds}: stop the enemy car. You have {_spikes.Strips} spike strip. Aim at the road centre ahead of it and hold [{TrainingInput.KeyText(TrainingInput.AbilityPrimary)}] for {TrainingKit.SpikeDeploySeconds:0} s. The car covers {12.0f:0} m every second - deploy early.";
    public override string StatusLine => $"Stopped {_wins}/{Rounds}";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.Road);
        Session.PlacePlayer(_anchor, new Vector3(6.5f, 0, -22.0f), 90.0f);

        // The road: a dark strip, 14 m wide.
        DrillHelpers.Place(Session, _anchor, TrainingUtil.Box(new Vector3(14.0f, 0.03f, 260.0f), new Color(0.12f, 0.12f, 0.13f), Vector3.Zero, "Road"), new Vector3(0, 0.02f, -20.0f));
        DrillHelpers.Place(Session, _anchor, TrainingUtil.Box(new Vector3(0.25f, 0.035f, 260.0f), new Color(0.9f, 0.85f, 0.3f), Vector3.Zero, "CentreLine"), new Vector3(0, 0.03f, -20.0f));

        _spikes = Session.AddMechanic<SpikeStripSystem>();
        _spikes.FixedYaw = _anchor.Basis.GetEuler().Y;
        _spikes.StoppedVehicle += OnVehicleStopped;
    }

    protected override void OnEnd()
    {
        if (IsInstanceValid(_car)) _car!.QueueFree();
    }

    protected override void OnTick(float dt)
    {
        if (!_active)
        {
            _pause -= dt;
            if (_pause > 0.0f) return;
            if (_round + 1 >= Rounds)
            {
                Finish(_wins >= 2, _wins >= 2 ? "The road is yours." : "Too many got through.");
                return;
            }
            StartRound(_round + 1);
            return;
        }

        if (_car == null || !IsInstanceValid(_car)) return;
        float carZ = (_anchor.AffineInverse() * _car.GlobalPosition).Z;
        if (!_roundResolved && carZ < PassedZ && !_car.IsImmobilized)
        {
            Session.Toast("It got past you - deploy earlier, and across its line", new Color(1.0f, 0.65f, 0.4f), 2.5f);
            Resolve(false);
        }
    }

    private void StartRound(int index)
    {
        _round = index;
        _active = true;
        _roundResolved = false;
        _spikes.SetStrips(1);

        // Clear the previous round's strip and car.
        foreach (Node child in Session.Props.GetChildren())
            if (child is SpikeStrip) child.QueueFree();
        if (_car != null && IsInstanceValid(_car)) _car.QueueFree();

        _car = Session.SpawnCar(_anchor * new Vector3(0, 0, CarStartZ), DrillHelpers.YawDegrees(_anchor), c =>
        {
            c.Hostile = true;
            c.AllowEnterExit = false;
            c.AutopilotEnabled = true;
            c.CruiseSpeed = 12.0f;
        });
        _car.Route.Add(_anchor * new Vector3(0, 0, CarEndZ));
        Session.Toast($"Round {index + 1}: car inbound", new Color(1.0f, 0.85f, 0.5f), 2.0f);
    }

    private void OnVehicleStopped(TrainingCar car)
    {
        if (_roundResolved || car != _car) return;
        Session.Toast("Car stopped!", new Color(0.6f, 1.0f, 0.6f), 2.0f);
        Resolve(true);
    }

    private void Resolve(bool won)
    {
        _roundResolved = true;
        _active = false;
        _pause = 3.0f;
        if (won) _wins++;
        _results.Add(won);
    }

    protected override float ComputeRating() => 100.0f * _wins / Rounds;

    protected override void BuildResults()
    {
        Row("Cars stopped", $"{_wins} / {Rounds}");
        for (int i = 0; i < _results.Count; i++) Row($"Round {i + 1}", _results[i] ? "stopped" : "got past");
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (_wins < Rounds) tips.Add("The strip is only 5 m wide and the car holds the road centre. Aim at the centre line, at least 35 m ahead of the car, and start the moment a round begins.");
        tips.Add("A deployed strip stops the SAME vehicle only once per 45 seconds - there is no second try on the same car, so make the first one count.");
        return tips.ToArray();
    }
}
