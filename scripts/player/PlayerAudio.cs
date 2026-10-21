using Godot;
using GameAudio;

/// <summary>
/// Everything the player's body makes noise about: walking, running, jumping, taking a
/// hit, dying (or not making a sound about it), respawning and - for the local
/// player only - breathing hard at low health. All of it 3D and positional except the low-health
/// breathing, and all of it running on EVERY peer rather than just the owner - hearing someone
/// sprint up behind you is the whole point.
///
/// Remote copies read ReplicatedMovementState (a synchronized string) rather than PlayerMovement's
/// local State, which is only ever written on the authority. The authority writes both every
/// frame, so one code path covers both cases.
///
/// FOOTSTEPS: there is no walk/sprint loop clip anymore (the quality_audio pack has no multi-second
/// pre-mixed loop file to replace the old one with, only discrete one-shot steps) - every stride is
/// now a one-shot picked by SurfaceClassifier via UpdateSurfaceFootstepAccent, at the same
/// WalkStepSeconds/SprintStepSeconds/CrouchStepSeconds cadence the old fallback system already had
/// tuned. See that method, and CurrentSurfaceFolder for which material it reads.
///
/// DEATH SOUNDS (the rules, in one place - see also Health.DetermineIfDeathStruggles):
///  - Instant death (headshot or explosive hit): NO sound, for anyone.
///  - Every other death (a body shot, any melee kill): the victim makes a struggling sound that
///    whoever is close enough hears - it is a positional sound, so "close enough" is just distance.
///  - Downed (at or below 20% HP): a breathing loop plays for the victim. If no medic revives within
///    Health.DownedWindowSeconds the struggle plays as the death. A hit while downed is instant and silent.
///    The medic voice recording is reserved for future use and is not loaded or played by this script.
///
/// SOUND SOURCE: audio/SFX (the quality_audio pack) for everything below except low-health
/// breathing, which the pack has no equivalent for - see LowHealthBreathingSound's own comment.
/// </summary>
public partial class PlayerAudio : Node3D
{
	// Several variants now instead of one fixed clip (the pack ships three) - PickRandom below rolls
	// one at play time, same idea as WeaponAttachment's RollFireVariant.
	private static readonly string[] HitSounds =
	{
		"res://audio/SFX/impacts/blood_01.wav",
		"res://audio/SFX/impacts/blood_02.wav",
		"res://audio/SFX/impacts/blood_03.wav",
	};
	// "Man down" reads as the moment-of-death vocalization the doc comment above describes. No
	// per-character gender exists anywhere in CharacterData yet, so - same as every other sound in
	// this file - one voice is used for everyone rather than guessing at a new per-character system.
	private const string StruggleSound = "res://audio/SFX/voice/male/man_down.wav";
	private const string RespawnSound = "res://audio/SFX/voice/male/relief.wav";
	// No dedicated jump take-off sound in the pack; "low hop" is the closest thing it ships to a
	// push-off grunt.
	private const string JumpSound = "res://audio/SFX/player/low_hop.wav";

	// No breathing-loop clip anywhere in the pack (voice/ has pain/relief/kill lines, nothing
	// sustained), so this one is deliberately left on the old asset rather than going silent on a
	// real gameplay cue - see cleanup_old_audio.sh, which carves this file out of the old folder it
	// otherwise deletes for exactly this reason.
	private const string LowHealthBreathingSound = "res://audio/Clips/Player/Low Health/LowHealthBreathing.wav";

	/// <summary>Below this fraction of MaxHealth, the local player hears their own breathing.
	/// Nobody else does - see the note on _lowHealthPlayer below.</summary>
	private const float LowHealthThreshold = 0.30f;

	// --- levels -------------------------------------------------------------------------------
	// Starting points, not by-ear values - nudge these once you've actually heard the new clips.
	private const float JumpVolumeDb = -3.0f;
	private const float HitVolumeDb = -2.0f;
	private const float StruggleVolumeDb = 0.0f;
	private const float LowHealthVolumeDb = 0.0f;

	// Stride interval in seconds, per movement state - drives UpdateSurfaceFootstepAccent below.
	private const float WalkStepSeconds = 0.48f;
	private const float SprintStepSeconds = 0.34f;
	private const float CrouchStepSeconds = 0.72f;

	private PlayerMovement _movement = null!;
	private Health? _health;
	private KillFeed? _killFeed;
	private AudioStreamPlayer3D? _voicePlayer;
	private AudioStreamPlayer3D? _deathPlayer;
	private AudioStreamPlayer3D? _actionPlayer;
	// Local-only (non-positional, no AudioStreamPlayer3D): this represents the player's own
	// laboured breathing, not something happening in the world at a point other players could hear
	// from a direction - the same "owner-only, non-positional" reasoning WeaponSwitcher's hit
	// marker already uses.
	private AudioStreamPlayer? _lowHealthPlayer;

	private AudioStream[] _hits = System.Array.Empty<AudioStream>();
	private AudioStream? _struggle;
	private AudioStream? _respawn;
	private AudioStream? _jump;
	private AudioStream? _lowHealthBreathing;
	private readonly RandomNumberGenerator _rng = new();

	// Drives the surface-specific footstep one-shot (see UpdateSurfaceFootstepAccent) - the sole
	// footstep mechanism now that there is no loop clip to layer it on top of.
	private float _surfaceStepTimer;
	private float _lastKnownHealth = -1.0f;
	private bool _wasDead;
	private PlayerMovement.MovementState _previousState = PlayerMovement.MovementState.Idle;
	private bool _lowHealthActive;

	// Team-match bleed-out, run identically on every peer from the same KillAnnounced broadcast.

	public override void _Ready()
	{
		_rng.Randomize();
		_movement = (PlayerMovement)GetParent();
		_health = _movement.GetNodeOrNull<Health>("Health");
		_killFeed = KillFeed.From(this);
		if (_killFeed != null) _killFeed.KillAnnounced += OnKillAnnounced;

		_hits = LoadExisting(HitSounds);
		_struggle = Load(StruggleSound);
		_respawn = Load(RespawnSound);
		_jump = Load(JumpSound);

		_voicePlayer = new AudioStreamPlayer3D { Name = "Voice", UnitSize = 14.0f, VolumeDb = HitVolumeDb, Bus = "Voice" };
		AddChild(_voicePlayer);

		// The struggle is meant for whoever is right next to the dying player, so it has a short
		// unit size and a hard cutoff. It gets its own player so a hit or respawn sound on the
		// shared Voice player can never cut it off partway through.
		_deathPlayer = new AudioStreamPlayer3D { Name = "DeathStruggle", UnitSize = 6.0f, VolumeDb = StruggleVolumeDb, MaxDistance = 25.0f, Bus = "Voice" };
		AddChild(_deathPlayer);

		// Jump gets its own player rather than sharing Voice: stealing that node would cut a voice
		// line off partway through instead of just layering the jump accent in alongside it.
		_actionPlayer = new AudioStreamPlayer3D { Name = "Action", UnitSize = 7.0f, MaxDistance = 40.0f, Bus = "Movement" };
		AddChild(_actionPlayer);

		// Only the owning player ever needs to hear their own low-health breathing, so only their
		// copy of this node builds the player for it at all.
		// "Owning" means the person at this machine - a bot the host simulates is authority here too
		// and must not get a breathing track.
		if (PlayerIdentity.IsLocalHuman(_movement))
		{
			_lowHealthBreathing = Load(LowHealthBreathingSound);
			_lowHealthPlayer = new AudioStreamPlayer { Name = "LowHealthBreathing", Bus = "Voice", VolumeDb = LowHealthVolumeDb };
			AddChild(_lowHealthPlayer);
			_lowHealthPlayer.Finished += OnLowHealthLoopFinished;
		}
	}

	public override void _ExitTree()
	{
		if (_killFeed != null && IsInstanceValid(_killFeed)) _killFeed.KillAnnounced -= OnKillAnnounced;
	}

	public override void _PhysicsProcess(double delta)
	{
		UpdateHealthSounds();
		UpdateMovementStateSounds();
		UpdateSurfaceFootstepAccent((float)delta);
		UpdateLowHealthBreathing();
	}

	// -------------------------------------------------------------------------------------------
	// Death
	// -------------------------------------------------------------------------------------------

	/// <summary>
	/// Every peer's KillFeed fires for every kill in the match, not just this player's own - the
	/// victimPeerId check narrows it down to "did I just die". The death sound is driven from here,
	/// not from watching CurrentHealth: KillAnnounced is the one message that reliably reaches every
	/// peer exactly once and carries the victim's own read of how they died (struggles = false means
	/// an instant death - see Health.DetermineIfDeathStruggles). Watching CurrentHealth instead would
	/// race against this message, since the two arrive by separate replication paths in no fixed order.
	/// </summary>
	private void OnKillAnnounced(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
		int totalPoints, long assistPeerId, int assistPoints, bool struggles)
	{
		// PlayerIdentity, not raw authority: every bot shares the host's authority, so authority
		// cannot tell which player died. People use their peer id, bots their own fake id.
		if (victimPeerId != PlayerIdentity.IdOf(_movement)) return;
		StartDeathAudio(struggles);
	}

	private void StartDeathAudio(bool struggles)
	{
		CancelDeathAudio();

		// An instant death (a hit while downed, a headshot, an explosion) is silent. Nothing plays, for anyone.
		if (!struggles) return;

		// The struggle is the moment of death: the downed window already played out on the victim's
		// device (see Health.DownedWindowSeconds), so the struggle sound starts now, for everyone nearby.
		PlayDeathStruggle();
	}

	private void CancelDeathAudio()
	{
		if (_deathPlayer != null && _deathPlayer.Playing) _deathPlayer.Stop();
	}

	private void PlayDeathStruggle()
	{
		if (_struggle == null || _deathPlayer == null) return;
		_deathPlayer.Stream = _struggle;
		_deathPlayer.PitchScale = _rng.RandfRange(0.96f, 1.04f);
		_deathPlayer.Play();
	}

	// -------------------------------------------------------------------------------------------
	// Movement
	// -------------------------------------------------------------------------------------------

	private static float StrideSecondsFor(PlayerMovement.MovementState state) => state switch
	{
		PlayerMovement.MovementState.Walk => WalkStepSeconds,
		PlayerMovement.MovementState.Sprint => SprintStepSeconds,
		PlayerMovement.MovementState.CrouchWalk => CrouchStepSeconds,
		// Idle, Crouch, Slide, Jump, Fall and Aim are all silent.
		_ => 0.0f,
	};

	/// <summary>
	/// The sole footstep mechanism (see the class doc comment for why there is no loop clip to layer
	/// this on top of anymore). Runs on every peer the same way UpdateMovementStateSounds does, off
	/// CurrentState() rather than raw velocity, since velocity itself is only meaningful on the
	/// authority.
	/// </summary>
	private void UpdateSurfaceFootstepAccent(float delta)
	{
		if (_health != null && _health.IsDead) { _surfaceStepTimer = 0.0f; return; }

		float interval = StrideSecondsFor(CurrentState());
		if (interval <= 0.0f) { _surfaceStepTimer = 0.0f; return; }

		_surfaceStepTimer -= delta;
		if (_surfaceStepTimer > 0.0f) return;
		_surfaceStepTimer = interval;
		Audio.PlayAt($"surfaces/{CurrentSurfaceFolder()}/step", GlobalPosition);
	}

	/// <summary>Maps SurfaceClassifier's read of what PlayerMovement reports standing on to a
	/// folder under audio/SFX/surfaces. Glass has no footstep clips in the pack (only an impact
	/// crack - see WeaponAttachment's bullet-impact use of SurfaceType.Glass, a different system),
	/// and Generic is this project's own documented "no material identity left to read" catch-all
	/// (see SurfaceType.cs) - both fall back to concrete, a reasonable default for maps this
	/// industrial/urban (Warehouse, Prison, Depot, Lab).</summary>
	private string CurrentSurfaceFolder() => SurfaceClassifier.Classify(_movement.GroundCollider()) switch
	{
		SurfaceType.Metal => "metal",
		SurfaceType.Wood => "wood",
		SurfaceType.Concrete => "concrete",
		SurfaceType.Dirt => "dirt",
		SurfaceType.Tile => "tile",
		SurfaceType.Water => "water",
		_ => "concrete",
	};

	private void UpdateMovementStateSounds()
	{
		var state = CurrentState();
		if (_health == null || !_health.IsDead)
		{
			if (state == PlayerMovement.MovementState.Jump && _previousState != PlayerMovement.MovementState.Jump)
				PlayAction(_jump, JumpVolumeDb);
			else if (IsAirborne(_previousState) && !IsAirborne(state))
				Audio.PlayAt($"surfaces/{CurrentSurfaceFolder()}/land", GlobalPosition);

			// No crouch-settle clip anywhere in the pack - dropped rather than reusing something
			// that would read wrong. Tell me if you add/find one and I'll wire it back in.
		}

		_previousState = state;
	}

	private void PlayAction(AudioStream? stream, float volumeDb)
	{
		if (stream == null || _actionPlayer == null) return;
		_actionPlayer.Stream = stream;
		_actionPlayer.VolumeDb = volumeDb;
		_actionPlayer.PitchScale = _rng.RandfRange(0.95f, 1.05f);
		_actionPlayer.Play();
	}

	private static bool IsAirborne(PlayerMovement.MovementState state) =>
		state is PlayerMovement.MovementState.Jump or PlayerMovement.MovementState.Fall;

	/// <summary>Owner-only. CurrentHealth crossing LowHealthThreshold starts/stops a looping
	/// breathing track - manually re-triggered on Finished.</summary>
	private void UpdateLowHealthBreathing()
	{
		if (_lowHealthPlayer == null || _health == null || _lowHealthBreathing == null) return;

		bool shouldBreathe = !_health.IsDead && _health.CurrentHealth <= _health.MaxHealth * LowHealthThreshold;
		if (shouldBreathe == _lowHealthActive) return;
		_lowHealthActive = shouldBreathe;

		if (shouldBreathe)
		{
			_lowHealthPlayer.Stream = _lowHealthBreathing;
			_lowHealthPlayer.Play();
		}
		else
		{
			_lowHealthPlayer.Stop();
		}
	}

	private void OnLowHealthLoopFinished()
	{
		if (!GodotObject.IsInstanceValid(_lowHealthPlayer)) return;
		if (_lowHealthActive) _lowHealthPlayer!.Play();
	}

	// -------------------------------------------------------------------------------------------
	// Hit / respawn
	// -------------------------------------------------------------------------------------------

	/// <summary>
	/// The "I've been shot" grunt and the respawn sound, driven by watching CurrentHealth rather than
	/// by signals, because Health only emits Respawned on the owning peer. CurrentHealth is
	/// replicated, so polling it gives every peer the same transitions at roughly the same moment.
	/// The DEATH sound is deliberately not here - see OnKillAnnounced.
	/// </summary>
	private void UpdateHealthSounds()
	{
		if (_health == null || _voicePlayer == null) return;
		float current = _health.CurrentHealth;

		if (_lastKnownHealth < 0.0f)
		{
			_lastKnownHealth = current;
			_wasDead = _health.IsDead;
			return;
		}

		bool isDead = _health.IsDead;
		if (!isDead && _wasDead)
		{
			// Respawned. Drop anything left over from the last death.
			CancelDeathAudio();
			PlayVoice(_respawn);
		}
		else if (!isDead && current < _lastKnownHealth - 0.01f)
		{
			// Took a hit and is still standing. (The killing blow makes no "hit" sound - the death
			// rules above decide what, if anything, is heard instead.)
			PlayVoice(PickRandom(_hits));
		}

		_lastKnownHealth = current;
		_wasDead = isDead;
	}

	private void PlayVoice(AudioStream? stream)
	{
		if (stream == null || _voicePlayer == null) return;
		_voicePlayer.Stream = stream;
		_voicePlayer.PitchScale = _rng.RandfRange(0.96f, 1.04f);
		_voicePlayer.Play();
	}

	private AudioStream? PickRandom(AudioStream[] streams) =>
		streams.Length == 0 ? null : streams[_rng.RandiRange(0, streams.Length - 1)];

	private PlayerMovement.MovementState CurrentState()
	{
		if (PlayerIdentity.HasAuthority(_movement)) return _movement.State;
		return System.Enum.TryParse(_movement.ReplicatedMovementState,
			out PlayerMovement.MovementState remote)
			? remote
			: PlayerMovement.MovementState.Idle;
	}

	// -------------------------------------------------------------------------------------------
	// Loading
	// -------------------------------------------------------------------------------------------

	/// <summary>Loads whichever of the given paths actually resolve, silently skipping any that
	/// don't.</summary>
	private static AudioStream[] LoadExisting(params string[] paths)
	{
		var streams = new System.Collections.Generic.List<AudioStream>();
		foreach (string path in paths)
		{
			var stream = Load(path);
			if (stream != null) streams.Add(stream);
		}
		return streams.ToArray();
	}

	/// <summary>Exists-check first, so an optional clip that is not in the project is a quiet null
	/// instead of an engine error.</summary>
	private static AudioStream? Load(string path) =>
		ResourceLoader.Exists(path) ? ResourceLoader.Load<AudioStream>(path) : null;
}
