using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The "player" behind a bot. It is a child of a normal Player scene, runs only on the host, and does
/// exactly what a person does: it looks (vision cone + line-of-sight rays), listens (footsteps and
/// gunshots), remembers, decides, then presses virtual keys (BotControls) and moves a virtual mouse
/// (BotAim). It has no access to anything a person couldn't perceive: no wall-hacks, no perfect aim.
///
/// The class is split over three files:
///   BotBrain.cs           lifecycle + senses (this file)
///   BotBrain.Behavior.cs  what to do: roam / investigate / engage / hunt, tactics, movement
///   BotBrain.Combat.cs    aiming, trigger discipline, reloading, weapon handling
/// </summary>
public partial class BotBrain : Node
{
	private const float PerceiveInterval = 0.05f;
	private const float EyeLosHeight = 1.5f;

	private enum Mode { Roam, Investigate, Engage, Hunt, Idle }

	private sealed class Track
	{
		public CharacterBody3D Body = null!;
		public Node3D? HeadNode;
		public long Id;
		public bool Visible;
		public bool Acquired;
		public float VisibleFor;
		public float NeedReact;
		public float LastSeen = -999.0f;
		public Vector3 LastPos;
		public Vector3 LastHead;
		public Vector3 Vel;
		public float NextHear;
		public readonly List<HistSample> Hist = new List<HistSample>();
	}

	private struct HistSample
	{
		public float T;
		public Vector3 Head;
	}

	public BotControls Controls { get; } = new BotControls();
	public long BotId { get; private set; }
	public BotDifficulty Level { get; private set; } = BotDifficulty.Normal;

	private BotProfile _p = BotProfile.For(BotDifficulty.Normal);
	private readonly RandomNumberGenerator _rng = new RandomNumberGenerator();
	private BotAim _aim = null!;
	private PlayerMovement _body = null!;
	private Health _health = null!;
	private WeaponSwitcher _weapon = null!;
	private Camera3D _camera = null!;
	private KillFeed? _feed;
	private Godot.Collections.Array<Rid> _selfExclude = new Godot.Collections.Array<Rid>();

	private float _now;
	private float _perceiveTimer;
	private float _horizSpeed;
	private Mode _mode = Mode.Roam;
	private float _stateTime;

	private readonly Dictionary<long, Track> _tracks = new Dictionary<long, Track>();
	private Track? _target;
	private readonly List<Vector3> _otherPositions = new List<Vector3>();

	// suspicion (heard something / got shot / was told)
	private bool _hasSuspect;
	private Vector3 _suspectPos;
	private float _suspectTime;
	private float _nextGunshotReact;
	private float _freezeUntil;
	private float _alertUntil;
	private float _lastFiredTime = -99.0f;
	private float _lastEngageTime = -99.0f;

	public void Configure(long id, BotDifficulty level)
	{
		BotId = id;
		Level = level == BotDifficulty.Mixed ? BotDifficulty.Normal : level;
		_p = BotProfile.For(Level);
	}

	public override void _Ready()
	{
		_rng.Randomize();
		_aim = new BotAim(_rng);
		// Run before the Player node's own physics step so this frame's decisions are applied this frame.
		ProcessPhysicsPriority = -10;

		_body = GetParent<PlayerMovement>();
		_health = _body.GetNode<Health>("Health");
		_weapon = _body.GetNode<WeaponSwitcher>("WeaponSwitcher");
		_camera = _body.GetNode<Camera3D>("Head/FirstPersonCamera");
		_selfExclude = new Godot.Collections.Array<Rid> { _body.GetRid() };

		_health.Died += OnDied;
		_health.Respawned += OnRespawned;
		_health.Damaged += OnDamaged;
		_feed = KillFeed.From(this);
		if (_feed != null) _feed.KillAnnounced += OnKillAnnounced;
		BotSenses.Register(this);

		_perceiveTimer = _rng.RandfRange(0.0f, PerceiveInterval);
		InitBehavior();
		PickLoadout();
	}

	public override void _ExitTree()
	{
		BotSenses.Unregister(this);
		if (_health != null && IsInstanceValid(_health))
		{
			_health.Died -= OnDied;
			_health.Respawned -= OnRespawned;
			_health.Damaged -= OnDamaged;
		}
		if (_feed != null && IsInstanceValid(_feed)) _feed.KillAnnounced -= OnKillAnnounced;
	}

	public override void _PhysicsProcess(double delta)
	{
		float dt = (float)delta;
		if (_health.IsDead)
		{
			Controls.Reset();
			return;
		}
		if (BotNav.Grid == null) return;

		_now += dt;
		_horizSpeed = new Vector2(_body.Velocity.X, _body.Velocity.Z).Length();

		_perceiveTimer -= dt;
		if (_perceiveTimer <= 0.0f)
		{
			_perceiveTimer += PerceiveInterval;
			Perceive(PerceiveInterval);
		}
		RecordHistory(dt);
		UpdatePositionNoise(dt);

		Think(dt);
		Act(dt);
	}

	// ------------------------------------------------------------------------------------------------
	// Identity / teams
	// ------------------------------------------------------------------------------------------------

	private static float Ease => Mathf.Clamp(BotSenses.Ease, -0.15f, 0.35f);

	private bool IsEnemy(long id)
	{
		if (id == BotId || id == 0) return false;
		MatchManager? m = MatchManager.Instance;
		if (m == null) return true;
		return !m.AreTeammates(BotId, id);
	}

	private static bool IsTeamMode()
	{
		MatchManager? m = MatchManager.Instance;
		return m != null && m.IsTeamMode;
	}

	private Node3D? FindPlayer(long id)
	{
		foreach (Node n in GetTree().GetNodesInGroup("players"))
			if (n is Node3D p && PlayerIdentity.IdOf(p) == id) return p;
		return null;
	}

	private static float Flat(Vector3 a, Vector3 b)
	{
		float dx = a.X - b.X, dz = a.Z - b.Z;
		return Mathf.Sqrt(dx * dx + dz * dz);
	}

	private Vector3 NoiseVec(float distance)
	{
		float err = _p.SoundError * (distance / 10.0f);
		return new Vector3(_rng.Randfn(0.0f, err), 0.0f, _rng.Randfn(0.0f, err));
	}

	// ------------------------------------------------------------------------------------------------
	// Vision
	// ------------------------------------------------------------------------------------------------

	private Vector3 HeadPoint(Track t) =>
		t.HeadNode != null ? t.HeadNode.GlobalPosition : t.Body.GlobalPosition + new Vector3(0.0f, 1.5f, 0.0f);

	private void Perceive(float elapsed)
	{
		NavGrid? grid = BotNav.Grid;
		if (grid == null) return;
		Vector3 eye = _camera.GlobalPosition;
		Vector3 fwd = -_camera.GlobalTransform.Basis.Z;
		float halfFov = Mathf.DegToRad(_p.VisionHalfAngleDeg);

		_otherPositions.Clear();
		foreach (Node n in GetTree().GetNodesInGroup("players"))
		{
			if (n == _body || n is not CharacterBody3D other) continue;
			_otherPositions.Add(other.GlobalPosition);

			long id = PlayerIdentity.IdOf(other);
			if (!IsEnemy(id))
			{
				_tracks.Remove(id);
				continue;
			}

			Health? hp = other.GetNodeOrNull<Health>("Health");
			if (hp == null || hp.IsDead)
			{
				if (_tracks.TryGetValue(id, out Track? dead))
				{
					if (_target == dead) _target = null;
					_tracks.Remove(id);
				}
				continue;
			}

			if (!_tracks.TryGetValue(id, out Track? t))
			{
				t = new Track { Body = other, Id = id, HeadNode = other.GetNodeOrNull<Node3D>("Head") };
				_tracks[id] = t;
			}

			bool seen = TestVisible(t, eye, fwd, halfFov, out float recognition);
			UpdateVisibility(t, seen, recognition, elapsed);
			if (!t.Visible) ListenForFootsteps(t, grid, elapsed);
		}
	}

	private bool TestVisible(Track t, Vector3 eye, Vector3 fwd, float halfFov, out float recognition)
	{
		recognition = 0.0f;
		Vector3 head = HeadPoint(t);
		Vector3 toHead = head - eye;
		float dist = toHead.Length();
		if (dist > _p.VisionRange || dist < 0.1f) return false;

		float cos = Mathf.Clamp(fwd.Dot(toHead / dist), -1.0f, 1.0f);
		float angle = Mathf.Acos(cos);
		if (angle > halfFov) return false;

		recognition = angle <= Mathf.DegToRad(30.0f) ? 1.0f : _p.PeripheralGain;
		if (recognition <= 0.0f) return false;

		if (RayReaches(eye, head, t.Body)) return true;
		if (RayReaches(eye, head - new Vector3(0.0f, 0.5f, 0.0f), t.Body)) return true;
		return RayReaches(eye, t.Body.GlobalPosition + new Vector3(0.0f, 0.25f, 0.0f), t.Body);
	}

	private bool RayReaches(Vector3 from, Vector3 to, CharacterBody3D target)
	{
		var query = PhysicsRayQueryParameters3D.Create(from, to, 0xFFFFFFFF, _selfExclude);
		var hit = _body.GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (hit.Count == 0) return true;
		return hit["collider"].As<Node>() == target;
	}

	private float NextReaction(Track t)
	{
		float react = _rng.RandfRange(_p.ReactMin, _p.ReactMax) * (1.0f + Ease);
		float dist = Flat(t.Body.GlobalPosition, _body.GlobalPosition);
		react *= 0.7f + 0.5f * Mathf.Clamp(dist / _p.VisionRange, 0.0f, 1.0f);

		PlayerMovement.MovementState s = StateOf(t.Body);
		if (s == PlayerMovement.MovementState.Crouch || s == PlayerMovement.MovementState.CrouchWalk) react *= 1.2f;
		else if (s == PlayerMovement.MovementState.Sprint) react *= 0.85f;

		if (_now < _alertUntil) react *= 0.4f;          // already on edge (just got shot at)
		if (_mode == Mode.Idle) react *= 1.6f;           // zoned out
		if (_mode == Mode.Investigate || _mode == Mode.Hunt) react *= 0.75f; // expecting someone
		return react;
	}

	private void UpdateVisibility(Track t, bool seen, float recognition, float elapsed)
	{
		if (seen)
		{
			if (!t.Visible)
			{
				t.NeedReact = NextReaction(t);
				t.Hist.Clear();
				t.Vel = Vector3.Zero;
			}
			t.Visible = true;
			t.LastSeen = _now;
			t.LastPos = t.Body.GlobalPosition;
			t.LastHead = HeadPoint(t);
			t.VisibleFor += elapsed * recognition;
			if (!t.Acquired && t.VisibleFor >= t.NeedReact)
			{
				t.Acquired = true;
				OnAcquired(t);
			}
		}
		else
		{
			t.Visible = false;
			t.VisibleFor = Mathf.Max(0.0f, t.VisibleFor - elapsed * 2.0f);
			float memory = _p.MemorySeconds * (1.0f - 0.5f * Mathf.Clamp(Ease, 0.0f, 0.35f));
			if (t.Acquired && _now - t.LastSeen > memory)
			{
				t.Acquired = false;
				if (_target == t) _target = null;
			}
		}
	}

	private void RecordHistory(float dt)
	{
		foreach (Track t in _tracks.Values)
		{
			if (!t.Visible) continue;
			Vector3 head = HeadPoint(t);
			t.LastHead = head;
			t.LastPos = t.Body.GlobalPosition;
			if (t.Hist.Count > 0)
			{
				Vector3 v = (head - t.Hist[t.Hist.Count - 1].Head) / Mathf.Max(dt, 0.0001f);
				v.Y *= 0.3f;
				t.Vel = t.Vel.Lerp(v, 0.25f);
			}
			t.Hist.Add(new HistSample { T = _now, Head = head });
			while (t.Hist.Count > 1 && t.Hist[0].T < _now - 1.0f) t.Hist.RemoveAt(0);
		}
	}

	private static Vector3 SampleHistory(Track t, float time)
	{
		if (t.Hist.Count == 0) return t.LastHead;
		if (time <= t.Hist[0].T) return t.Hist[0].Head;
		for (int i = 1; i < t.Hist.Count; i++)
		{
			if (t.Hist[i].T >= time)
			{
				float span = Mathf.Max(t.Hist[i].T - t.Hist[i - 1].T, 0.0001f);
				float k = Mathf.Clamp((time - t.Hist[i - 1].T) / span, 0.0f, 1.0f);
				return t.Hist[i - 1].Head.Lerp(t.Hist[i].Head, k);
			}
		}
		return t.Hist[t.Hist.Count - 1].Head;
	}

	// ------------------------------------------------------------------------------------------------
	// Hearing, being shot, being told
	// ------------------------------------------------------------------------------------------------

	private static PlayerMovement.MovementState StateOf(CharacterBody3D other)
	{
		if (other is not PlayerMovement pm) return PlayerMovement.MovementState.Idle;
		if (pm.IsMultiplayerAuthority()) return pm.State;
		PlayerMovement.MovementState parsed;
		if (!Enum.TryParse(pm.ReplicatedMovementState, out parsed)) parsed = PlayerMovement.MovementState.Idle;
		return parsed;
	}

	/// <summary>How far away (metres) a player in this movement state can be heard by a perfect listener.</summary>
	private static float FootstepRadius(PlayerMovement.MovementState s)
	{
		switch (s)
		{
			case PlayerMovement.MovementState.Sprint: return 16.0f;
			case PlayerMovement.MovementState.Slide: return 12.0f;
			case PlayerMovement.MovementState.Walk: return 8.5f;
			case PlayerMovement.MovementState.Jump: return 5.0f;
			case PlayerMovement.MovementState.Fall: return 5.0f;
			case PlayerMovement.MovementState.CrouchWalk: return 2.5f;
			default: return 0.0f;
		}
	}

	private void ListenForFootsteps(Track t, NavGrid grid, float elapsed)
	{
		float loud = FootstepRadius(StateOf(t.Body));
		if (loud <= 0.0f || _now < t.NextHear) return;

		float radius = loud * _p.HearingScale;
		if (_horizSpeed > 5.5f) radius *= 0.6f;                 // own footsteps mask theirs
		if (_now - _lastFiredTime < 0.8f) radius *= 0.4f;       // own gunfire deafens

		Vector3 me = _body.GlobalPosition;
		Vector3 pos = t.Body.GlobalPosition;
		float dist = Flat(me, pos);
		if (dist > radius) return;
		if (!grid.HasLos(me, EyeLosHeight, pos, EyeLosHeight) && dist > radius * 0.55f) return; // muffled by walls

		t.NextHear = _now + 0.9f + _rng.RandfRange(0.0f, 0.6f);
		if (_rng.Randf() > _p.HearingReliability) return;
		Suspect(pos + NoiseVec(dist));
	}

	public void HearGunshot(Vector3 position, float loudness, Node shooter)
	{
		if (_health == null || _health.IsDead || shooter == _body) return;
		if (shooter is not CharacterBody3D other) return;
		if (!IsEnemy(PlayerIdentity.IdOf(other))) return;
		if (_mode == Mode.Engage && _target != null && _target.Visible) return;
		if (_now < _nextGunshotReact) return;

		float dist = Flat(_body.GlobalPosition, position);
		float radius = loudness * _p.HearingScale;
		if (_now - _lastFiredTime < 0.8f) radius *= 0.5f;
		if (dist > radius) return;

		_nextGunshotReact = _now + 0.6f;
		if (_rng.Randf() > Mathf.Min(1.0f, _p.HearingReliability + 0.15f)) return;
		Suspect(position + NoiseVec(dist));
	}

	public void ReceiveCallout(BotBrain from, Vector3 position, float certainty)
	{
		if (_health == null || _health.IsDead || Level == BotDifficulty.Easy) return;
		if (!IsTeamMode() || IsEnemy(from.BotId)) return;
		if (_mode == Mode.Engage && _target != null && _target.Visible) return;
		if (Flat(_body.GlobalPosition, position) > 60.0f) return;
		Suspect(position + NoiseVec(20.0f) * 0.5f);
	}

	private void Suspect(Vector3 position)
	{
		_hasSuspect = true;
		_suspectPos = position;
		_suspectTime = _now;
		if (_mode == Mode.Roam || _mode == Mode.Idle) BeginInvestigate();
		else if (_mode == Mode.Investigate) BeginInvestigate();
	}

	private void OnDamaged(float amount, long attackerId)
	{
		if (!IsEnemy(attackerId)) return;
		_alertUntil = _now + 4.0f;
		_lastHurtTime = _now;
		_freezeUntil = Mathf.Max(_freezeUntil, _now + _rng.RandfRange(_p.HitFreezeMin, _p.HitFreezeMax));

		Node3D? attacker = FindPlayer(attackerId);
		if (attacker == null) return;
		float dist = Flat(_body.GlobalPosition, attacker.GlobalPosition);
		Vector3 guess = attacker.GlobalPosition + NoiseVec(dist) * 0.7f;
		if (_mode != Mode.Engage || _target == null || !_target.Visible) Suspect(guess);

		if (_tracks.TryGetValue(attackerId, out Track? t) && !t.Acquired)
			t.NeedReact = Mathf.Min(t.NeedReact, 0.15f); // he has my attention: recognise him fast
	}

	private void OnKillAnnounced(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
		int totalPoints, long assistPeerId, int assistPoints, bool struggles)
	{
		if (_health == null || _health.IsDead || _p.KillInference <= 0.0f || !IsTeamMode()) return;
		if (victimPeerId == BotId || IsEnemy(victimPeerId) || !IsEnemy(killerPeerId)) return;
		Node3D? killer = FindPlayer(killerPeerId);
		if (killer == null) return;
		if (Flat(_body.GlobalPosition, killer.GlobalPosition) > 45.0f) return;
		if (_mode == Mode.Engage && _target != null && _target.Visible) return;
		var err = new Vector3(_rng.Randfn(0.0f, _p.KillInference), 0.0f, _rng.Randfn(0.0f, _p.KillInference));
		Suspect(killer.GlobalPosition + err);
	}

	private void OnAcquired(Track t)
	{
		_lastEngageTime = _now;
		if (IsTeamMode() && _rng.Randf() < _p.CalloutChance)
			BotSenses.ReportCallout(this, t.LastPos, 0.8f);
	}

	// ------------------------------------------------------------------------------------------------
	// Death / respawn
	// ------------------------------------------------------------------------------------------------

	private void OnDied()
	{
		Controls.Reset();
		_tracks.Clear();
		_target = null;
		_hasSuspect = false;
		_burstLeft = 0;
		_mode = Mode.Roam;
		_aim.Reset();
	}

	private void OnRespawned()
	{
		Controls.Reset();
		_tracks.Clear();
		_target = null;
		_hasSuspect = false;
		_burstLeft = 0;
		_freezeUntil = 0.0f;
		_alertUntil = 0.0f;
		_aim.Reset();

		NavGrid? grid = BotNav.Grid;
		if (grid != null)
		{
			Vector3 pos = _body.GlobalPosition;
			bool offNav = !grid.IsWalkable(pos) || Mathf.Abs(pos.Y - grid.GroundY(pos)) > 1.2f;
			if (offNav && grid.TryNearest(pos, out Vector3 safe))
				_body.GlobalPosition = new Vector3(safe.X, safe.Y + 0.1f, safe.Z);
		}

		PickLoadout();
		_weapon.RestockAmmo();
		BeginRoam();
	}
}
