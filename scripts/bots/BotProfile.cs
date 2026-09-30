using System;

/// <summary>Bot skill level picked in the menu. Mixed rolls one of the other three per bot.</summary>
public enum BotDifficulty
{
	Easy = 0,
	Normal = 1,
	Advanced = 2,
	Mixed = 3,
}

/// <summary>
/// Every number that makes an Easy bot feel different from a Normal or Advanced one, in one place.
///
/// The aim/reaction block was tuned with a duel simulator (tools/bot_duel_sim.py) that mirrors BotAim
/// and the fire gate, using the game's real AK-47 numbers. Re-run it after changing any value here. Sensing and behaviour numbers are the
/// human-feel knobs: if a level feels too strong or too weak in play, change it HERE and nowhere else.
/// </summary>
public sealed class BotProfile
{
	public string Label = "";
	public string Tag = "";

	// ---- Vision ----
	public float VisionHalfAngleDeg;      // anything outside this cone is simply not seen
	public float VisionRange;             // metres
	public float PeripheralGain;          // recognition speed outside the central 30 degrees (0..1)
	public float ReactMin, ReactMax;      // seconds from "in view" to "I noticed an enemy"
	public float MemorySeconds;           // how long a lost enemy is remembered and hunted
	public float PerceptionDelay;         // the aim follows where the enemy was this long ago
	public float Lead;                    // 0..1 how much of that delay is predicted away
	public float PositionNoise;           // metres of slow drift in where the bot thinks you are

	// ---- Hearing ----
	public float HearingScale;            // multiplies every sound radius
	public float HearingReliability;      // chance a sound inside the radius is actually noticed
	public float SoundError;              // metres of position error per 10 m of distance
	public float HitFreezeMin, HitFreezeMax; // startle time after being shot, no firing back
	public float CalloutChance;           // chance to share a sighting with teammates (TDM)
	public float KillInference;           // metres of error when guessing a killer's position (0 = doesn't)

	// ---- Aim ----
	public float TrackSpeedDeg;           // max turn rate while tracking (deg/s)
	public float FlickSpeedDeg;           // max turn rate during a flick
	public float FlickAccelDeg;           // angular acceleration (deg/s^2)
	public float Overconfidence;          // >1 brakes late so flicks overshoot
	public float TrackTau;                // seconds to close a tracking error
	public float HandNoiseDeg;            // wobble amplitude
	public float HandNoiseTau;            // wobble time constant
	public float FlickErrorDeg;           // where a flick lands relative to the target
	public float CorrectionTau;           // how fast the post-flick error is corrected
	public float RecoilCompensation;      // 0..1 share of weapon kick the bot pulls back
	public float FlickThresholdDeg;       // errors bigger than this become a flick
	public float FlickUse;                // chance a big correction is a flick rather than a slow turn
	public float LookSpeedDeg;            // turn rate when just looking around
	public float HeadAimChance;           // chance to hold the crosshair at head height
	public float FireCone;                // fire when the error is inside this multiple of the body radius
	public float MoveAimPenalty;          // extra wobble while the bot itself is moving

	// ---- Trigger discipline ----
	public int BurstMin, BurstMax;
	public float BurstPauseMin, BurstPauseMax;
	public float SemiDelayMin, SemiDelayMax;

	// ---- Behaviour ----
	public float MistakeRate;             // per decision chance of a human-style blunder
	public float AfkChance;               // per 4 s chance of zoning out (Easy only)
	public float CoverUse;                // 0..1 how much the bot plays around cover
	public float Aggression;              // 0..1 push vs hold
	public float SprintLove;              // 0..1 sprints instead of walking when roaming
	public float StrafeMin, StrafeMax;    // seconds per strafe direction
	public bool CounterStrafe;            // stops moving for a beat before a burst
	public float LowHealthCaution;        // hp fraction under which the bot plays safe

	public static BotProfile For(BotDifficulty level)
	{
		switch (level)
		{
			case BotDifficulty.Easy: return CreateEasy();
			case BotDifficulty.Advanced: return CreateAdvanced();
			default: return CreateNormal();
		}
	}

	private static BotProfile CreateEasy() => new BotProfile
	{
		Label = "Easy", Tag = "E",
		VisionHalfAngleDeg = 42f, VisionRange = 28f, PeripheralGain = 0.0f,
		ReactMin = 0.55f, ReactMax = 1.0f, MemorySeconds = 2.0f,
		PerceptionDelay = 0.30f, Lead = 0.0f, PositionNoise = 0.35f,
		HearingScale = 0.55f, HearingReliability = 0.45f, SoundError = 4.5f,
		HitFreezeMin = 0.5f, HitFreezeMax = 1.1f, CalloutChance = 0.0f, KillInference = 0f,
		TrackSpeedDeg = 200f, FlickSpeedDeg = 300f, FlickAccelDeg = 1800f, Overconfidence = 1.35f,
		TrackTau = 0.20f, HandNoiseDeg = 2.6f, HandNoiseTau = 0.30f, FlickErrorDeg = 3.5f,
		CorrectionTau = 0.35f, RecoilCompensation = 0.10f, FlickThresholdDeg = 12f, FlickUse = 0.25f,
		LookSpeedDeg = 90f, HeadAimChance = 0.04f, FireCone = 3.0f, MoveAimPenalty = 1.8f,
		BurstMin = 8, BurstMax = 30, BurstPauseMin = 0.15f, BurstPauseMax = 0.5f,
		SemiDelayMin = 0.25f, SemiDelayMax = 0.7f,
		MistakeRate = 0.30f, AfkChance = 0.08f, CoverUse = 0.05f, Aggression = 0.65f, SprintLove = 0.75f,
		StrafeMin = 0.6f, StrafeMax = 1.6f, CounterStrafe = false, LowHealthCaution = 0.0f,
	};

	private static BotProfile CreateNormal() => new BotProfile
	{
		Label = "Normal", Tag = "N",
		VisionHalfAngleDeg = 58f, VisionRange = 45f, PeripheralGain = 0.35f,
		ReactMin = 0.30f, ReactMax = 0.50f, MemorySeconds = 6.0f,
		PerceptionDelay = 0.14f, Lead = 0.72f, PositionNoise = 0.12f,
		HearingScale = 0.85f, HearingReliability = 0.75f, SoundError = 2.0f,
		HitFreezeMin = 0.15f, HitFreezeMax = 0.35f, CalloutChance = 0.35f, KillInference = 8f,
		TrackSpeedDeg = 420f, FlickSpeedDeg = 750f, FlickAccelDeg = 4800f, Overconfidence = 1.10f,
		TrackTau = 0.09f, HandNoiseDeg = 1.0f, HandNoiseTau = 0.22f, FlickErrorDeg = 1.4f,
		CorrectionTau = 0.16f, RecoilCompensation = 0.58f, FlickThresholdDeg = 12f, FlickUse = 0.65f,
		LookSpeedDeg = 170f, HeadAimChance = 0.28f, FireCone = 1.5f, MoveAimPenalty = 0.6f,
		BurstMin = 3, BurstMax = 7, BurstPauseMin = 0.20f, BurstPauseMax = 0.45f,
		SemiDelayMin = 0.15f, SemiDelayMax = 0.4f,
		MistakeRate = 0.10f, AfkChance = 0f, CoverUse = 0.55f, Aggression = 0.5f, SprintLove = 0.5f,
		StrafeMin = 0.4f, StrafeMax = 1.1f, CounterStrafe = false, LowHealthCaution = 0.35f,
	};

	private static BotProfile CreateAdvanced() => new BotProfile
	{
		Label = "Advanced", Tag = "A",
		VisionHalfAngleDeg = 70f, VisionRange = 75f, PeripheralGain = 0.6f,
		ReactMin = 0.20f, ReactMax = 0.32f, MemorySeconds = 12.0f,
		PerceptionDelay = 0.09f, Lead = 0.88f, PositionNoise = 0.06f,
		HearingScale = 1.15f, HearingReliability = 0.95f, SoundError = 0.8f,
		HitFreezeMin = 0.0f, HitFreezeMax = 0.1f, CalloutChance = 0.85f, KillInference = 4f,
		TrackSpeedDeg = 680f, FlickSpeedDeg = 1400f, FlickAccelDeg = 10000f, Overconfidence = 1.04f,
		TrackTau = 0.06f, HandNoiseDeg = 0.5f, HandNoiseTau = 0.16f, FlickErrorDeg = 0.7f,
		CorrectionTau = 0.085f, RecoilCompensation = 0.86f, FlickThresholdDeg = 12f, FlickUse = 0.92f,
		LookSpeedDeg = 260f, HeadAimChance = 0.45f, FireCone = 1.0f, MoveAimPenalty = 0.35f,
		BurstMin = 2, BurstMax = 5, BurstPauseMin = 0.12f, BurstPauseMax = 0.30f,
		SemiDelayMin = 0.10f, SemiDelayMax = 0.24f,
		MistakeRate = 0.025f, AfkChance = 0f, CoverUse = 0.9f, Aggression = 0.55f, SprintLove = 0.3f,
		StrafeMin = 0.3f, StrafeMax = 0.9f, CounterStrafe = true, LowHealthCaution = 0.4f,
	};
}
