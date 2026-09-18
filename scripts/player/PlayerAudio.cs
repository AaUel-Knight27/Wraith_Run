using Godot;

/// <summary>
/// Everything the player's body makes noise about: footsteps, landings, taking a hit, dying and
/// respawning. All of it 3D and positional, and all of it running on EVERY peer rather than just
/// the owner - hearing someone sprint up behind you is the whole point.
///
/// Remote copies read ReplicatedMovementState (a synchronized string) rather than PlayerMovement's
/// local State, which is only ever written on the authority. The authority writes both every
/// frame, so one code path covers both cases.
///
/// The nine Player_Footstep clips and the Hit/Dead/Respawn clips were already sitting in
/// res://audio unused; this is what plays them.
/// </summary>
public partial class PlayerAudio : Node3D
{
	private const string FootstepDirectory = "res://audio/Clips/Player/Footsteps And Landing";
	private const string HitSound = "res://audio/Clips/Player/Hit Dead And Respawn/PlayerHit.wav";
	private const string DeadSound = "res://audio/Clips/Player/Hit Dead And Respawn/PlayerDead.wav";
	private const string RespawnSound = "res://audio/Clips/Player/Hit Dead And Respawn/PlayerRespawn.wav";

	// Stride intervals in seconds. Sprint is not simply "walk but faster" - the sprint clip has a
	// longer stride, so the cadence is tuned per state rather than derived from speed.
	private const float WalkStepSeconds = 0.48f;
	private const float SprintStepSeconds = 0.34f;
	private const float CrouchStepSeconds = 0.72f;

	private PlayerMovement _movement = null!;
	private Health? _health;
	private AudioStreamPlayer3D _footstepPlayer = null!;
	private AudioStreamPlayer3D? _voicePlayer;
	private AudioStream[] _footsteps = System.Array.Empty<AudioStream>();
	private AudioStream? _hit;
	private AudioStream? _dead;
	private AudioStream? _respawn;
	private readonly RandomNumberGenerator _rng = new();

	private float _stepTimer;
	private float _lastKnownHealth = -1.0f;
	private bool _wasDead;

	public override void _Ready()
	{
		_rng.Randomize();
		_movement = (PlayerMovement)GetParent();
		_health = _movement.GetNodeOrNull<Health>("Health");

		_footsteps = LoadFootsteps();
		_hit = Load(HitSound);
		_dead = Load(DeadSound);
		_respawn = Load(RespawnSound);

		// Footsteps sit low and carry a short distance; the hit/death voice carries further because
		// it is a positional cue about a fight, not a detail of someone's gait.
		_footstepPlayer = new AudioStreamPlayer3D { Name = "Footsteps", UnitSize = 7.0f, VolumeDb = -3.0f };
		AddChild(_footstepPlayer);
		_voicePlayer = new AudioStreamPlayer3D { Name = "Voice", UnitSize = 14.0f };
		AddChild(_voicePlayer);

		if (_footsteps.Length == 0)
			GD.PushWarning($"No footstep clips found under {FootstepDirectory}.");
	}

	public override void _PhysicsProcess(double delta)
	{
		UpdateHealthSounds();
		UpdateFootsteps((float)delta);
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
	/// Hit / death / respawn are driven by watching CurrentHealth rather than by signals, because
	/// Health only emits Respawned on the owning peer. CurrentHealth is replicated, so polling it
	/// gives every peer the same three transitions at roughly the same moment.
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
		if (isDead && !_wasDead) PlayVoice(_dead);
		else if (!isDead && _wasDead) PlayVoice(_respawn);
		else if (!isDead && current < _lastKnownHealth - 0.01f) PlayVoice(_hit);

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

	private static AudioStream? Load(string path) => ResourceLoader.Load<AudioStream>(path);
}
