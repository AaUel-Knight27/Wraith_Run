using Godot;
using System.Collections.Generic;

/// <summary>DEMOLISHER basics. Two breachable walls, two charges. Aim at a wall, hold to arm (5 s), then GET CLEAR - the fuse
/// is short and the blast (with a red radius on the ground) hurts you as much as anyone. Arming is cancelled if you look
/// away or step off. Trains planting, timing and standing off.</summary>
public partial class ChargeBasicsDrill : TrainingDrill
{
    private const float TimeLimit = 100.0f;
    private const float ParSeconds = 50.0f;

    private Transform3D _anchor;
    private ExplosiveSystem _explosives = null!;
    private readonly List<BreachWall> _walls = new();
    private int _breached;
    private int _selfHits;
    private int _interrupts;
    private int _selfDowns;

    public override string Objective => $"Breach both walls. Aim at a wall within 3.5 m and hold [{TrainingInput.KeyText(TrainingInput.AbilityPrimary)}] for {Session.Kit.ArmingSeconds:0} s to arm a charge. The fuse is {TrainingKit.BaseFuseSeconds:0} s and the blast radius is {Session.Kit.BlastRadius:0} m (the red circle) - get out of it.";
    public override string StatusLine => $"Breached {_breached}/2   charges {_explosives.Charges}   caught in blast {_selfHits}";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.BreachSite);
        Session.PlacePlayer(_anchor);

        foreach (float x in new[] { -11.0f, 11.0f })
        {
            var wall = new BreachWall { Size = new Vector3(5.0f, 3.2f, 0.6f) };
            wall.Breached += w => _breached++;
            DrillHelpers.Place(Session, _anchor, wall, new Vector3(x, 0, -14.0f));
            _walls.Add(wall);
        }

        _explosives = Session.AddMechanic<ExplosiveSystem>();
        _explosives.Detonated += (charge, traineeHit) => { if (traineeHit) _selfHits++; };
        _explosives.PlantInterrupted += reason => _interrupts++;
    }

    public override void OnTraineeDowned()
    {
        // The blast can down you; in training that is a lesson, not the end of the run.
        _selfDowns++;
        Session.Toast("Your own blast downed you - stand further back", new Color(1.0f, 0.5f, 0.4f), 2.5f);
        TrainingDamage.FullHealTrainee(Session.PlayerHealth);
    }

    protected override void OnTick(float dt)
    {
        if (_breached >= _walls.Count)
        {
            Finish(true, "Both walls are open.");
            return;
        }

        bool anyCharge = false;
        foreach (Node node in Session.GetTree().GetNodesInGroup("placed_charge"))
            if (node is PlacedCharge charge && !charge.IsSpent) anyCharge = true;
        if (_explosives.Charges <= 0 && !anyCharge && !_explosives.Channel.Active)
        {
            Finish(false, "Out of charges with a wall still standing.");
            return;
        }
        if (Elapsed >= TimeLimit) Finish(false, "Out of time.");
    }

    protected override float ComputeRating()
    {
        float breached = (float)_breached / _walls.Count * 100.0f;
        float time = TimeScore(Elapsed, ParSeconds);
        float safe = Mathf.Clamp(100.0f - _selfHits * 40.0f - _selfDowns * 30.0f, 0.0f, 100.0f);
        float clean = Mathf.Clamp(100.0f - _interrupts * 25.0f, 0.0f, 100.0f);
        return breached * 0.4f + time * 0.2f + safe * 0.3f + clean * 0.1f;
    }

    protected override void BuildResults()
    {
        Row("Walls breached", $"{_breached} / {_walls.Count}");
        Row("Caught in your own blast", _selfHits.ToString());
        Row("Arming interrupted", _interrupts.ToString());
        Row("Time", TrainingUtil.Clock(Elapsed));
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (_selfHits > 0) tips.Add("You were inside the red circle when it blew. Plant, then walk straight back - the fuse gives you time, but not much.");
        if (_interrupts > 0) tips.Add("Arming stops if you lose aim on the wall or step away. Plant from a stable stance, crosshair on the wall.");
        tips.Add("Blast damage falls off with distance: the further you are the less it hurts - and friends are subject to the same rule.");
        return tips.ToArray();
    }
}
