using Godot;
using System.Collections.Generic;

/// <summary>MEDIC basics: triage. Four teammates are hurt (20, 35, 45 and 70 HP) and you have 4 heal charges of 25 HP -
/// exactly enough to bring the three worst up to 50 HP (the worst needs two). Any charge spent on the 70 HP teammate, or
/// on yourself, means someone stays below 50. Heal is a 3 s channel: hold still, stay close, and don't get interrupted.</summary>
public partial class HealBasicsDrill : TrainingDrill
{
    private const float StableHp = 50.0f;
    private const float TimeLimit = 100.0f;
    private const float ParSeconds = 40.0f;

    private Transform3D _anchor;
    private HealSystem _heal = null!;
    private readonly List<TrainingActor> _friends = new();
    private int _wasted;
    private int _interrupts;
    private int _heals;

    public override string Objective => $"Bring EVERY teammate to {StableHp:0}+ HP with your {TrainingKit.BaseHealCharges} charges. Hold [{TrainingInput.KeyText(TrainingInput.AbilityPrimary)}] next to a teammate (stand still, 3 s). Heal the worst first - and don't waste one on someone who is fine.";
    public override string StatusLine => $"Charges {_heal.Charges}   wasted {_wasted}   interrupted {_interrupts}";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.CombatYard);
        Session.PlacePlayer(_anchor, new Vector3(0, 0, 4.0f));

        float[] hps = { 20.0f, 35.0f, 45.0f, 70.0f };
        float[] xs = { -7.5f, -2.5f, 2.5f, 7.5f };
        for (int i = 0; i < 4; i++)
        {
            TrainingActor friend = Session.SpawnActor(TrainingActor.FactionType.Friend, _anchor * new Vector3(xs[i], 0, -3.0f), $"Ally {i + 1}");
            friend.SetHp(hps[i]);
            _friends.Add(friend);
        }

        _heal = Session.AddMechanic<HealSystem>();
        _heal.Candidates = () => _friends.ConvertAll(f => (IHealable)f);
        _heal.Healed += OnHealed;
        _heal.HealInterrupted += reason => _interrupts++;
    }

    private void OnHealed(IHealable target, float amount)
    {
        _heals++;
        // The heal has already been applied; it was wasted if the target had been fine before it.
        if (target.CurrentHp - amount >= StableHp) _wasted++;
    }

    protected override void OnTick(float dt)
    {
        bool allStable = true;
        foreach (TrainingActor friend in _friends)
            if (friend.Hp < StableHp) allStable = false;

        if (allStable)
        {
            Finish(true, "Everyone is stable.");
            return;
        }
        if (_heal.Charges <= 0 && !_heal.Channel.Active)
        {
            Finish(false, "Out of charges with someone still below 50 HP.");
            return;
        }
        if (Elapsed >= TimeLimit) Finish(false, "Out of time.");
    }

    protected override float ComputeRating()
    {
        int stable = 0;
        foreach (TrainingActor f in _friends) if (f.Hp >= StableHp) stable++;
        float saved = stable / 4.0f * 100.0f;
        float time = TimeScore(Elapsed, ParSeconds);
        float efficient = Mathf.Clamp(100.0f - _wasted * 35.0f - _interrupts * 15.0f, 0.0f, 100.0f);
        return saved * 0.5f + time * 0.2f + efficient * 0.3f;
    }

    protected override void BuildResults()
    {
        int stable = 0;
        foreach (TrainingActor f in _friends) if (f.Hp >= StableHp) stable++;
        Row("Teammates at 50+ HP", $"{stable} / 4");
        Row("Heals delivered", _heals.ToString());
        Row("Charges wasted", _wasted.ToString());
        Row("Heals interrupted", _interrupts.ToString());
        Row("Time", TrainingUtil.Clock(Elapsed));
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (_wasted > 0) tips.Add("You healed someone who was already fine. Check the HP over each teammate's head first - the pool is small on purpose.");
        if (_interrupts > 0) tips.Add("Heal is cancelled if you move or take damage mid-channel. Plant your feet, then start.");
        tips.Add("Triage rule: worst first, and stop at 'stable' - a teammate at 50 HP is alive and fighting; saving your last charge for someone at 20 HP saves a life.");
        return tips.ToArray();
    }
}
