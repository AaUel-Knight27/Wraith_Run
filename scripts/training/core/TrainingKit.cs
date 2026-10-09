using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The numbers a class's abilities run on during training.
///
/// WHERE THE NUMBERS COME FROM. Every "Base*" constant below is back-derived from the roster notes in
/// data/characters/*.tres, which state each operator's perk relative to a baseline (e.g. Ratchet Vane:
/// "Repair time -30% (6 -> 4.2 s)" => base repair is 6 s; Undertaker Kade: "Heal channel -25% (2.25 s)"
/// => base 3.0 s; Volk "+1 charge (125 HP total)" and Kade "1 fewer charge (75 HP total)" => base pool
/// is 100 HP in 25 HP charges). Where the roster does not say (blast radius, fuse length, HP restored
/// per repair charge, smoke for a medic who is not Ashen Corvus) the value is marked ASSUMED - tune it
/// here and every drill follows.
///
/// Modifiers is empty in training: training is per CLASS, on the baseline kit, and only ever on shared character.
/// ApplyCharacter() is the future hook for "practice as operator X" - it changes NUMBERS only, never the body.
/// </summary>
public sealed class TrainingKit
{
    public readonly Dictionary<string, float> Modifiers = new();

    public float Get(string effectId, float defaultValue) =>
        Modifiers.TryGetValue(effectId, out float value) ? value : defaultValue;

    public bool Flag(string effectId) => Get(effectId, 0.0f) > 0.5f;

    /// <summary>Future hook. Sums Buffs+Drawbacks exactly like CharacterData.GetModifier does.</summary>
    public void ApplyCharacter(CharacterData? data)
    {
        Modifiers.Clear();
        if (data == null) return;
        foreach (StatModifier mod in data.Buffs) Add(mod);
        foreach (StatModifier mod in data.Drawbacks) Add(mod);
    }

    private void Add(StatModifier mod)
    {
        Modifiers.TryGetValue(mod.EffectId, out float existing);
        Modifiers[mod.EffectId] = existing + mod.Value;
    }

    // ---- Medic -------------------------------------------------------------------------------
    public const float BaseHealChannelSeconds = 3.0f;   // roster: Kade 2.25 = -25%, Draug 3.6 = +20%
    public const int BaseHealCharges = 4;               // roster: pool 100 HP in 25 HP charges
    public const float BaseHealPerCharge = 25.0f;
    public const float SmokeSeconds = 10.0f;            // roster (Ashen Corvus): 2 smokes, 10 s, 4 m
    public const float SmokeRadius = 4.0f;
    public const int PracticeSmokeCharges = 2;          // ASSUMED: baseline medic has none; drills lend 2

    public float HealChannelSeconds => BaseHealChannelSeconds * Get(CharacterEffectIds.HealChannelTimeMult, 1.0f);
    public int HealCharges => Mathf.Max(0, Mathf.RoundToInt(BaseHealCharges + Get(CharacterEffectIds.HealChargeCountDelta, 0.0f)));
    public float HealPoolTotal => Get(CharacterEffectIds.HealPoolTotalHp,
        HealCharges * BaseHealPerCharge * Get(CharacterEffectIds.HealPerChargeMult, 1.0f));
    public float HealPerCharge => HealCharges > 0 ? HealPoolTotal / HealCharges : 0.0f;
    public bool HealInterruptedByDamage => Get(CharacterEffectIds.HealInterruptible, 1.0f) > 0.5f;
    public bool HealWhileMoving => Flag(CharacterEffectIds.HealWhileMovingEnabled);
    public bool SelfHealAllowed => Get(CharacterEffectIds.HealSelfUseAllowed, 1.0f) > 0.5f;

    // ---- Fixer -------------------------------------------------------------------------------
    public const float BaseRepairSeconds = 6.0f;        // roster: Ratchet 4.2 s = -30%; spare tires "still 6 s"
    public const int BaseRepairCharges = 3;             // roster: Magpie +1 = 4, Ratchet -1 = 2
    public const float RepairHpPerCharge = 50.0f;       // roster: garage "Repair car (+50 car HP)"
    public const float RepairSpeedLimitMps = 2.0f;      // baseline: vehicle must be (nearly) stopped
    public const float MovingRepairSpeedLimitMps = 8.0f;// roster: Patchwork "vehicle moving under 8 m/s"

    public float RepairSeconds => BaseRepairSeconds * Get(CharacterEffectIds.VehicleRepairTimeMult, 1.0f);
    public int RepairCharges => Mathf.Max(0, Mathf.RoundToInt(BaseRepairCharges + Get(CharacterEffectIds.RepairChargeCountDelta, 0.0f)));
    public float RepairMaxVehicleSpeed => Flag(CharacterEffectIds.MovingRepairEnabled) ? MovingRepairSpeedLimitMps : RepairSpeedLimitMps;

    // ---- Demolisher --------------------------------------------------------------------------
    public const float BaseArmingSeconds = 5.0f;        // roster: Cinder Varga 3 s = -40%
    public const int BaseExplosiveCharges = 2;          // roster: Kral +1 = 3, Kest/Dolan -1
    public const float BaseBlastRadius = 6.0f;          // ASSUMED (roster only gives -15% for Cinder)
    public const float BlastMaxDamage = 90.0f;          // ASSUMED, at the centre; falls off to 0 at the radius
    public const float BaseFuseSeconds = 4.0f;          // ASSUMED: baseline charges self-detonate after arming
    public const float SpikeDeploySeconds = 3.0f;       // roster: Tripwire Dolan "deploys in 3 s"
    public const float SpikeCooldownSeconds = 45.0f;    // roster: "stops the same vehicle only once per 45 s"
    public const float ManualDetonationRange = 40.0f;   // roster: Deadhand Kest, line of sight

    public float ArmingSeconds => BaseArmingSeconds * Get(CharacterEffectIds.ArmingTimeMult, 1.0f);
    public int ExplosiveCharges => Mathf.Max(0, Mathf.RoundToInt(BaseExplosiveCharges + Get(CharacterEffectIds.ExplosiveChargeCountDelta, 0.0f)));
    public float BlastRadius => BaseBlastRadius * Get(CharacterEffectIds.BlastRadiusMult, 1.0f);
    public bool ManualDetonation => Flag(CharacterEffectIds.ManualDetonationEnabled);
    public float ExplosionDamageTakenMult => Get(CharacterEffectIds.ExplosionDamageTakenMult, 1.0f);

    // ---- Driver ------------------------------------------------------------------------------
    public const float BaseSeatSwapSeconds = 2.0f;      // FR-CA-05 (roster: Shiv Brandt 1.5 s = -25%)
    public const float FullStopSpeed = 2.0f;            // FR-CA-04
    public const float BriefSlowdownSpeed = 8.0f;       // FR-CA-04

    public float SeatSwapSeconds => BaseSeatSwapSeconds * Get(CharacterEffectIds.SeatSwapTimeMult, 1.0f);
    public float FuelUseMult => Get(CharacterEffectIds.FuelUseMult, 1.0f);
    public float BoostOutputMult => Get(CharacterEffectIds.EngineBoostOutputMult, 1.0f);
    public float SwapSpeedLimit(SeatSwapRule rule) => rule == SeatSwapRule.FullStop ? FullStopSpeed : BriefSlowdownSpeed;
}
