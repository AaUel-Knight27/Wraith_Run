using Godot;
using System.Collections.Generic;

/// <summary>Heal a wounded teammate in the open while two enemies shoot at you both. The heal channel is cancelled if you
/// are hit, so you need to break their line of sight first: throw SMOKE between them and the patient (F), then heal.
/// The baseline medic has no smoke, so this drill lends you two - the training version of Ashen Corvus's kit.</summary>
public partial class HealUnderFireDrill : TrainingDrill
{
    private const float TargetHp = 55.0f;
    private const float TimeLimit = 70.0f;
    private const float ParSeconds = 35.0f;

    private Transform3D _anchor;
    private HealSystem _heal = null!;
    private SmokeSystem _smoke = null!;
    private TrainingActor _patient = null!;
    private int _interrupts;
    private int _smokesThrown;
    private bool _patientDied;

    public override CalloutType? ContextCallout => CalloutType.CoverMe;

    public override string Objective
    {
        get
        {
            float hp = _patient == null ? 0.0f : _patient.Hp;
            return $"Your teammate is down in the open ({hp:0} HP), under fire. Get him to {TargetHp:0}+ HP. A hit cancels your heal - throw smoke [{TrainingInput.KeyText(TrainingInput.AbilitySecondary)}] to cut the enemies' line of sight, then hold [{TrainingInput.KeyText(TrainingInput.AbilityPrimary)}] to heal.";
        }
    }

    public override string StatusLine => $"Patient {Mathf.CeilToInt(_patient.Hp)} HP   smoke {_smoke.Charges}   heal charges {_heal.Charges}   interrupted {_interrupts}";

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.CombatYard);
        Session.PlacePlayer(_anchor, new Vector3(0, 0, 4.0f));

        _patient = Session.SpawnActor(TrainingActor.FactionType.Friend, _anchor * new Vector3(0, 0, -16.0f), "Patient");
        _patient.SetHp(30.0f);
        _patient.Died += (a, s) => _patientDied = true;

        Vector3 axis = _anchor.Basis * Vector3.Right;
        foreach (float side in new[] { -1.0f, 1.0f })
        {
            Vector3 world = _anchor * new Vector3(24.0f * side, 0, -44.0f);
            Session.SpawnActor(TrainingActor.FactionType.Enemy, world, "Shooter", a =>
            {
                a.Mode = TrainingActor.BehaviorType.Strafe;
                a.Anchor = world;
                a.Axis = axis;
                a.Amplitude = 2.0f;
                a.Period = 3.4f;
                a.PhaseOffset = side;
                a.CanShoot = true;
                a.Accuracy = 0.3f;
                a.ShotDamage = 5.0f;
                a.FireInterval = 1.0f;
                a.TellSeconds = 1.0f;
                a.ShootRange = 90.0f;
            });
        }

        _heal = Session.AddMechanic<HealSystem>();
        _heal.Candidates = () => new List<IHealable> { _patient };
        _heal.HealInterrupted += reason => _interrupts++;
        _smoke = Session.AddMechanic<SmokeSystem>();
        _smoke.SetCharges(TrainingKit.PracticeSmokeCharges);
        _smoke.Thrown += cloud => _smokesThrown++;
    }

    protected override void OnTick(float dt)
    {
        if (_patientDied)
        {
            Finish(false, "The patient died.");
            return;
        }
        if (_patient.Hp >= TargetHp)
        {
            Finish(true, "Patient stabilised.");
            return;
        }
        if (Elapsed >= TimeLimit) Finish(false, "Out of time.");
    }

    protected override float ComputeRating()
    {
        float done = _patient.Hp >= TargetHp ? 100.0f : Mathf.Clamp((_patient.Hp - 30.0f) / (TargetHp - 30.0f), 0.0f, 1.0f) * 100.0f;
        float time = TimeScore(Elapsed, ParSeconds);
        float safety = Mathf.Clamp(100.0f - Session.Stats.DamageTaken * 1.5f, 0.0f, 100.0f);
        float bonus = _smokesThrown > 0 ? 100.0f : 0.0f;
        return done * 0.45f + time * 0.15f + safety * 0.2f + bonus * 0.2f;
    }

    protected override void BuildResults()
    {
        Row("Patient HP", $"{Mathf.CeilToInt(_patient.Hp)} / {TargetHp:0} needed");
        Row("Smoke used", _smokesThrown.ToString());
        Row("Heals interrupted", _interrupts.ToString());
        Row("Damage taken", $"{Session.Stats.DamageTaken:0}");
        Row("Time", TrainingUtil.Clock(Elapsed));
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        if (_smokesThrown == 0) tips.Add("You never used smoke. Put it BETWEEN the enemies and the patient (aim at the gap, press the smoke key) - enemies cannot shoot what they cannot see.");
        if (_interrupts > 0) tips.Add("Every hit cancelled a 3-second channel. Blind them first, then heal - never the other way round.");
        return tips.ToArray();
    }
}
