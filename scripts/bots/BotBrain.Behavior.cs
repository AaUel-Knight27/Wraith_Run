using Godot;
using System;
using System.Collections.Generic;

public partial class BotBrain
{
	private enum Tactic { Strafe, Advance, Hold, Peek, Retreat }
	private enum PeekPhase { Hidden, Exposed }

	// movement output for this frame (set by the Act* methods, applied in ApplyMovement)
	private Vector3 _moveWorld;
	private bool _wantSprint;
	private bool _wantCrouch;
	private bool _adsWanted;
	private bool _inCover;

	// where the crosshair should point this frame
	private bool _aimHasTarget;
	private bool _aimEngaged;
	private Vector3 _aimPoint;
	private Vector3 _lookPoint;

	// path following
	private readonly List<Vector3> _path = new List<Vector3>();
	private int _pathIdx;
	private Vector3 _pathGoal;
	private bool _pathOk;
	private float _repathTimer;
	private Vector3 _destination;
	private bool _hasDestination;

	// roaming
	private bool _legSprint;
	private float _roamPauseTimer;
	private float _afkCheckTimer = 4.0f;
	private float _idleUntil;
	private int _idleStyle;
	private float _glanceTimer;
	private float _glanceUntil;
	private float _glanceAngle;
	private float _sweep;
	private readonly float[] _sectorVisited = new float[36];

	// investigate / hunt
	private bool _holding;
	private float _holdTimer;
	private bool _holdCrouch;
	private bool _hasHoldSpot;
	private Vector3 _holdSpot;

	// engage
	private Tactic _tactic = Tactic.Strafe;
	private float _tacticTimer;
	private float _strafeSign = 1.0f;
	private float _strafeTimer;
	private bool _advanceSprint;
	private bool _hasCover;
	private Vector3 _coverPos;
	private Vector3 _peekPos;
	private PeekPhase _peekPhase = PeekPhase.Hidden;
	private float _peekTimer;
	private int _peekCount;
	private float _counterStop;
	private float _lastHurtTime = -99.0f;
	private int _visibleCount;

	// aim point choice + slow drift in where the bot thinks the enemy is
	private bool _aimHead;
	private float _aimHeadTimer;
	private Vector3 _posNoise;

	// stuck recovery
	private Vector3 _stuckRef;
	private float _stuckTimer;
	private float _stuckCheck;
	private float _unstuckUntil;
	private Vector3 _unstuckDir;

	private void InitBehavior()
	{
		for (int i = 0; i < _sectorVisited.Length; i++) _sectorVisited[i] = -100.0f;
		_stuckRef = _body.GlobalPosition;
		_lookPoint = _camera.GlobalPosition + (_body.GlobalTransform.Basis * Vector3.Forward) * 10.0f;
		BeginRoam();
	}

	// ------------------------------------------------------------------------------------------------
	// Decision layer
	// ------------------------------------------------------------------------------------------------

	private void Think(float dt)
	{
		_stateTime += dt;

		Track? best = null;
		float bestScore = float.MaxValue;
		int visible = 0;
		Vector3 me = _body.GlobalPosition;
		foreach (Track t in _tracks.Values)
		{
			if (!t.Acquired) continue;
			if (t.Visible) visible++;
			float s = Flat(t.LastPos, me) + (t.Visible ? 0.0f : 12.0f);
			if (t == _target) s -= 4.0f;
			if (s < bestScore) { bestScore = s; best = t; }
		}
		_visibleCount = visible;
		if (best != _target)
		{
			_target = best;
			if (best != null)
			{
				_aimHead = _rng.Randf() < _p.HeadAimChance;
				_aimHeadTimer = _rng.RandfRange(1.5f, 3.0f);
			}
		}

		ManageWeapon(dt);

		if (_target != null)
		{
			if (_target.Visible)
			{
				_lastEngageTime = _now;
				if (_mode != Mode.Engage) EnterEngage();
			}
			else if (_mode == Mode.Engage)
			{
				float lose = Level == BotDifficulty.Easy ? 0.25f : (Level == BotDifficulty.Normal ? 0.5f : 0.7f);
				bool peeking = _tactic == Tactic.Peek && _hasCover && _peekCount < 4;
				if (!peeking && _now - _target.LastSeen > lose) EnterHunt();
			}
			else if (_mode != Mode.Hunt)
			{
				EnterHunt();
			}
		}
		else if (_mode == Mode.Engage || _mode == Mode.Hunt)
		{
			if (_hasSuspect && _now - _suspectTime < 6.0f) BeginInvestigate();
			else BeginRoam();
		}
	}

	private void Act(float dt)
	{
		_moveWorld = Vector3.Zero;
		_wantSprint = false;
		_wantCrouch = false;
		_inCover = false;
		_aimHasTarget = false;
		_aimEngaged = false;

		switch (_mode)
		{
			case Mode.Roam: ActRoam(dt); break;
			case Mode.Investigate: ActInvestigate(dt); break;
			case Mode.Engage: ActEngage(dt); break;
			case Mode.Hunt: ActHunt(dt); break;
			case Mode.Idle: ActIdle(dt); break;
		}

		UpdateStuck(dt);
		ApplyMovement(dt);
		AimAndFire(dt);
	}

	// ------------------------------------------------------------------------------------------------
	// State entry
	// ------------------------------------------------------------------------------------------------

	private void BeginRoam()
	{
		_mode = Mode.Roam;
		_stateTime = 0.0f;
		_hasDestination = false;
		_pathOk = false;
		_roamPauseTimer = 0.0f;
		_holding = false;
	}

	private void BeginIdle()
	{
		_mode = Mode.Idle;
		_stateTime = 0.0f;
		_idleUntil = _now + _rng.RandfRange(3.0f, 8.0f);
		_idleStyle = _rng.RandiRange(0, 2);
	}

	private void BeginInvestigate()
	{
		_mode = Mode.Investigate;
		_stateTime = 0.0f;
		_holding = false;
		_hasHoldSpot = false;
		_pathOk = false;
		_holdCrouch = Level == BotDifficulty.Advanced && _rng.Randf() < 0.6f;
		if (Level == BotDifficulty.Advanced && FindHoldSpot(_suspectPos, out Vector3 spot))
		{
			_holdSpot = spot;
			_hasHoldSpot = true;
		}
	}

	private void EnterEngage()
	{
		_mode = Mode.Engage;
		_stateTime = 0.0f;
		_tacticTimer = 0.0f;
		_peekCount = 0;
		_hasCover = false;
		_holding = false;
		_burstLeft = 0;
		_adsWanted = Level != BotDifficulty.Easy && _rng.Randf() < (Level == BotDifficulty.Advanced ? 0.7f : 0.35f);
	}

	private void EnterHunt()
	{
		_mode = Mode.Hunt;
		_stateTime = 0.0f;
		_holding = false;
		_hasHoldSpot = false;
		_pathOk = false;
		if (_target != null) PickHuntGoal(PredictedPosition(_target));
	}

	// ------------------------------------------------------------------------------------------------
	// Roam / Idle / Investigate / Hunt
	// ------------------------------------------------------------------------------------------------

	private void ActIdle(float dt)
	{
		// zoned out: stands there, slowly looking around, sometimes crouched
		_wantCrouch = _idleStyle == 2;
		LookAround(dt, _idleStyle == 0 ? 0.25f : 0.6f);
		if (_now >= _idleUntil) BeginRoam();
	}

	private void ActRoam(float dt)
	{
		if (_p.AfkChance > 0.0f)
		{
			_afkCheckTimer -= dt;
			if (_afkCheckTimer <= 0.0f)
			{
				_afkCheckTimer = 4.0f;
				if (_rng.Randf() < _p.AfkChance) { BeginIdle(); return; }
			}
		}

		if (_roamPauseTimer > 0.0f)
		{
			_roamPauseTimer -= dt;
			_wantCrouch = _holdCrouch;
			LookAround(dt, 0.7f);
			return;
		}

		if (!_hasDestination) PickRoamDestination();

		bool arrived = StepAlongPath(dt, _destination, out Vector3 heading);
		_wantSprint = _legSprint;
		SetTravelLook(heading, dt);
		MarkSector();

		if (arrived)
		{
			_hasDestination = false;
			float pauseChance = Level == BotDifficulty.Advanced ? 0.45f : (Level == BotDifficulty.Normal ? 0.3f : 0.2f);
			if (_rng.Randf() < pauseChance)
			{
				_roamPauseTimer = _rng.RandfRange(1.0f, 3.0f);
				_holdCrouch = Level == BotDifficulty.Advanced && _rng.Randf() < 0.4f;
			}
		}
	}

	private void PickRoamDestination()
	{
		NavGrid grid = BotNav.Grid!;
		Vector3 me = _body.GlobalPosition;
		int samples = Level == BotDifficulty.Easy ? 1 : (Level == BotDifficulty.Normal ? 5 : 8);
		Vector3 best = me;
		float bestScore = float.MinValue;
		for (int i = 0; i < samples; i++)
		{
			Vector3 c = grid.RandomWalkable(_rng);
			float d = Flat(c, me);
			if (d < 10.0f && i < samples - 1) continue;
			float score = _rng.RandfRange(0.0f, 3.0f);
			if (Level != BotDifficulty.Easy)
			{
				score += grid.Enclosure(c, 3) * 6.0f * _p.CoverUse;
				float unseen = Mathf.Min(_now - _sectorVisited[grid.Sector(c, 6)], 60.0f);
				score += unseen * (Level == BotDifficulty.Advanced ? 0.08f : 0.03f);
				score -= Mathf.Abs(d - 22.0f) * 0.05f;
			}
			if (score > bestScore) { bestScore = score; best = c; }
		}
		_destination = best;
		_hasDestination = true;
		_pathOk = false;
		_legSprint = _rng.Randf() < _p.SprintLove;
		// a pro walks (quietly) when something dangerous was heard recently
		if (Level == BotDifficulty.Advanced && _now - _suspectTime < 20.0f) _legSprint = false;
	}

	private void MarkSector()
	{
		NavGrid? grid = BotNav.Grid;
		if (grid != null) _sectorVisited[grid.Sector(_body.GlobalPosition, 6)] = _now;
	}

	private void ActInvestigate(float dt)
	{
		if (!_hasSuspect || _now - _suspectTime > 14.0f) { BeginRoam(); return; }
		Vector3 me = _body.GlobalPosition;
		Vector3 eye = _camera.GlobalPosition;
		float dSus = Flat(me, _suspectPos);

		if (!_holding)
		{
			Vector3 goal = _hasHoldSpot ? _holdSpot : _suspectPos;
			bool arrived = StepAlongPath(dt, goal, out Vector3 heading);
			_wantSprint = Level == BotDifficulty.Easy || (Level == BotDifficulty.Normal ? dSus > 20.0f : dSus > 35.0f);
			if (Level != BotDifficulty.Easy && dSus < 30.0f)
				_lookPoint = new Vector3(_suspectPos.X, eye.Y - 0.05f, _suspectPos.Z); // pre-aim the noise
			else
				SetTravelLook(heading, dt);

			if (arrived || Flat(me, goal) < 1.0f)
			{
				_holding = true;
				_holdTimer = Level == BotDifficulty.Advanced ? _rng.RandfRange(1.8f, 3.5f)
					: (Level == BotDifficulty.Normal ? _rng.RandfRange(1.0f, 2.2f) : _rng.RandfRange(0.4f, 1.0f));
			}
		}
		else
		{
			_holdTimer -= dt;
			_wantCrouch = _holdCrouch;
			if (Level != BotDifficulty.Easy)
			{
				float sway = Mathf.Sin(_now * 1.3f) * 1.5f;
				_lookPoint = new Vector3(_suspectPos.X + sway, eye.Y - 0.05f, _suspectPos.Z);
			}
			else
			{
				LookAround(dt, 0.5f);
			}
			if (_holdTimer <= 0.0f)
			{
				_hasSuspect = false;
				BeginRoam();
			}
		}
	}

	private Vector3 PredictedPosition(Track t)
	{
		float since = Mathf.Min(_now - t.LastSeen, 1.2f);
		return t.LastPos + new Vector3(t.Vel.X, 0.0f, t.Vel.Z) * (since * 0.5f * _p.Lead);
	}

	private void PickHuntGoal(Vector3 predicted)
	{
		NavGrid grid = BotNav.Grid!;
		_hasHoldSpot = false;
		_holding = false;
		_pathOk = false;
		if (Level == BotDifficulty.Advanced)
		{
			if (_rng.Randf() < 0.4f && TryFlank(predicted, out Vector3 flank)) { _holdSpot = flank; _hasHoldSpot = true; }
			else if (FindHoldSpot(predicted, out Vector3 spot)) { _holdSpot = spot; _hasHoldSpot = true; }
		}
		else if (Level == BotDifficulty.Normal)
		{
			if (grid.RandomWalkableNear(predicted, 2.0f, 8.0f, _rng, out Vector3 near)) { _holdSpot = near; _hasHoldSpot = true; }
		}
	}

	private void ActHunt(float dt)
	{
		if (_target == null) { BeginRoam(); return; }
		Vector3 me = _body.GlobalPosition;
		Vector3 predicted = PredictedPosition(_target);

		// keep the crosshair where the enemy most likely is, at head height
		_aimHasTarget = true;
		_aimEngaged = Level != BotDifficulty.Easy;
		_aimPoint = predicted + new Vector3(0.0f, 1.45f, 0.0f);

		if (!_holding)
		{
			Vector3 goal = _hasHoldSpot ? _holdSpot : predicted;
			bool arrived = StepAlongPath(dt, goal, out Vector3 _);
			_wantSprint = Level == BotDifficulty.Normal && Flat(me, goal) > 25.0f;
			if (arrived || Flat(me, goal) < 1.2f)
			{
				_holding = true;
				_holdTimer = _rng.RandfRange(1.0f, 2.2f);
			}
		}
		else
		{
			_holdTimer -= dt;
			if (_holdTimer <= 0.0f) PickHuntGoal(predicted);
		}
	}

	private bool FindHoldSpot(Vector3 target, out Vector3 spot)
	{
		NavGrid grid = BotNav.Grid!;
		Vector3 me = _body.GlobalPosition;
		spot = target;
		float best = float.MaxValue;
		bool found = false;
		for (int i = 0; i < 16; i++)
		{
			if (!grid.RandomWalkableNear(target, 6.0f, 16.0f, _rng, out Vector3 c)) continue;
			if (!grid.HasLos(c, EyeLosHeight, target, EyeLosHeight)) continue;
			float d = Flat(c, me);
			if (d < best) { best = d; spot = c; found = true; }
		}
		return found;
	}

	private bool TryFlank(Vector3 predicted, out Vector3 spot)
	{
		NavGrid grid = BotNav.Grid!;
		Vector3 me = _body.GlobalPosition;
		spot = predicted;
		float baseAngle = Mathf.Atan2(me.Z - predicted.Z, me.X - predicted.X);
		float best = float.MaxValue;
		bool found = false;
		for (int i = 0; i < 20; i++)
		{
			if (!grid.RandomWalkableNear(predicted, 8.0f, 14.0f, _rng, out Vector3 c)) continue;
			float angle = Mathf.Atan2(c.Z - predicted.Z, c.X - predicted.X);
			if (Mathf.Abs(BotAim.WrapPi(angle - baseAngle)) < 1.05f) continue; // must come from a different side
			if (!grid.HasLos(c, EyeLosHeight, predicted, EyeLosHeight)) continue;
			float cost = Flat(me, c);
			if (cost < best) { best = cost; spot = c; found = true; }
		}
		return found;
	}

	// ------------------------------------------------------------------------------------------------
	// Engage
	// ------------------------------------------------------------------------------------------------

	private void ActEngage(float dt)
	{
		if (_target == null) return;
		Track t = _target;
		Vector3 me = _body.GlobalPosition;
		Vector3 tp = t.Visible ? t.Body.GlobalPosition : t.LastPos;
		Vector3 toT = tp - me;
		toT.Y = 0.0f;
		float dist = toT.Length();
		Vector3 dir = dist > 0.01f ? toT / dist : (_body.GlobalTransform.Basis * Vector3.Forward);
		WeaponInfo w = CurrentWeaponInfo();

		_aimHasTarget = true;
		_aimEngaged = true;
		_aimHeadTimer -= dt;
		if (_aimHeadTimer <= 0.0f)
		{
			_aimHeadTimer = _rng.RandfRange(1.5f, 3.0f);
			_aimHead = _rng.Randf() < _p.HeadAimChance;
		}
		_aimPoint = PerceivedAimPoint(t);

		if (_now < _freezeUntil) return; // startled by a hit: stands still for a beat

		_tacticTimer -= dt;
		if (_tacticTimer <= 0.0f) ChooseTactic(dist, w);

		switch (_tactic)
		{
			case Tactic.Strafe: StrafeMove(dir, dist, w, dt); break;
			case Tactic.Advance: AdvanceMove(tp, dist, w, dt); break;
			case Tactic.Hold: _wantCrouch = _holdCrouch; break;
			case Tactic.Peek: PeekMove(tp, dist, dt); break;
			case Tactic.Retreat: RetreatMove(dir, tp, dt); break;
		}

		// panicky players hop around mid-fight; pros almost never do
		float hop = Level == BotDifficulty.Easy ? 0.5f : (Level == BotDifficulty.Normal ? 0.06f : 0.015f);
		if (_body.IsOnFloor() && (_tactic == Tactic.Strafe || _tactic == Tactic.Advance) && _rng.Randf() < hop * dt)
			Controls.JumpPulse = true;
	}

	private void ChooseTactic(float dist, WeaponInfo w)
	{
		float hp = _health.CurrentHealth / Mathf.Max(_health.MaxHealth, 1.0f);
		float r = _rng.Randf();
		bool reloadSoon = NeedsReloadSoon();
		bool cover = _p.CoverUse > 0.2f && TryFindCover(_target!.LastPos);
		_hasCover = cover;

		Tactic next;
		float dur = _rng.RandfRange(0.6f, 1.6f);
		switch (Level)
		{
			case BotDifficulty.Easy:
				if (hp < 0.25f && r < 0.4f) { next = Tactic.Retreat; dur = _rng.RandfRange(1.2f, 2.4f); }
				else if (dist > w.IdealMax) next = r < _p.Aggression + 0.05f ? Tactic.Advance : Tactic.Hold;
				else next = r < 0.25f ? Tactic.Strafe : (r < 0.65f ? Tactic.Hold : Tactic.Advance);
				if (next != Tactic.Retreat) dur = _rng.RandfRange(0.8f, 2.2f);
				break;

			case BotDifficulty.Normal:
				if (reloadSoon && cover) { next = Tactic.Retreat; dur = _rng.RandfRange(2.0f, 3.0f); }
				else if (hp < _p.LowHealthCaution && cover && r < 0.6f) next = Tactic.Peek;
				else if (dist > w.IdealMax) next = r < _p.Aggression ? Tactic.Advance : (cover && r < 0.8f ? Tactic.Peek : Tactic.Hold);
				else if (dist < w.IdealMin) next = r < 0.6f ? Tactic.Strafe : (r < 0.85f ? Tactic.Retreat : Tactic.Advance);
				else next = r < 0.55f ? Tactic.Strafe : (cover && r < 0.8f ? Tactic.Peek : Tactic.Hold);
				break;

			default: // Advanced
				bool outnumbered = _visibleCount >= 2 && hp < 0.6f;
				if (reloadSoon && cover) { next = Tactic.Retreat; dur = _rng.RandfRange(2.0f, 3.0f); }
				else if (outnumbered || hp < _p.LowHealthCaution) next = cover ? Tactic.Peek : Tactic.Retreat;
				else if (dist > w.IdealMax + 5.0f) next = (cover && r < 1.0f - _p.Aggression) ? Tactic.Peek : Tactic.Advance;
				else if (dist > 12.0f && cover && r < 0.45f) next = Tactic.Peek;
				else if (r < 0.15f) next = Tactic.Hold;
				else if (dist < w.IdealMin) next = r < 0.5f ? Tactic.Retreat : Tactic.Strafe;
				else next = Tactic.Strafe;
				break;
		}

		// human blunder: wrong call at a bad moment (rare for pros, common for beginners)
		if (_rng.Randf() < _p.MistakeRate)
			next = _rng.Randf() < 0.5f ? Tactic.Advance : Tactic.Hold;

		if (next == Tactic.Peek && !cover) next = Tactic.Strafe;
		_tactic = next;
		_tacticTimer = next == Tactic.Peek ? _rng.RandfRange(4.0f, 6.5f) : dur;
		OnTacticStart(next);
	}

	private void OnTacticStart(Tactic t)
	{
		switch (t)
		{
			case Tactic.Strafe:
				_strafeSign = _rng.Randf() < 0.5f ? 1.0f : -1.0f;
				_strafeTimer = 0.0f;
				break;
			case Tactic.Advance:
				_advanceSprint = Level == BotDifficulty.Easy && _rng.Randf() < 0.5f;
				_pathOk = false;
				break;
			case Tactic.Hold:
				_holdCrouch = Level == BotDifficulty.Advanced ? _rng.Randf() < 0.4f
					: (Level == BotDifficulty.Easy && _rng.Randf() < 0.2f);
				break;
			case Tactic.Peek:
				_peekPhase = PeekPhase.Hidden;
				_peekTimer = _rng.RandfRange(0.4f, 1.1f);
				_pathOk = false;
				break;
			case Tactic.Retreat:
				_pathOk = false;
				break;
		}
	}

	private void StrafeMove(Vector3 dir, float dist, WeaponInfo w, float dt)
	{
		NavGrid grid = BotNav.Grid!;
		Vector3 me = _body.GlobalPosition;
		_strafeTimer -= dt;
		if (_strafeTimer <= 0.0f)
		{
			if (_rng.Randf() < 0.75f) _strafeSign = -_strafeSign;
			_strafeTimer = _rng.RandfRange(_p.StrafeMin, _p.StrafeMax);
		}

		var perp = new Vector3(-dir.Z, 0.0f, dir.X);
		float radial = dist > w.IdealMax ? 0.5f : (dist < w.IdealMin ? -0.6f : 0.0f);
		Vector3 want = (perp * _strafeSign + dir * radial).Normalized();
		if (!grid.IsWalkable(me + want * 1.0f))
		{
			_strafeSign = -_strafeSign;
			want = (perp * _strafeSign + dir * radial).Normalized();
			if (!grid.IsWalkable(me + want * 1.0f)) want = Vector3.Zero;
		}
		_moveWorld = want;
	}

	private void AdvanceMove(Vector3 tp, float dist, WeaponInfo w, float dt)
	{
		bool inRange = Level == BotDifficulty.Easy ? dist < 4.0f : (dist <= w.IdealMax * 0.85f && _target!.Visible);
		if (inRange) return;
		StepAlongPath(dt, tp, out Vector3 _);
		_wantSprint = (Level == BotDifficulty.Easy && _advanceSprint)
			|| (Level != BotDifficulty.Easy && !_target!.Visible && dist > 18.0f);
	}

	private void PeekMove(Vector3 tp, float dist, float dt)
	{
		if (!_hasCover) { _tactic = Tactic.Strafe; _tacticTimer = 0.5f; return; }
		Vector3 me = _body.GlobalPosition;
		_peekTimer -= dt;
		bool visible = _target!.Visible;

		if (visible && dist < 7.0f) { _tacticTimer = 0.0f; return; } // he's on top of me: stop peeking, fight

		if (_peekPhase == PeekPhase.Hidden)
		{
			float dCover = Flat(me, _coverPos);
			if (dCover > 0.8f) StepAlongPath(dt, _coverPos, out Vector3 _);
			else _inCover = true;
			if (_peekTimer <= 0.0f && dCover <= 1.2f)
			{
				_peekPhase = PeekPhase.Exposed;
				_peekTimer = _rng.RandfRange(1.0f, 2.0f);
				_peekCount++;
			}
		}
		else
		{
			if (Flat(me, _peekPos) > 0.7f) StepAlongPath(dt, _peekPos, out Vector3 _);
			if (visible) _peekCount = 0;
			bool hurt = _now - _lastHurtTime < 0.4f;
			if (_peekTimer <= 0.0f || hurt)
			{
				_peekPhase = PeekPhase.Hidden;
				_peekTimer = _rng.RandfRange(0.5f, 1.3f);
			}
		}
	}

	private void RetreatMove(Vector3 dir, Vector3 tp, float dt)
	{
		NavGrid grid = BotNav.Grid!;
		Vector3 me = _body.GlobalPosition;
		Vector3 goal;
		if (_hasCover) goal = _coverPos;
		else
		{
			goal = me - dir * 6.0f;
			if (!grid.IsWalkable(goal)) { _tactic = Tactic.Strafe; _tacticTimer = 0.4f; return; }
		}

		if (Flat(me, goal) > 0.9f)
		{
			StepAlongPath(dt, goal, out Vector3 _);
			_wantSprint = Level == BotDifficulty.Easy;
		}
		else
		{
			_inCover = true;
		}
	}

	/// <summary>
	/// Finds a spot that is hidden from <paramref name="threat"/> plus a nearby "peek" spot that can see
	/// it again after a short sidestep. Scores by closeness, enclosure (corners beat open walls), and a
	/// sensible distance from the threat.
	/// </summary>
	private bool TryFindCover(Vector3 threat)
	{
		NavGrid grid = BotNav.Grid!;
		Vector3 me = _body.GlobalPosition;
		float bestScore = float.MinValue;
		bool found = false;
		for (int i = 0; i < 24; i++)
		{
			if (!grid.RandomWalkableNear(me, 2.0f, 12.0f, _rng, out Vector3 c)) continue;
			if (grid.HasLos(c, EyeLosHeight, threat, EyeLosHeight)) continue;
			float dThreat = Flat(c, threat);
			if (dThreat < 5.0f) continue;

			Vector3 peek = c;
			float bestPeek = float.MaxValue;
			bool hasPeek = false;
			for (int k = 0; k < 8; k++)
			{
				if (!grid.RandomWalkableNear(c, 1.0f, 3.2f, _rng, out Vector3 pc)) continue;
				if (!grid.HasLos(pc, EyeLosHeight, threat, EyeLosHeight)) continue;
				float pd = Flat(pc, c);
				if (pd < bestPeek) { bestPeek = pd; peek = pc; hasPeek = true; }
			}
			if (!hasPeek) continue;

			float score = -Flat(c, me) + Mathf.Min(dThreat, 25.0f) * 0.3f + grid.Enclosure(c, 2) * 6.0f - bestPeek * 1.5f;
			if (score > bestScore)
			{
				bestScore = score;
				_coverPos = c;
				_peekPos = peek;
				found = true;
			}
		}
		return found;
	}

	// ------------------------------------------------------------------------------------------------
	// Looking
	// ------------------------------------------------------------------------------------------------

	private void SetTravelLook(Vector3 heading, float dt)
	{
		Vector3 eye = _camera.GlobalPosition;
		float angle = 0.0f;
		if (Level != BotDifficulty.Easy)
		{
			_glanceTimer -= dt;
			if (_glanceTimer <= 0.0f)
			{
				_glanceTimer = _rng.RandfRange(2.5f, 5.0f);
				_glanceUntil = _now + _rng.RandfRange(0.4f, 0.9f);
				_glanceAngle = (_rng.Randf() < 0.5f ? -1.0f : 1.0f) * Mathf.DegToRad(_rng.RandfRange(45.0f, 85.0f));
			}
			if (_now < _glanceUntil) angle = _glanceAngle;
		}
		Vector3 d = heading.Rotated(Vector3.Up, angle);
		_lookPoint = eye + d * 12.0f;
	}

	private void LookAround(float dt, float speed)
	{
		Vector3 eye = _camera.GlobalPosition;
		Vector3 facing = _body.GlobalTransform.Basis * Vector3.Forward;
		facing.Y = 0.0f;
		if (facing.LengthSquared() < 0.0001f) facing = Vector3.Forward;
		_sweep += dt * speed;
		_lookPoint = eye + facing.Normalized().Rotated(Vector3.Up, Mathf.Sin(_sweep) * 0.9f) * 10.0f;
	}

	// ------------------------------------------------------------------------------------------------
	// Path following, stuck recovery, output
	// ------------------------------------------------------------------------------------------------

	/// <summary>Moves toward <paramref name="goal"/> along an A* path. Returns true once there (or if unreachable).</summary>
	private bool StepAlongPath(float dt, Vector3 goal, out Vector3 heading)
	{
		NavGrid grid = BotNav.Grid!;
		Vector3 me = _body.GlobalPosition;
		heading = _body.GlobalTransform.Basis * Vector3.Forward;
		heading.Y = 0.0f;

		_repathTimer -= dt;
		if (_repathTimer <= 0.0f || !_pathOk || Flat(goal, _pathGoal) > 1.5f)
		{
			_repathTimer = 0.9f;
			_pathGoal = goal;
			_pathIdx = 0;
			_pathOk = grid.FindPath(me, goal, _path);
			if (!_pathOk) return true;
		}

		while (_pathIdx < _path.Count && Flat(me, _path[_pathIdx]) < 0.55f) _pathIdx++;
		if (_pathIdx >= _path.Count) return true;

		Vector3 d = _path[_pathIdx] - me;
		d.Y = 0.0f;
		if (d.LengthSquared() > 0.0001f)
		{
			heading = d.Normalized();
			_moveWorld = heading;
		}
		return false;
	}

	private void UpdateStuck(float dt)
	{
		_stuckCheck -= dt;
		if (_stuckCheck > 0.0f) return;
		_stuckCheck = 0.5f;

		Vector3 me = _body.GlobalPosition;
		bool trying = _moveWorld.LengthSquared() > 0.25f && _now >= _freezeUntil && _counterStop <= 0.0f;
		if (trying && Flat(me, _stuckRef) < 0.12f) _stuckTimer += 0.5f;
		else _stuckTimer = 0.0f;
		_stuckRef = me;

		if (_stuckTimer >= 1.0f)
		{
			_pathOk = false;
			_repathTimer = 0.0f;
			_unstuckUntil = _now + 0.5f;
			_unstuckDir = new Vector3(-_moveWorld.Z, 0.0f, _moveWorld.X) * (_rng.Randf() < 0.5f ? 1.0f : -1.0f);
			if (_stuckTimer >= 1.5f && _body.IsOnFloor()) Controls.JumpPulse = true;
			if (_stuckTimer >= 3.0f)
			{
				_stuckTimer = 0.0f;
				_hasDestination = false;
				if (_mode == Mode.Investigate) { _hasSuspect = false; BeginRoam(); }
			}
		}
	}

	private void ApplyMovement(float dt)
	{
		Vector3 mv = _moveWorld;
		mv.Y = 0.0f;
		if (_now < _unstuckUntil) mv = _unstuckDir;
		if (_now < _freezeUntil || _counterStop > 0.0f) mv = Vector3.Zero;
		_counterStop = Mathf.Max(0.0f, _counterStop - dt);

		if (mv.LengthSquared() < 0.0001f)
		{
			Controls.Move = Vector2.Zero;
			Controls.Sprint = false;
		}
		else
		{
			// keep a little personal space so bots don't stack on each other
			Vector3 me = _body.GlobalPosition;
			Vector3 push = Vector3.Zero;
			for (int i = 0; i < _otherPositions.Count; i++)
			{
				Vector3 away = me - _otherPositions[i];
				away.Y = 0.0f;
				float len = away.Length();
				if (len > 0.01f && len < 1.3f) push += away / len * (1.3f - len);
			}
			Vector3 dir = (mv.Normalized() + push * 0.8f).Normalized();
			Vector3 local = _body.GlobalTransform.Basis.Inverse() * dir;
			Controls.Move = new Vector2(local.X, local.Z);
			bool forwardish = local.Z < -0.2f;
			Controls.Sprint = _wantSprint && forwardish && !_wantCrouch;
		}

		PlayerMovement.MovementState state = _body.State;
		Controls.Crouch = _wantCrouch
			&& state != PlayerMovement.MovementState.Sprint
			&& state != PlayerMovement.MovementState.Slide;
		Controls.Aim = _adsWanted && _mode == Mode.Engage && Controls.Move == Vector2.Zero && !Controls.Crouch;
	}
}
