using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// MEDIC - the heal channel. Hold [Q] next to a hurt friend (or yourself if the kit allows it).
///
/// Rules, all from the roster baseline (TrainingKit): 3.0 s channel, 4 charges of 25 HP, interrupted if you are
/// hit (Draug Marrow's perk removes that), no healing while moving (Renk Vael's perk allows it), self-heal allowed.
///
/// REAL WIRING: give it real players by returning IHealable adapters from Candidates (PlayerHealable in Ports.cs
/// already wraps a real Health node). Nothing else in here needs to change.
/// </summary>
public partial class HealSystem : TrainingMechanic
{
    public const float Range = 2.8f;

    public int Charges { get; private set; }
    public float PerCharge { get; private set; }
    public readonly HoldChannel Channel = new();
    /// <summary>Who can be healed right now. Set by the drill.</summary>
    public Func<IEnumerable<IHealable>>? Candidates { get; set; }

    public event Action<IHealable, float>? Healed;
    public event Action<string>? HealInterrupted;

    private IHealable _self = null!;
    private IHealable? _target;
    private float _lastPlayerHp;
    private bool _needRelease;

    public void SetCharges(int charges) => Charges = charges;

    public override void _Ready()
    {
        Charges = Session.Kit.HealCharges;
        PerCharge = Session.Kit.HealPerCharge;
        _self = new PlayerHealable(Session.PlayerHealth, Session.Player);
        _lastPlayerHp = Session.PlayerHealth.CurrentHealth;
        Channel.Completed += OnCompleted;
        Channel.Interrupted += OnInterrupted;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        bool hold = Input.IsActionPressed(TrainingInput.AbilityPrimary);
        float hp = Session.PlayerHealth.CurrentHealth;
        if (!hold) _needRelease = false;

        if (Channel.Active)
        {
            string? problem = Validate(hold, hp);
            if (problem != null) Channel.Interrupt(problem);
            else Channel.Tick(dt);
        }
        else if (hold && !_needRelease && Session.DrillLive)
        {
            TryBegin();
        }

        _lastPlayerHp = Session.PlayerHealth.CurrentHealth;

        if (Channel.Active)
        {
            ShowChannel(Channel.Progress, Channel.Label);
            ShowPrompt(null);
            return;
        }
        HideChannel();
        IHealable? near = NearestNeedy();
        string key = TrainingInput.KeyText(TrainingInput.AbilityPrimary);
        if (Charges <= 0) ShowPrompt("Out of heal charges");
        else if (near != null) ShowPrompt($"Hold [{key}] to heal {near.DisplayName}   ({Charges} charges)");
        else ShowPrompt($"[{key}] heals a hurt friend within {Range:0.#} m   ({Charges} charges)");
    }

    private IHealable? NearestNeedy()
    {
        IHealable? best = null;
        float bestDistance = Range;
        if (Candidates == null) return null;
        Vector3 me = Session.Player.GlobalPosition;
        foreach (IHealable candidate in Candidates())
        {
            if (!candidate.IsAlive || candidate.CurrentHp >= candidate.MaxHp - 0.01f) continue;
            float d = me.DistanceTo(candidate.WorldPosition);
            if (d > bestDistance) continue;
            best = candidate;
            bestDistance = d;
        }
        return best;
    }

    private void TryBegin()
    {
        if (Charges <= 0)
        {
            Session.Toast("Out of heal charges", new Color(1.0f, 0.5f, 0.4f));
            _needRelease = true;
            return;
        }

        IHealable? target = NearestNeedy();
        if (target == null && Session.Kit.SelfHealAllowed && _self.CurrentHp < _self.MaxHp - 0.01f) target = _self;
        if (target == null) return;

        _target = target;
        Channel.Begin(Session.Kit.HealChannelSeconds, $"HEALING {target.DisplayName.ToUpperInvariant()}");
        if (!ReferenceEquals(target, _self))
            Session.Callouts.Send(CalloutType.Healing, "You", true, target.WorldPosition);
    }

    private string? Validate(bool hold, float hp)
    {
        if (!hold) return "released too early";
        if (_target == null || !_target.IsAlive) return "target is down";
        if (!ReferenceEquals(_target, _self) && Session.Player.GlobalPosition.DistanceTo(_target.WorldPosition) > Range + 0.8f)
            return "target moved out of reach";
        if (!Session.Kit.HealWhileMoving && HorizontalSpeed(Session.Player) > 0.6f) return "you moved";
        if (Session.Kit.HealInterruptedByDamage && hp < _lastPlayerHp - 0.01f) return "you were hit";
        return null;
    }

    private void OnCompleted()
    {
        if (_target == null) return;
        _target.Heal(PerCharge);
        Charges--;
        _needRelease = true;
        Session.Toast($"Healed {_target.DisplayName} +{PerCharge:0} HP", new Color(0.5f, 1.0f, 0.6f));
        Healed?.Invoke(_target, PerCharge);
    }

    private void OnInterrupted(string reason)
    {
        _needRelease = true;
        Session.Toast($"Heal interrupted: {reason}", new Color(1.0f, 0.55f, 0.4f));
        HealInterrupted?.Invoke(reason);
    }
}
