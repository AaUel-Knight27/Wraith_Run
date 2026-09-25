/// <summary>
/// Every EffectId used by the operator roster, grouped by class, with what the number means and
/// whether anything in the project actually reads it today.
///
/// Semantics used below:
///  Mult    - multiplies a base stat. Direction (lower/higher = better) is noted per field,
///            since it is not consistent (a time is better lower, a capacity is better higher).
///  Delta   - a signed value added to a base count.
///  Abs     - an absolute value that overrides/sets a stat directly (not relative to a base).
///  Flag    - 1.0 = true, 0.0 = false. Absence of the modifier means false/base-behavior.
///
/// "Wired" means an existing script already reads a value that this could multiply or override -
/// CharacterLoadout.GetModifier(...) is called from that script today. Everything marked
/// "not wired" is a real, named field on real characters, but the system it belongs to (vehicles,
/// the garage, an ability/charge economy, a stamina meter) does not exist yet in this project -
/// it is docs-only. Nothing here is invented busywork: it is exactly what the 25-operator roster
/// needs, recorded so the data model does not have to change again once those systems land.
/// </summary>
public static class CharacterEffectIds
{
	// ---------------------------------------------------------------------------------------
	// Driver
	// ---------------------------------------------------------------------------------------
	public const string SeatSwapTimeMult = "SeatSwapTimeMult";               // Mult, lower=faster. Not wired - no seat-swap timer exists.
	public const string OnFootMaxHealthMult = "OnFootMaxHealthMult";         // Mult, applies to Health.MaxHealth. WIRED.
	public const string FuelUseMult = "FuelUseMult";                        // Mult, lower=better. Not wired - no fuel system.
	public const string EngineBoostOutputMult = "EngineBoostOutputMult";     // Mult, lower=worse. Not wired - no vehicle system.
	public const string CorneringGripMult = "CorneringGripMult";            // Mult, higher=better. Not wired.
	public const string BrakingForceMult = "BrakingForceMult";              // Mult, lower=worse. Not wired.
	public const string HazardHighlightRangeMeters = "HazardHighlightRangeMeters"; // Abs, meters, higher=better. Not wired.
	public const string TopSpeedMult = "TopSpeedMult";                      // Mult, lower=worse. Not wired.
	public const string OffRoadSpeedLossMult = "OffRoadSpeedLossMult";      // Mult, lower=better (less loss). Not wired.
	public const string PavedAccelerationMult = "PavedAccelerationMult";    // Mult, lower=worse. Not wired.

	// ---------------------------------------------------------------------------------------
	// Fixer
	// ---------------------------------------------------------------------------------------
	public const string VehicleRepairTimeMult = "VehicleRepairTimeMult";    // Mult, lower=faster. Not wired - no repair system.
	public const string MovingRepairEnabled = "MovingRepairEnabled";        // Flag: can repair a vehicle moving under 8 m/s. Not wired.
	public const string RepairChargeCountDelta = "RepairChargeCountDelta";  // Delta on the Fixer's repair-charge pool. Not wired.
	public const string PlatingDamageReductionMult = "PlatingDamageReductionMult"; // Mult on damage taken by a plated vehicle. Not wired.
	public const string PlatingDurationSeconds = "PlatingDurationSeconds";  // Abs, seconds. Not wired.
	public const string PlatingChargeCost = "PlatingChargeCost";           // Abs, charges consumed per use. Not wired.
	public const string SpareTireCount = "SpareTireCount";                 // Abs, count carried. Not wired.
	public const string SpareTireUsesInsteadOfCharges = "SpareTireUsesInsteadOfCharges"; // Flag. Not wired.

	// ---------------------------------------------------------------------------------------
	// Operator
	// ---------------------------------------------------------------------------------------
	public const string InteractionSpeedMult = "InteractionSpeedMult";      // Mult on pickup/garage-claim/seat-entry time, lower=faster. Not wired.
	public const string CheckpointContestMult = "CheckpointContestMult";    // Mult, higher=better. Not wired.
	public const string SprintStaminaMult = "SprintStaminaMult";           // Mult, lower=worse. Not wired - no stamina meter exists anywhere in the project.
	public const string ReserveAmmoMult = "ReserveAmmoMult";               // Mult, higher=better. Not wired - only magazine size exists, no reserve pool.
	public const string MoveSpeedMult = "MoveSpeedMult";                   // Mult on base walk/sprint speed. WIRED.
	public const string VehicleSwayReductionMult = "VehicleSwayReductionMult"; // Mult on shooter-seat aim sway, lower=better. Not wired.
	public const string WeaponSwapSpeedMult = "WeaponSwapSpeedMult";       // Mult on switch time, higher=slower. Not wired - switching is instant today.
	public const string FootstepAudibleRadiusMult = "FootstepAudibleRadiusMult"; // Mult, lower=quieter. Not wired - no audio-detection system.
	public const string CrouchWalkSpeedMult = "CrouchWalkSpeedMult";       // Mult on crouched move speed. WIRED.
	public const string WeaponRecoilMult = "WeaponRecoilMult";             // Mult on vertical+horizontal recoil, lower=better. WIRED.
	public const string AimTimeMult = "AimTimeMult";                       // Mult on ADS time, SMG/pistol only, higher=slower. Not wired.

	// ---------------------------------------------------------------------------------------
	// Medic
	// ---------------------------------------------------------------------------------------
	public const string HealChannelTimeMult = "HealChannelTimeMult";       // Mult, lower=faster. Not wired - no heal-channel action exists.
	public const string HealChargeCountDelta = "HealChargeCountDelta";     // Delta on the Medic's heal-charge pool. Not wired.
	public const string HealPoolTotalHp = "HealPoolTotalHp";               // Abs, total HP healable across all charges. Not wired.
	public const string HealWhileMovingEnabled = "HealWhileMovingEnabled"; // Flag. Not wired.
	public const string HealWhileMovingSpeedMult = "HealWhileMovingSpeedMult"; // Mult on move speed while channeling. Not wired.
	public const string HealPerChargeMult = "HealPerChargeMult";           // Mult on HP restored per charge, lower=worse. Not wired.
	public const string SmokeChargeCount = "SmokeChargeCount";             // Abs, count. Not wired - no smoke mechanic exists.
	public const string SmokeDurationSeconds = "SmokeDurationSeconds";     // Abs, seconds. Not wired.
	public const string SmokeRadiusMeters = "SmokeRadiusMeters";           // Abs, meters. Not wired.
	public const string SmokeVisibleToEveryone = "SmokeVisibleToEveryone"; // Flag (drawback: not ally-only). Not wired.
	public const string HealInterruptible = "HealInterruptible";           // Flag, 0 = heal channel cannot be interrupted by damage. Not wired.
	public const string HealSelfUseAllowed = "HealSelfUseAllowed";         // Flag, 0 = cannot target self. Not wired.

	// ---------------------------------------------------------------------------------------
	// Demolisher
	// ---------------------------------------------------------------------------------------
	public const string ArmingTimeMult = "ArmingTimeMult";                 // Mult, lower=faster. Not wired - no charge/arming mechanic exists.
	public const string BlastRadiusMult = "BlastRadiusMult";               // Mult, lower=smaller/worse. Not wired.
	public const string ExplosiveChargeCountDelta = "ExplosiveChargeCountDelta"; // Delta on the Demolisher's explosive-charge pool. Not wired.
	public const string ManualDetonationEnabled = "ManualDetonationEnabled"; // Flag. Not wired.
	public const string ManualDetonationRangeMeters = "ManualDetonationRangeMeters"; // Abs, meters, line of sight. Not wired.
	public const string SpikeStripChargeCount = "SpikeStripChargeCount";   // Abs, count. Not wired.
	public const string SpikeStripDeploySeconds = "SpikeStripDeploySeconds"; // Abs, seconds. Not wired.
	public const string SpikeStripCooldownSeconds = "SpikeStripCooldownSeconds"; // Abs, seconds before the same vehicle can be stopped again. Not wired.
	public const string ExplosionDamageTakenMult = "ExplosionDamageTakenMult"; // Mult, lower=better (vehicle blasts + own charges). Not wired.
}
