using Godot;

/// <summary>
/// Everything the player's body makes noise about: walking, running, jumping, crouching, taking a
/// hit, dying (or not making a sound about it), calling for a medic, respawning and - for the local
/// player only - breathing hard at low health. All of it 3D and positional except the low-health
/// breathing, and all of it running on EVERY peer rather than just the owner - hearing someone
/// sprint up behind you is the whole point.
///
/// Remote copies read ReplicatedMovementState (a synchronized string) rather than PlayerMovement's
/// local State, which is only ever written on the authority. The authority writes both every
/// frame, so one code path covers both cases.
///
/// DEATH SOUNDS (the rules, in one place - see also Health.DetermineIfDeathStruggles):
///  - Instant death (headshot, Bazooka hit): NO sound, for anyone.
///  - Every other death (a body shot, any melee kill): the victim makes a struggling sound that
///    whoever is close enough hears - it is a positional sound, so "close enough" is just distance.
///  - Team Deathmatch only: a non-instant death is a bleed-out. For Health.TeamBleedOutSeconds the
///    victim calls "I need a medic" (teammates hear it), then they are eliminated and the struggle
///    plays. Deathmatch (free-for-all) never plays the medic call - nobody is coming to help.
/// </summary>
public partial class PlayerAudio : Node3D
{
	private const string FootstepDirectory = "res://audio/Clips/Player/Footsteps And Landing";
	private const string HitDeadRespawnDirectory = "res://audio/Clips/Player/Hit Dead And Respawn";
	private const string HitSound = HitDeadRespawnDirectory + "/PlayerHit.wav";
	private const string StruggleSound = HitDeadRespawnDirectory + "/PlayerDeathFinisher.wav";
	private const string MedicSound = HitDeadRespawnDirectory + "/PlayerMedicNeeded.wav";
	private const string RespawnSound = HitDeadRespawnDirectory + "/PlayerRespawn.wav";

	private const string MovementDirectory = "res://audio/Clips/Player/Movement";
	private const string JumpSound = MovementDirectory + "/PlayerJump.wav";
	private const string CrouchSound = MovementDirectory + "/PlayerCrouch.wav";
	private const string WalkSound = MovementDirectory + "/PlayerWalkLoop.wav";
	private const string SprintSound = MovementDirectory + "/PlayerSprintLoop.wav";

	private const string LowHealthBreathingSound = "res://audio/Clips/Player/Low Health/LowHealthBreathing.wav";

	/// <summary>Below this fraction of MaxHealth, the local player hears their own breathing.
	/// Nobody else does - see the note on _lowHealthPlayer below.</summary>
	private const float LowHealthThreshold = 0.3f;

	// --- levels -------------------------------------------------------------------------------
	// Gain trims in dB. The clips were recorded at wildly different levels (the walk/run clips are
	// hot, the crouch clip peaks around -29 dBFS), so each gets its own trim on top of its player.
	// All of these are by-the-numbers starting points, not by-ear values - nudge them here.
	private const float WalkVolumeDb = -6.0f;
	private const float SprintVolumeDb = -12.0f;
	private const float CrouchWalkVolumeDb = -16.0f;
	private const float CrouchWalkPitch = 0.85f;
	private const float JumpVolumeDb = -3.0f;
	private const float CrouchVolumeDb = 14.0f;
	private const float HitVolumeDb = -2.0f;
	private const float StruggleVolumeDb = 0.0f;
	private const float MedicVolumeDb = -3.0f;
	private const float LowHealthVolumeDb = 0.0f;

	// --- locomotion loops ---------------------------------------------------------------------
	// PlayerWalkLoop.wav is 2.0 s but only the first ~1.2 s is sound: two footsteps (~0.26 s and
	// ~0.86 s) and then silence. Looping the whole file would leave a 1.4 s gap after every second
	// step, so it is restarted at 1.2 s (in the silence) to give an even 0.6 s cadence.
	// PlayerSprintLoop.wav is two evenly spaced steps in 0.88 s and loops as it is (0 = whole clip).
	private const float WalkLoopSeconds = 1.2f;
	private const float SprintLoopSeconds = 0.0f;

	// Stride intervals in seconds for the OLD per-step footstep clips, used only if the walk/sprint
	// loops above are missing from the project.
	private const float WalkStepSeconds = 0.48f;
	private const float SprintStepSeconds = 0.34f;
	private const float CrouchStepSeconds = 0.72f;

	// --- team-match bleed-out -----------------------------------------------------------------
	/// <summary>Seconds between "I need a medic" calls: the 1.1 s clip plus a pause.</summary>
	private const float MedicRepeatSeconds = 2.6f;

	/// <summary>Where the struggle falls in a Team Deathmatch bleed-out. True: when the medic window
	/// runs out and the player is eliminated (the medic calls come first). False: the moment they go
	/// down, with the medic calls following it.</summary>
	private static readonly bool StruggleWhenBleedOutEnds = true;

	private PlayerMovement _movement = null!;
	private Health? _health;
	private KillFeed? _killFeed;
	private AudioStreamPlayer3D _footstepPlayer = null!;
	private AudioStreamPlayer3D? _voicePlayer;
	private AudioStreamPlayer3D? _deathPlayer;
	private AudioStreamPlayer3D? _medicPlayer;
	private AudioStreamPlayer3D? _actionPlayer;
	private AudioStreamPlayer3D? _locomotionLoopPlayer;
	// Local-only (non-positional, no AudioStreamPlayer3D): this represents the player's own
	// laboured breathing, not something happening in the world at a point other players could hear
	// from a direction - the same "owner-only, non-positional" reasoning WeaponSwitcher's hit
	// marker already uses.
	private AudioStreamPlayer? _lowHealthPlayer;

	private AudioStream[] _footsteps = System.Array.Empty<AudioStream>();
	private AudioStream[] _hits = System.Array.Empty<AudioStream>();
	private AudioStream? _struggle;
	private AudioStream? _medic;
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
	private PlayerMovement.MovementState? _loopState;
	private bool _lowHealthActive;

	// Team-match bleed-out, run identically on every peer from the same KillAnnounced broadcast.
	private bool _bleedingOut;
	private float _bleedOutRemaining;
	private float _bleedOutElapsed;
	private bool _bleedOutSawDead;
	private float _medicTimer;

	public override void _Ready()
	{
		_rng.Randomize();
		_movement = (PlayerMovement)GetParent();
		_health = _movement.GetNodeOrNull<Health>("Health");
		_killFeed = KillFeed.From(this);
		if (_killFeed != null) _killFeed.KillAnnounced += OnKillAnnounced;

		_footsteps = LoadFootsteps();
		_hits = LoadExisting(HitSound);
		_struggle = Load(StruggleSound);
		_medic = Load(MedicSound);
		_respawn = Load(RespawnSound);
		_jump = Load(JumpSound);
		_crouch = Load(CrouchSound);
		_walkLoop = Load(WalkSound);
		_sprintLoop = Load(SprintSound);

		// Footsteps (only used if the walk/sprint loops are missing) sit low and carry a short
		// distance; the hit voice carries further because it is a positional cue about a fight, not
		// a detail of someone's gait.
		_footstepPlayer = new AudioStreamPlayer3D { Name = "Footsteps", UnitSize = 7.0f, VolumeDb = -3.0f, MaxDistance = 40.0f, Bus = "Movement" };
		AddChild(_footstepPlayer);
		_voicePlayer = new AudioStreamPlayer3D { Name = "Voice", UnitSize = 14.0f, VolumeDb = HitVolumeDb, Bus = "Voice" };
		AddChild(_voicePlayer);

		// The struggle is meant for whoever is right next to the dying player, so it has a short
		// unit size and a hard cutoff. It gets its own player so a hit or respawn sound on the
		// shared Voice player can never cut it off partway through.
		_deathPlayer = new AudioStreamPlayer3D { Name = "DeathStruggle", UnitSize = 6.0f, VolumeDb = StruggleVolumeDb, MaxDistance = 25.0f, Bus = "Voice" };
		AddChild(_deathPlayer);

		// A shout for help carries further than a struggle - but it is still a positional sound.
		_medicPlayer = new AudioStreamPlayer3D { Name = "MedicCall", UnitSize = 10.0f, VolumeDb = MedicVolumeDb, MaxDistance = 50.0f, Bus = "Voice" };
		AddChild(_medicPlayer);

		// Jump/crouch get their own player rather than sharing Footsteps or Voice: both of those
		// already restart on a schedule this script doesn't control, and stealing either one's
		// node would cut a sound off partway through instead of just layering another one in.
		_actionPlayer = new AudioStreamPlayer3D { Name = "Action", UnitSize = 7.0f, MaxDistance = 40.0f, Bus = "Movement" };
		AddChild(_actionPlayer);

		// The walk / run steps. Restarted by hand in UpdateLocomotionLoop rather than through the
		// .wav's own loop flag, because the walk clip needs a custom loop point (see WalkLoopSeconds).
		_locomotionLoopPlayer = new AudioStreamPlayer3D { Name = "LocomotionLoop", UnitSize = 7.0f, MaxDistance = 40.0f, Bus = "Movement" };
		AddChild(_locomotionLoopPlayer);

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

		if (_walkLoop == null && _sprintLoop == null && _footsteps.Length == 0)
			GD.PushWarning($"No walk/sprint loops or footstep clips found ({MovementDirectory}, {FootstepDirectory}).");
	}

	public override void _ExitTree()
	{
		if (_killFeed != null && IsInstanceValid(_killFeed)) _killFeed.KillAnnounced -= OnKillAnnounced;
	}

	public override void _PhysicsProcess(double delta)
	{
		UpdateHealthSounds();
		UpdateBleedOut((float)delta);
		UpdateFootsteps((float)delta);
		UpdateMovementStateSounds();
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

		// An instant death is silent. Nothing plays, for anyone.
		if (!struggles) return;

		if (!IsTeamMatch())
		{
			// Deathmatch: no medic to call, so the dying player just struggles, right away.
			PlayDeathStruggle();
			return;
		}

		// Team Deathmatch: medic calls for Health.TeamBleedOutSeconds (UpdateBleedOut), with the
		// struggle either at the start or at the end of that window.
		_bleedingOut = true;
		_bleedOutRemaining = Health.TeamBleedOutSeconds;
		_bleedOutElapsed = 0.0f;
		_bleedOutSawDead = false;
		_medicTimer = 0.0f;
		if (!StruggleWhenBleedOutEnds) PlayDeathStruggle();
	}

	private void UpdateBleedOut(float delta)
	{
		if (!_bleedingOut) return;

		// Stop if the player respawns early. IsDead can lag a moment behind KillAnnounced on a
		// remote peer (separate replication path), so only treat "alive" as a respawn once we have
		// actually seen them dead - or after a couple of seconds without ever seeing it.
		_bleedOutElapsed += delta;
		if (_health != null)
		{
			if (_health.IsDead) _bleedOutSawDead = true;
			else if (_bleedOutSawDead || _bleedOutElapsed > 2.0f)
			{
				CancelDeathAudio();
				return;
			}
		}

		_bleedOutRemaining -= delta;
		if (_bleedOutRemaining <= 0.0f)
		{
			// Out of time: eliminated.
			_bleedingOut = false;
			if (_medicPlayer != null) _medicPlayer.Stop();
			if (StruggleWhenBleedOutEnds) PlayDeathStruggle();
			return;
		}

		_medicTimer -= delta;
		if (_medicTimer > 0.0f) return;
		_medicTimer = MedicRepeatSeconds;
		// A medic call is for the dying player's own team.
		if (ListenerIsOnMyTeam()) PlayMedicCall();
	}

	private void CancelDeathAudio()
	{
		_bleedingOut = false;
		if (_medicPlayer != null && _medicPlayer.Playing) _medicPlayer.Stop();
		if (_deathPlayer != null && _deathPlayer.Playing) _deathPlayer.Stop();
	}

	private static bool IsTeamMatch() =>
		MatchManager.Instance != null && MatchManager.Instance.IsTeamMode;

	/// <summary>Is the player hearing this (this device's own player) on the same team as the one
	/// calling for a medic? The caller always hears their own call.</summary>
	private bool ListenerIsOnMyTeam()
	{
		if (PlayerIdentity.IsLocalHuman(_movement)) return true;
		MatchManager? match = MatchManager.Instance;
		if (match == null) return false;
		return match.AreTeammates(Multiplayer.GetUniqueId(), PlayerIdentity.IdOf(_movement));
	}

	private void PlayDeathStruggle()
	{
		if (_struggle == null || _deathPlayer == null) return;
		_deathPlayer.Stream = _struggle;
		_deathPlayer.PitchScale = _rng.RandfRange(0.96f, 1.04f);
		_deathPlayer.Play();
	}

	private void PlayMedicCall()
	{
		if (_medic == null || _medicPlayer == null) return;
		_medicPlayer.Stream = _medic;
		_medicPlayer.PitchScale = _rng.RandfRange(0.97f, 1.03f);
		_medicPlayer.Play();
	}

	// -------------------------------------------------------------------------------------------
	// Movement
	// -------------------------------------------------------------------------------------------

	/// <summary>Only used when the new walk/sprint loops are missing - otherwise the loops ARE the
	/// footsteps and this stays out of the way so steps are not doubled up.</summary>
	private void UpdateFootsteps(float delta)
	{
		if (_walkLoop != null || _sprintLoop != null) return;
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
		// Idle, Crouch, Slide, Jump, Fall and Aim are all silent.
		_ => 0.0f,
	};

	/// <summary>
	/// Jump take-off, crouch settle, and the walk/run steps, all driven by watching CurrentState()'s
	/// transitions rather than by signals - the same reason UpdateHealthSounds polls CurrentHealth
	/// instead of Health's Died/Respawned signals: PlayerMovement's real input handling only ever
	/// runs on the owning peer, so a signal raised there would never reach a remote observer's copy
	/// of this node. ReplicatedMovementState is what actually crosses the network, so reading it
	/// here is what lets every peer hear it.
	/// </summary>
	private void UpdateMovementStateSounds()
	{
		var state = CurrentState();
		if (_health == null || !_health.IsDead)
		{
			if (state == PlayerMovement.MovementState.Jump && _previousState != PlayerMovement.MovementState.Jump)
				PlayAction(_jump, JumpVolumeDb);

			bool enteringCrouch = state is PlayerMovement.MovementState.Crouch or PlayerMovement.MovementState.CrouchWalk;
			bool wasCrouching = _previousState is PlayerMovement.MovementState.Crouch or PlayerMovement.MovementState.CrouchWalk;
			if (enteringCrouch && !wasCrouching)
				PlayAction(_crouch, CrouchVolumeDb);
		}

		UpdateLocomotionLoop(state);
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

	/// <summary>Walk / crouch-walk / sprint steps. Walk and crouch-walk share the walk clip
	/// (crouch-walk is quieter and slower); sprint has its own. Any other state stops the sound.</summary>
	private void UpdateLocomotionLoop(PlayerMovement.MovementState state)
	{
		if (_locomotionLoopPlayer == null) return;
		if (_health != null && _health.IsDead)
		{
			StopLocomotion();
			return;
		}

		AudioStream? desired = null;
		float volumeDb = 0.0f;
		float pitch = 1.0f;
		float loopSeconds = 0.0f;
		switch (state)
		{
			case PlayerMovement.MovementState.Walk:
				desired = _walkLoop;
				volumeDb = WalkVolumeDb;
				loopSeconds = WalkLoopSeconds;
				break;
			case PlayerMovement.MovementState.CrouchWalk:
				desired = _walkLoop;
				volumeDb = CrouchWalkVolumeDb;
				pitch = CrouchWalkPitch;
				loopSeconds = WalkLoopSeconds;
				break;
			case PlayerMovement.MovementState.Sprint:
				desired = _sprintLoop;
				volumeDb = SprintVolumeDb;
				loopSeconds = SprintLoopSeconds;
				break;
		}

		if (desired == null)
		{
			StopLocomotion();
			return;
		}

		if (_loopState != state)
		{
			// New state (or first frame moving): switch clip/level/pitch and start from the top.
			_loopState = state;
			_locomotionLoopPlayer.Stream = desired;
			_locomotionLoopPlayer.VolumeDb = volumeDb;
			_locomotionLoopPlayer.PitchScale = pitch;
			_locomotionLoopPlayer.Play();
			return;
		}

		// Same state: keep it going. Restart when the clip has finished, or - for the walk clip,
		// which ends in silence - once playback reaches its loop point. GetPlaybackPosition is in
		// stream time, so this is unaffected by the crouch-walk pitch.
		if (!_locomotionLoopPlayer.Playing)
			_locomotionLoopPlayer.Play();
		else if (loopSeconds > 0.0f && _locomotionLoopPlayer.GetPlaybackPosition() >= loopSeconds)
			_locomotionLoopPlayer.Play();
	}

	private void StopLocomotion()
	{
		_loopState = null;
		if (_locomotionLoopPlayer != null && _locomotionLoopPlayer.Playing) _locomotionLoopPlayer.Stop();
	}

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
		if (_movement.IsMultiplayerAuthority()) return _movement.State;
		return System.Enum.TryParse(_movement.ReplicatedMovementState,
			out PlayerMovement.MovementState remote)
			? remote
			: PlayerMovement.MovementState.Idle;
	}

	// -------------------------------------------------------------------------------------------
	// Loading
	// -------------------------------------------------------------------------------------------

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

	/// <summary>Exists-check first, so an optional clip that is not in the project (the old
	/// respawn sound, the old footstep set) is a quiet null instead of an engine error.</summary>
	private static AudioStream? Load(string path) =>
		ResourceLoader.Exists(path) ? ResourceLoader.Load<AudioStream>(path) : null;
}
