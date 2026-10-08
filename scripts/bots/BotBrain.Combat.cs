using Godot;
using System;

public partial class BotBrain
{
	private struct WeaponInfo
	{
		public float IdealMin;
		public float IdealMax;

		public WeaponInfo(float idealMin, float idealMax)
		{
			IdealMin = idealMin;
			IdealMax = idealMax;
		}
	}

	// Looked up by weapon name. This used to be a literal array "indexed like WeaponSwitcher.Loadout",
	// but the Kriss Vector was later inserted at index 3 of Loadout, which shifted everything after
	// it: bots then believed index 8 (Bazooka) was the Kriss and gave it real main-weapon weight, and
	// the Shotgun/Glock/Desert Eagle entries all pointed one slot off.
	private static int LoadoutIndex(string weaponName)
	{
		int index = System.Array.IndexOf(WeaponSwitcher.Loadout, weaponName);
		return index < 0 ? 0 : index;
	}

	private static readonly WeaponInfo[] WeaponTable = BuildWeaponTable();

	private static WeaponInfo[] BuildWeaponTable()
	{
		var table = new WeaponInfo[WeaponSwitcher.Loadout.Length];
		for (int i = 0; i < table.Length; i++) table[i] = new WeaponInfo(3.0f, 28.0f);
		table[LoadoutIndex("AK-47")] = new WeaponInfo(6.0f, 45.0f);
		table[LoadoutIndex("M4")] = new WeaponInfo(5.0f, 45.0f);
		table[LoadoutIndex("P90")] = new WeaponInfo(3.0f, 28.0f);
		table[LoadoutIndex("Kriss Vector")] = new WeaponInfo(3.0f, 28.0f);
		table[LoadoutIndex("Glock 19")] = new WeaponInfo(3.0f, 22.0f);
		table[LoadoutIndex("Desert Eagle")] = new WeaponInfo(4.0f, 30.0f);
		table[LoadoutIndex("Shotgun")] = new WeaponInfo(0.0f, 9.0f);
		return table;
	}

	private static readonly int GlockIndex = LoadoutIndex("Glock 19");
	private static readonly int DesertEagleIndex = LoadoutIndex("Desert Eagle");

	/// <summary>Weights over Loadout by name. Karambit and Bazooka are never a bot's main weapon, so
	/// bots never carry the heavy weapon (it is Demolisher-only).</summary>
	private static float[] BuildWeights(float ak, float m4, float p90, float kriss, float glock, float deagle, float shotgun)
	{
		var weights = new float[WeaponSwitcher.Loadout.Length];
		weights[LoadoutIndex("AK-47")] = ak;
		weights[LoadoutIndex("M4")] = m4;
		weights[LoadoutIndex("P90")] = p90;
		weights[LoadoutIndex("Kriss Vector")] = kriss;
		weights[LoadoutIndex("Glock 19")] = glock;
		weights[LoadoutIndex("Desert Eagle")] = deagle;
		weights[LoadoutIndex("Shotgun")] = shotgun;
		return weights;
	}

	private int _primary;
	private int _secondary = GlockIndex;
	private int _burstLeft;
	private float _pauseTimer;
	private float _semiTimer;
	private float _swapCooldown;
	private int _lastAmmo = -1;
	private int _lastAmmoWeapon = -1;

	private WeaponInfo CurrentWeaponInfo() =>
		WeaponTable[Mathf.Clamp(_weapon.ReplicatedWeaponIndex, 0, WeaponTable.Length - 1)];

	// ------------------------------------------------------------------------------------------------
	// Loadout
	// ------------------------------------------------------------------------------------------------

	private int WeightedIndex(float[] weights)
	{
		float total = 0.0f;
		for (int i = 0; i < weights.Length; i++) total += weights[i];
		float roll = _rng.RandfRange(0.0f, total);
		for (int i = 0; i < weights.Length; i++)
		{
			roll -= weights[i];
			if (roll <= 0.0f) return i;
		}
		return 0;
	}

	/// <summary>Picks what this bot carries this life. Weak bots choose more oddly, like beginners do.</summary>
	private void PickLoadout()
	{
		float[] weights;
		switch (Level)
		{
			case BotDifficulty.Easy: weights = BuildWeights(ak: 20, m4: 15, p90: 20, kriss: 12, glock: 15, deagle: 10, shotgun: 20); break;
			case BotDifficulty.Advanced: weights = BuildWeights(ak: 42, m4: 42, p90: 10, kriss: 10, glock: 0, deagle: 0, shotgun: 6); break;
			default: weights = BuildWeights(ak: 30, m4: 30, p90: 20, kriss: 15, glock: 5, deagle: 5, shotgun: 10); break;
		}
		_primary = WeightedIndex(weights);
		_secondary = _primary == GlockIndex ? 0 : GlockIndex;
		if (Level == BotDifficulty.Advanced && _rng.Randf() < 0.25f && _primary != DesertEagleIndex) _secondary = DesertEagleIndex;

		_weapon.ReplicatedWeaponIndex = _primary;
		_lastAmmo = -1;
		_lastAmmoWeapon = -1;
		_burstLeft = 0;
	}

	private void SwitchWeapon(int index)
	{
		_weapon.ReplicatedWeaponIndex = index;
		_swapCooldown = _now + 1.0f;
		_lastAmmo = -1;
		_lastAmmoWeapon = -1;
		_burstLeft = 0;
	}

	// ------------------------------------------------------------------------------------------------
	// Weapon / ammo upkeep (runs every frame from Think)
	// ------------------------------------------------------------------------------------------------

	private bool NeedsReloadSoon()
	{
		int mag = _weapon.MagazineSize;
		if (mag <= 0 || _weapon.CurrentReserve <= 0) return false;
		return (float)_weapon.CurrentAmmo / mag < 0.35f;
	}

	private void ManageWeapon(float dt)
	{
		int idx = _weapon.ReplicatedWeaponIndex;
		int mag = _weapon.MagazineSize;
		int ammo = _weapon.CurrentAmmo;
		int reserve = _weapon.CurrentReserve;
		bool enemyVisible = _mode == Mode.Engage && _target != null && _target.Visible;
		if (mag <= 0) return; // melee / unknown

		// Nothing left at all: in this game equipping another weapon refills it, so swap.
		if (ammo <= 0 && reserve <= 0 && _now >= _swapCooldown)
		{
			SwitchWeapon(idx == _primary ? _secondary : _primary);
			return;
		}

		// Pros pull the sidearm rather than reload with someone on top of them.
		if (Level == BotDifficulty.Advanced && ammo <= 0 && reserve > 0 && enemyVisible && idx == _primary
			&& _now >= _swapCooldown && _target != null && Flat(_target.LastPos, _body.GlobalPosition) < 14.0f)
		{
			SwitchWeapon(_secondary);
			return;
		}

		// Back to the main weapon once things calm down.
		if (idx != _primary && !enemyVisible && _now - _lastEngageTime > 2.5f && _now >= _swapCooldown)
		{
			SwitchWeapon(_primary);
			return;
		}

		if (_weapon.IsReloading || ammo >= mag || reserve <= 0) return;

		float frac = (float)ammo / mag;
		bool reload;
		switch (Level)
		{
			case BotDifficulty.Easy:
				// beginners only remember to reload when nobody is shooting at them; otherwise they just click
				reload = ammo <= 0 && !enemyVisible;
				break;
			case BotDifficulty.Normal:
				reload = ammo <= 0 || (frac <= 0.25f && !enemyVisible) || (_inCover && frac < 0.6f);
				break;
			default:
				reload = ammo <= 0
					|| (frac < 0.5f && !enemyVisible && _now - _lastEngageTime > 1.0f)
					|| (_inCover && frac < 0.7f);
				break;
		}
		if (reload) Controls.ReloadPulse = true;
	}

	// ------------------------------------------------------------------------------------------------
	// Aim
	// ------------------------------------------------------------------------------------------------

	private void UpdatePositionNoise(float dt)
	{
		// slow drift in where the bot believes the enemy is (bigger for weaker bots)
		float amp = _p.PositionNoise * (1.0f + 2.0f * Mathf.Max(Ease, 0.0f));
		const float tau = 0.4f;
		float kick = amp * Mathf.Sqrt(2.0f * dt / tau);
		_posNoise.X += (-_posNoise.X / tau) * dt + kick * _rng.Randfn(0.0f, 1.0f);
		_posNoise.Y += (-_posNoise.Y / tau) * dt + kick * 0.4f * _rng.Randfn(0.0f, 1.0f);
		_posNoise.Z += (-_posNoise.Z / tau) * dt + kick * _rng.Randfn(0.0f, 1.0f);
	}

	/// <summary>
	/// Where the bot BELIEVES the enemy is: the position from a moment ago (perception delay), pushed
	/// forward by how well it predicts movement, plus slow drift. Chest or head height per its habit.
	/// </summary>
	private Vector3 PerceivedAimPoint(Track t)
	{
		float delay = _p.PerceptionDelay * (1.0f + Ease);
		Vector3 head;
		if (t.Visible)
		{
			head = SampleHistory(t, _now - delay) + t.Vel * (_p.Lead * delay);
		}
		else
		{
			float since = Mathf.Min(_now - t.LastSeen, 0.8f);
			head = t.LastHead + new Vector3(t.Vel.X, 0.0f, t.Vel.Z) * (since * 0.5f * _p.Lead);
		}
		float drop = _aimHead ? 0.12f : 0.5f;
		return head - new Vector3(0.0f, drop, 0.0f) + _posNoise;
	}

	private void AimAndFire(float dt)
	{
		Vector3 eye = _camera.GlobalPosition;
		Vector3 point = _aimHasTarget ? _aimPoint : _lookPoint;
		Vector3 d = point - eye;
		if (d.LengthSquared() < 0.01f)
		{
			UpdateFire(dt);
			return;
		}

		float wantYaw = Mathf.Atan2(-d.X, -d.Z);
		float wantPitch = Mathf.Atan2(d.Y, Mathf.Sqrt(d.X * d.X + d.Z * d.Z));
		if (_aimEngaged)
		{
			// pull back against the weapon's kick, as well as this bot's skill allows
			wantYaw -= _p.RecoilCompensation * _body.RecoilYawRadians;
			wantPitch -= _p.RecoilCompensation * _body.RecoilPitchRadians;
		}

		float ease = Ease;
		float speedNorm = Mathf.Clamp(_horizSpeed / 7.2f, 0.0f, 1.2f);
		float noiseScale = (1.0f + 2.0f * ease) * (1.0f + _p.MoveAimPenalty * speedNorm)
			* (_body.IsOnFloor() ? 1.0f : 2.0f) * (_aimEngaged ? 1.0f : 0.35f);
		noiseScale = Mathf.Max(noiseScale, 0.2f);
		float easeAim = Mathf.Max(1.0f + 2.0f * ease, 0.5f);

		_aim.Step(dt, wantYaw, wantPitch, _body.Rotation.Y, _body.AimPitch, _p, _aimEngaged,
			noiseScale, easeAim, out float dYaw, out float dPitch);
		_body.BotLook(dYaw, dPitch);

		UpdateFire(dt);
	}

	// ------------------------------------------------------------------------------------------------
	// Trigger discipline
	// ------------------------------------------------------------------------------------------------

	private int PickBurst(float dist)
	{
		if (_weapon.WeaponName == "Shotgun") return 1;
		if (_weapon.FireMode != WeaponData.FireModeType.FullAuto)
			return Level == BotDifficulty.Advanced ? _rng.RandiRange(1, 2) : _rng.RandiRange(1, 3);
		int burst = _rng.RandiRange(_p.BurstMin, _p.BurstMax);
		if (Level != BotDifficulty.Easy && dist > 28.0f) burst = Mathf.Min(burst, _rng.RandiRange(1, 3)); // tap at range
		return burst;
	}

	private bool TeammateInLineOfFire(Vector3 eye, Vector3 fwd)
	{
		var query = PhysicsRayQueryParameters3D.Create(eye, eye + fwd * 60.0f, 0xFFFFFFFF, _selfExclude);
		var hit = _body.GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (hit.Count == 0) return false;
		if (hit["collider"].As<Node>() is CharacterBody3D other)
			return !IsEnemy(PlayerIdentity.IdOf(other));
		return false;
	}

	private void UpdateFire(float dt)
	{
		Controls.FireHeld = false;
		_semiTimer -= dt;
		if (_pauseTimer > 0.0f) _pauseTimer -= dt;

		// count the rounds that actually left the barrel to run the burst
		int idx = _weapon.ReplicatedWeaponIndex;
		int ammo = _weapon.CurrentAmmo;
		if (idx == _lastAmmoWeapon && ammo < _lastAmmo)
		{
			_burstLeft -= _lastAmmo - ammo;
			_lastFiredTime = _now;
			_semiTimer = _rng.RandfRange(_p.SemiDelayMin, _p.SemiDelayMax);
			if (_burstLeft <= 0)
			{
				_burstLeft = 0;
				_pauseTimer = _rng.RandfRange(_p.BurstPauseMin, _p.BurstPauseMax);
			}
		}
		_lastAmmo = ammo;
		_lastAmmoWeapon = idx;

		if (_mode != Mode.Engage || _target == null || _now < _freezeUntil)
		{
			_burstLeft = 0;
			return;
		}
		Track t = _target;

		if (!t.Visible)
		{
			// a beginner keeps spraying for a moment after the target ducks out of sight
			bool spraying = Level == BotDifficulty.Easy && _burstLeft > 0 && _now - t.LastSeen < 0.6f;
			if (!spraying)
			{
				_burstLeft = 0;
				return;
			}
		}

		int mag = _weapon.MagazineSize;
		if (mag > 0 && ammo <= 0)
		{
			_burstLeft = 0;
			if (Level == BotDifficulty.Easy)
			{
				// keeps pulling the trigger on an empty mag: click, then the game's auto-reload
				Controls.FirePulse = true;
				Controls.FireHeld = true;
			}
			return;
		}
		if (_weapon.IsReloading && _weapon.WeaponName != "Shotgun") return;

		Vector3 eye = _camera.GlobalPosition;
		Vector3 fwd = -_camera.GlobalTransform.Basis.Z;
		Vector3 to = _aimPoint - eye;
		float dist = to.Length();
		if (dist < 0.1f) return;

		// Fire only when the crosshair is on where the bot BELIEVES the enemy is (never on the true
		// position), so delays, noise and bad prediction turn into real misses.
		float err = fwd.AngleTo(to / dist);
		float radius = Mathf.Atan2(0.28f, Mathf.Max(dist, 1.0f));
		float cone = radius * _p.FireCone * (1.0f + Ease);
		WeaponInfo w = CurrentWeaponInfo();
		float maxRange = Level == BotDifficulty.Easy ? 60.0f : w.IdealMax * 1.6f + 6.0f;
		bool gate = err < cone && dist <= maxRange && !_aim.IsFlicking;
		if (gate && IsTeamMode() && TeammateInLineOfFire(eye, fwd)) gate = false;

		if (_pauseTimer > 0.0f && _burstLeft <= 0) return;
		if (_burstLeft <= 0)
		{
			if (!gate) return;
			if (_p.CounterStrafe && _horizSpeed > 1.6f)
			{
				_counterStop = 0.14f; // stop moving for a beat so the first bullets are accurate
				return;
			}
			_burstLeft = PickBurst(dist);
		}
		if (!gate && Level != BotDifficulty.Easy)
		{
			_burstLeft = 0; // aim drifted off mid-burst: a decent player lets go of the trigger
			return;
		}

		if (_weapon.FireMode == WeaponData.FireModeType.FullAuto) Controls.FireHeld = true;
		else if (_semiTimer <= 0.0f) Controls.FirePulse = true;
	}
}
