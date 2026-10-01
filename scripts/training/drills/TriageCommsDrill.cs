using Godot;
using System.Collections.Generic;

/// <summary>
/// MEDIC comms. Five teammates are bleeding out and they CALL for you, one after another, with a marker over their head.
/// Answer each call (say "On my way" - the callout menu or [G]) so they know help is coming, then reach and heal them,
/// worst first. Answered teammates bleed slower for a while. You have 5 charges, but not the time to waste any.
/// </summary>
public partial class TriageCommsDrill : TrainingDrill
{
    private sealed class Patient
    {
        public TrainingActor Actor = null!;
        public float FirstCallAt = -1.0f;
        public float LastCallAt = -1.0f;
        public float CallStart;
        public float AnsweredAt = -1.0f;
        public float CalmedUntil = -1.0f;
        public bool Stabilised;
    }

    private const float ReCallInterval = 7.0f;
    private const float ResponseGoodSeconds = 6.0f;
    private const float BleedRate = 1.0f;

    private static readonly float[] StartHp = { 40.0f, 55.0f, 35.0f, 60.0f, 45.0f };
    private static readonly float[] CallTimes = { 2.0f, 7.0f, 12.0f, 17.0f, 22.0f };

    private Transform3D _anchor;
    private HealSystem _heal = null!;
    private readonly List<Patient> _patients = new();

    public override CalloutType? ContextCallout => CalloutType.OnMyWay;

    public override string Objective => $"Teammates will call MEDIC! over the next 25 seconds. Answer each call ([{TrainingInput.KeyText(TrainingInput.CalloutQuick)}] = 'On my way'), go to them and heal - lowest HP first. A heal stops the bleeding for good.";

    public override string StatusLine
    {
        get
        {
            int saved = 0, dead = 0;
            foreach (Patient p in _patients)
            {
                if (p.Stabilised) saved++;
                if (p.Actor.IsDead) dead++;
            }
            return $"Saved {saved}/{_patients.Count}   lost {dead}   charges {_heal.Charges}";
        }
    }

    protected override void OnBegin()
    {
        _anchor = Session.Anchor(TrainingAnchors.CombatYard);
        Session.PlacePlayer(_anchor, new Vector3(0, 0, 0));

        for (int i = 0; i < StartHp.Length; i++)
        {
            float angle = Mathf.DegToRad(-100.0f + i * 50.0f);
            Vector3 local = new Vector3(Mathf.Sin(angle) * 14.0f, 0, -Mathf.Cos(angle) * 14.0f);
            TrainingActor actor = Session.SpawnActor(TrainingActor.FactionType.Friend, _anchor * local, $"Ally {i + 1}");
            actor.SetHp(StartHp[i]);
            _patients.Add(new Patient { Actor = actor, CallStart = CallTimes[i] });
        }

        _heal = Session.AddMechanic<HealSystem>();
        _heal.SetCharges(5);
        _heal.Candidates = () => _patients.ConvertAll(p => (IHealable)p.Actor);
        _heal.Healed += OnHealed;
        Session.Callouts.Issued += OnCallout;
    }

    protected override void OnEnd()
    {
        if (TrainingSession.Instance != null && IsInstanceValid(Session.Callouts)) Session.Callouts.Issued -= OnCallout;
    }

    protected override void OnTick(float dt)
    {
        bool allResolved = true;
        foreach (Patient p in _patients)
        {
            if (p.Actor.IsDead || p.Stabilised) continue;
            allResolved = false;

            if (Elapsed >= p.CallStart)
            {
                p.Actor.BleedPerSecond = Elapsed < p.CalmedUntil ? BleedRate * 0.4f : BleedRate;
                if (p.LastCallAt < 0.0f || Elapsed - p.LastCallAt >= ReCallInterval)
                {
                    if (p.AnsweredAt < 0.0f || Elapsed - p.LastCallAt >= ReCallInterval * 1.5f)
                    {
                        if (p.FirstCallAt < 0.0f) p.FirstCallAt = Elapsed;
                        p.LastCallAt = Elapsed;
                        Session.Callouts.Send(CalloutType.NeedMedic, p.Actor.ActorName, false, p.Actor.GlobalPosition);
                    }
                }
            }
        }

        // Patients that have not called yet are not bleeding yet (they are "fine" until their call time).
        foreach (Patient p in _patients)
            if (!p.Actor.IsDead && !p.Stabilised && Elapsed < p.CallStart) allResolved = false;

        if (allResolved) End();
        else if (Elapsed > 120.0f) End();
    }

    private void End()
    {
        int saved = 0;
        foreach (Patient p in _patients) if (p.Stabilised) saved++;
        Finish(saved >= 4, saved >= 4 ? "Team saved." : "You lost too many teammates.");
    }

    private void OnCallout(CalloutMessage message)
    {
        if (!message.FromPlayer) return;
        if (message.Type != CalloutType.OnMyWay && message.Type != CalloutType.Healing) return;

        // The reply answers the nearest teammate who is calling and has not been answered yet.
        Patient? best = null;
        float bestDistance = float.MaxValue;
        foreach (Patient p in _patients)
        {
            if (p.Actor.IsDead || p.Stabilised || p.FirstCallAt < 0.0f || p.AnsweredAt >= 0.0f) continue;
            // Nearest to where the reply was aimed; with no position, the one who has waited longest.
            float d = message.HasPosition ? p.Actor.GlobalPosition.DistanceTo(message.Position) : p.FirstCallAt;
            if (d < bestDistance) { best = p; bestDistance = d; }
        }
        if (best == null) return;
        best.AnsweredAt = Elapsed;
        best.CalmedUntil = Elapsed + 9.0f;
        Session.Toast($"{best.Actor.ActorName}: \"Thank God - hurry!\"", new Color(0.7f, 1.0f, 0.75f), 1.8f);
    }

    private void OnHealed(IHealable target, float amount)
    {
        for (int i = 0; i < _patients.Count; i++)
        {
            if (!ReferenceEquals(_patients[i].Actor, target)) continue;
            _patients[i].Stabilised = true;
            _patients[i].Actor.BleedPerSecond = 0.0f;
            if (_patients[i].AnsweredAt < 0.0f && _patients[i].FirstCallAt >= 0.0f) _patients[i].AnsweredAt = Elapsed;
        }
    }

    protected override float ComputeRating()
    {
        int saved = 0, called = 0, quick = 0;
        foreach (Patient p in _patients)
        {
            if (p.Stabilised) saved++;
            if (p.FirstCallAt < 0.0f) continue;
            called++;
            if (p.AnsweredAt >= 0.0f && p.AnsweredAt - p.FirstCallAt <= ResponseGoodSeconds) quick++;
        }
        float savedScore = (float)saved / _patients.Count * 100.0f;
        float comms = called > 0 ? (float)quick / called * 100.0f : 0.0f;
        float time = TimeScore(Elapsed, 70.0f);
        return savedScore * 0.6f + comms * 0.25f + time * 0.15f;
    }

    protected override void BuildResults()
    {
        int saved = 0;
        foreach (Patient p in _patients) if (p.Stabilised) saved++;
        Row("Teammates saved", $"{saved} / {_patients.Count}");
        foreach (Patient p in _patients)
        {
            string response = p.FirstCallAt < 0.0f ? "never called"
                : p.AnsweredAt < 0.0f ? "never answered"
                : $"answered in {p.AnsweredAt - p.FirstCallAt:0.0}s";
            string fate = p.Stabilised ? "saved" : p.Actor.IsDead ? "lost" : "hurt";
            Row(p.Actor.ActorName, $"{fate}, {response}");
        }
        Row("Heal charges left", _heal.Charges.ToString());
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        int unanswered = 0;
        foreach (Patient p in _patients) if (p.FirstCallAt >= 0.0f && p.AnsweredAt < 0.0f) unanswered++;
        if (unanswered > 0) tips.Add("Some calls went unanswered. A quick 'On my way' calms a teammate (they bleed slower) and tells the rest you are handling it.");
        tips.Add("Go to the lowest HP first, but only if you can reach them in time - a teammate at 55 HP can wait ten seconds; one at 20 cannot.");
        return tips.ToArray();
    }
}
