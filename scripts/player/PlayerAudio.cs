using Godot;

/// <summary>
/// Everything the player's body makes noise about: footsteps, landings, jumping, crouching,
/// sprinting, taking a hit, dying, respawning and (for the local player only) breathing hard at low
/// health. All of it 3D and positional except the low-health breathing, and all of it running on
/// EVERY peer rather than just the owner - hearing someone sprint up behind you is the whole point.
///
/// Remote copies read ReplicatedMovementState (a synchronized string) rather than PlayerMovement's
/// local State, which is only ever written on the authority. The authority writes both every
/// frame, so one code path covers both cases.
/// </summary>
public partial class PlayerAudio : Node3D
{
	private const string FootstepDirectory = "res://audio/Clips/Player/Footsteps And Landing";
	private const string HitDeadRespawnDirectory = "res://audio/Clips/Player/Hit Dead And Respawn";
	private const string CleanDeathSound = HitDeadRespawnDirectory + "/PlayerDead.wav";
	private const string StruggleDeathSound = HitDeadRespawnDirectory + "/PlayerDeathFinisher.wav";
	private const string RespawnSound = HitDeadRespawnDirectory + "/PlayerRespawn.wav";

	private const string MovementDirectory = "res://audio/Clips/Player/Movement";
	private const string JumpSound = MovementDirectory + "/PlayerJump.wav";
	private const string CrouchSound = MovementDirectory + "/PlayerCrouch.wav";
	private const string WalkLoopSound = MovementDirectory + "/PlayerWalkLoop.wav";
	private const string SprintLoopSound = MovementDirectory + "/PlayerSprintLoop.wav";

	private const string LowHealthBreathingSound = "res://audio/Clips/Player/Low Health/LowHealthBreathing.wav";

	/// <summary>Below this fraction of MaxHealth, the local player hears their own breathing.
	/// Nobody else does - see the note on _lowHealthPlayer below.</summary>
	private const float LowHealthThreshold = 0.3f;

	// Stride intervals in seconds. Sprint is not simply "walk but faster" - the sprint clip has a
	// longer stride, so the cadence is tuned per state rather than derived from speed.
	private const float WalkStepSeconds = 0.48f;
	private const float SprintStepSeconds = 0.34f;
	private const float CrouchStepSeconds = 0.72f;

	private PlayerMovement _movement = null!;
	private Health? _health;
	private KillFeed? _killFeed;
	private AudioStreamPlayer3D _footstepPlayer = null!;
	private AudioStreamPlayer3D? _voicePlayer;
	private AudioStreamPlayer3D? _actionPlayer;
	private AudioStreamPlayer3D? _locomotionLoopPlayer;
	// Local-only (non-positional, no AudioStreamPlayer3D): this represents the player's own
	// laboured breathing, not something happening in the world at a point other players could hear
	// from a direction - the same "owner-only, non-positional" reasoning WeaponSwitcher's hit
	// marker already uses (see SOUND_SLOTS.md's "Shooter feedback" section).
	private AudioStreamPlayer? _lowHealthPlayer;

	private AudioStream[] _footsteps = System.Array.Empty<AudioStream>();
	private AudioStream[] _hits = System.Array.Empty<AudioStream>();
	private AudioStream? _cleanDeath;
	private AudioStream? _struggleDeath;
	private AudioStream? _respawn;
	private AudioStream? _jump;
	private AudioStream? _crouch;
	private AudioStream? _walkLoop;
	private AudioStream? _sprintLoop;
	private AudioStream? _lowHealthBreathing;
	private readonly RandomNumberGenerator _rng = new();

	private float _stepTimer;
	private float _lastKnownHealth = -1.0f;
	private bool _wasDead;
	private PlayerMovement.MovementState _previousState = PlayerMovement.MovementState.Idle;
	private bool _lowHealthActive;

	/// <summary>Set by OnKillAnnounced the moment this player's own death is confirmed on the
	/// network, and consumed the next time UpdateHealthSounds notices the alive-to-dead edge - see
	/// the class doc on why picking the death clip can't just be done locally from CurrentHealth
	/// alone. Defaults to a clean death, which only matters if this player dies before any
	/// KillAnnounced ever reaches them (KillFeed missing, most likely in single-instance dev play
	/// with no autoload configured) - a clean death is the more common case, so it is the safer
	/// default to fall back on.</summary>
	private bool _pendingDeathStruggles;

	public override void _Ready()
	{
		_rng.Randomize();
		_movement = (PlayerMovement)GetParent();
		_health = _movement.GetNodeOrNull<Health>("Health");
		_killFeed = KillFeed.From(this);
		if (_killFeed != null) _killFeed.KillAnnounced += OnKillAnnounced;

		_footsteps = LoadFootsteps();
		_hits = LoadExisting($"{HitDeadRespawnDirectory}/PlayerHit.wav", $"{HitDeadRespawnDirectory}/PlayerHit_02.wav");
		_cleanDeath = Load(CleanDeathSound);
		_struggleDeath = Load(StruggleDeathSound);
		_respawn = Load(RespawnSound);
		_jump = Load(JumpSound);
		_crouch = Load(CrouchSound);
		_walkLoop = Load(WalkLoopSound);
		_sprintLoop = Load(SprintLoopSound);

		// Footsteps sit low and carry a short distance; the hit/death voice carries further because
		// it is a positional cue about a fight, not a detail of someone's gait.
		_footstepPlayer = new AudioStreamPlayer3D { Name = "Footsteps", UnitSize = 7.0f, VolumeDb = -3.0f, Bus = "Movement" };
		AddChild(_footstepPlayer);
		_voicePlayer = new AudioStreamPlayer3D { Name = "Voice", UnitSize = 14.0f, Bus = "Voice" };
		AddChild(_voicePlayer);
		// Jump/crouch get their own player rather than sharing Footsteps or Voice: both of those
		// already restart on a schedule this pass doesn't control (a footstep lands mid-air-grunt, a
		// hit voice line plays mid-crouch-foley), and stealing either one's node would cut a sound
		// off partway through instead of just layering another one in.
		_actionPlayer = new AudioStreamPlayer3D { Name = "Action", UnitSize = 7.0f, Bus = "Movement" };
		AddChild(_actionPlayer);

		// A quiet loop under the discrete footstep clicks - texture (gear, cloth, breathing rhythm),
		// not the primary "where are their feet" cue, which stays with the existing per-step system
		// above. Manually re-triggered on Finished rather than relying on the .wav's own loop flag,
		// which this pass has no Godot editor open to set on import.
		_locomotionLoopPlayer = new AudioStreamPlayer3D { Name = "LocomotionLoop", UnitSize = 6.0f, VolumeDb = -11.0f, Bus = "Movement" };
		AddChild(_locomotionLoopPlayer);
		_locomotionLoopPlayer.Finished += OnLocomotionLoopFinished;

		// Only the owning player ever needs to hear their own low-health breathing, so only their
		// copy of this node builds the player for it at all.
		if (_movement.IsMultiplayerAuthority())
		{
			_lowHealthBreathing = Load(LowHealthBreathingSound);
			_lowHealthPlayer = new AudioStreamPlayer { Name = "LowHealthBreathing", Bus = "Voice", VolumeDb = -4.0f };
			AddChild(_lowHealthPlayer);
			_lowHealthPlayer.Finished += OnLowHealthLoopFinished;
		}

		if (_footsteps.Length == 0)
			GD.PushWarning($"No footstep clips found under {FootstepDirectory}.");
	}

	public override void _ExitTree()
	{
		if (_killFeed != null && IsInstanceValid(_killFeed)) _killFeed.KillAnnounced -= OnKillAnnounced;
	}

	public override void _PhysicsProcess(double delta)
	{
		UpdateHealthSounds();
		UpdateFootsteps((float)delta);
		UpdateMovementStateSounds();
		UpdateLowHealthBreathing();
	}

	/// <summary>
	/// Every peer's KillFeed fires for every kill in the match, not just this player's own - the
	/// victimPeerId check is what narrows it down to "did I just die". struggles is the victim's own
	/// device's read of how the kill happened (see Health.DetermineIfDeathStruggles): headshot or a
	/// single 100+ damage hit reads as instant UNLESS it was melee or this player was already on the
	/// verge of death, in which case it plays the slower, struggling clip instead.
	/// </summary>
	private void OnKillAnnounced(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
		int totalPoints, long assistPeerId, int assistPoints, bool struggles)
	{
		if (victimPeerId == _movement.GetMultiplayerAuthority()) _pendingDeathStruggles = struggles;
	}

	private void UpdateFootsteps(float delta)
	{
		if (_footsteps.Length == 0) return;
		if (_health != null && _health.IsDead) return;

		float interval = StrideSecondsFor(CurrentState());
		if (interval <= 0.0f)
		{
			// Standing still: arm the next step so the very first stride after starting to move
			// lands immediately instead of half a stride later.
			_stepTimer = 0.0f;
			return;
		}

		_stepTimer -= delta;
		if (_stepTimer > 0.0f) return;
		_stepTimer = interval;
		_footstepPlayer.Stream = _footsteps[_rng.RandiRange(0, _footsteps.Length - 1)];
		_footstepPlayer.PitchScale = _rng.RandfRange(0.92f, 1.08f);
		_footstepPlayer.Play();
	}

	private static float StrideSecondsFor(PlayerMovement.MovementState state) => state switch
	{
		PlayerMovement.MovementState.Walk => WalkStepSeconds,
		PlayerMovement.MovementState.Sprint => SprintStepSeconds,
		PlayerMovement.MovementState.CrouchWalk => CrouchStepSeconds,
		// Idle, Crouch, Slide, Jump, Fall and Aim are all silent - a slide should hiss rather than
		// step, and there is no slide clip in the migrated audio yet.
		_ => 0.0f,
	};

	/// <summary>
	/// Jump take-off, crouch settle, and the walk/sprint locomotion loop, all driven by watching
	/// CurrentState()'s transitions rather than by signals - the same reason UpdateHealthSounds
	/// polls CurrentHealth instead of Health's Died/Respawned signals: PlayerMovement's real input
	/// handling only ever runs on the owning peer, so a signal raised there would never reach a
	/// remote observer's copy of this node. ReplicatedMovementState is what actually crosses the
	/// network, so reading it here is what lets every peer hear it.
	/// </summary>
	private void UpdateMovementStateSounds()
	{
		var state = CurrentState();
		if (_health == null || !_health.IsDead)
		{
			if (state == PlayerMovement.MovementState.Jump && _previousState != PlayerMovement.MovementState.Jump)
				PlayAction(_jump);

			bool enteringCrouch = state is PlayerMovement.MovementState.Crouch or PlayerMovement.MovementState.CrouchWalk;
			bool wasCrouching = _previousState is PlayerMovement.MovementState.Crouch or PlayerMovement.MovementState.CrouchWalk;
			if (enteringCrouch && !wasCrouching)
				PlayAction(_crouch);
		}

		UpdateLocomotionLoop(state);
		_previousState = state;
	}

	private void PlayAction(AudioStream? stream)
	{
		if (stream == null || _actionPlayer == null) return;
		_actionPlayer.Stream = stream;
		_actionPlayer.PitchScale = _rng.RandfRange(0.95f, 1.05f);
		_actionPlayer.Play();
	}

	private void UpdateLocomotionLoop(PlayerMovement.MovementState state)
	{
		if (_locomotionLoopPlayer == null) return;
		if (_health != null && _health.IsDead)
		{
			_locomotionLoopPlayer.Stop();
			return;
		}

		AudioStream? desired = state switch
		{
			PlayerMovement.MovementState.Walk or PlayerMovement.MovementState.CrouchWalk => _walkLoop,
			PlayerMovement.MovementState.Sprint => _sprintLoop,
			_ => null,
		};

		if (desired == null)
		{
			if (_locomotionLoopPlayer.Playing) _locomotionLoopPlayer.Stop();
			return;
		}
		if (_locomotionLoopPlayer.Stream != desired)
		{
			_locomotionLoopPlayer.Stream = desired;
			_locomotionLoopPlayer.Play();
		}
		else if (!_locomotionLoopPlayer.Playing)
		{
			_locomotionLoopPlayer.Play();
		}
	}

	private void OnLocomotionLoopFinished()
	{
		// Re-trigger to fake a seamless loop (see the _Ready note on why this isn't the .wav's own
		// loop flag). Only if the state that wanted it playing is still current - otherwise a loop
		// that happened to finish in the same frame the player stopped moving would restart once
		// more before the next physics tick catches up and stops it.
		if (!GodotObject.IsInstanceValid(_locomotionLoopPlayer)) return;
		var state = CurrentState();
		bool stillWanted = state is PlayerMovement.MovementState.Walk
			or PlayerMovement.MovementState.CrouchWalk or PlayerMovement.MovementState.Sprint;
		if (stillWanted) _locomotionLoopPlayer!.Play();
	}

	/// <summary>Owner-only. CurrentHealth crossing LowHealthThreshold starts/stops a looping
	/// breathing track - manually re-triggered on Finished, same technique as the locomotion loop.</summary>
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

	/// <summary>
	/// Hit / death / respawn are driven by watching CurrentHealth rather than by signals, because
	/// Health only emits Respawned on the owning peer. CurrentHealth is replicated, so polling it
	/// gives every peer the same three transitions at roughly the same moment. Which death clip
	/// plays comes from _pendingDeathStruggles (see OnKillAnnounced), not from anything computed
	/// here.
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
		if (isDead && !_wasDead)
		{
			PlayVoice(_pendingDeathStruggles ? _struggleDeath : _cleanDeath);
			_pendingDeathStruggles = false;
		}
		else if (!isDead && _wasDead) PlayVoice(_respawn);
		else if (!isDead && current < _lastKnownHealth - 0.01f) PlayVoice(PickRandom(_hits));

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
		if (_movement.IsMultiplayerAuthority()) return _movement.State;
		return System.Enum.TryParse(_movement.ReplicatedMovementState,
			out PlayerMovement.MovementState remote)
			? remote
			: PlayerMovement.MovementState.Idle;
	}

	private static AudioStream[] LoadFootsteps()
	{
		var streams = new System.Collections.Generic.List<AudioStream>();
		for (int i = 1; i <= 9; i++)
		{
			var stream = Load($"{FootstepDirectory}/Player_Footstep_0{i}.wav");
			if (stream != null) streams.Add(stream);
		}
		return streams.ToArray();
	}

	/// <summary>Loads whichever of the given paths actually resolve, silently skipping any that
	/// don't - used for the hit-variant list so a project missing the newer second clip still plays
	/// the original one instead of nothing.</summary>
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

	private static AudioStream? Load(string path) => ResourceLoader.Load<AudioStream>(path);
}
