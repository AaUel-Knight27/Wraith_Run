using Godot;

/// <summary>
/// Owns the equipped weapon: ammo, fire rate, reload state, hitscan resolution, and the recoil
/// kick. Model attachment stays centralized in WeaponAttachment.
///
/// Networking split:
///  - INPUT and DAMAGE are authority-only. Only the owning device reads the mouse and only it
///    sends the damage RPC, which keeps the existing client-authoritative model intact.
///  - WHICH WEAPON is a replicated property (ReplicatedWeaponIndex). Making it state rather than
///    an event means a peer that joins mid-match still sees the right gun in everyone's hands.
///  - FIRE and RELOAD EFFECTS are broadcast RPCs. Before this, PlayFire ran only inside the
///    authority's own _Process, so nobody ever heard or saw anyone else shoot.
///
/// It is also where a shot's kill context is captured: whether the bullet landed in the head zone,
/// whether the shooter was aiming, how far they spun in the last second, and how hurt they were at
/// the moment of firing. That context travels with the damage RPC so the victim's authoritative
/// device can score the kill through the one shared KillStyleBonus calculator (FR-SC-01).
/// </summary>
public partial class WeaponSwitcher : Node
{
	private const float MaxRange = 500.0f;
	private const float MeleeRange = 2.5f;

	/// <summary>Radius of the sphere around the Head node that counts as a head hit. The player rig
	/// is a single capsule, so the head zone is a geometric test against the live Head position
	/// rather than a separate hitbox.</summary>
	private const float HeadZoneRadius = 0.25f;

	// --- shot audio ---------------------------------------------------------------------------
	/// <summary>A shotgun fires 8 pellets per pull, all landing within a few metres of each other;
	/// 8 stacked impact sounds is a smear, not realism. At most this many impact (and flesh-hit)
	/// sounds are broadcast per trigger pull - damage is unaffected.</summary>
	private const int MaxImpactSoundsPerShot = 2;

	/// <summary>|direction . surface normal| below this is a glancing hit (roughly under 20 degrees
	/// to the surface) - the case where a real bullet skips off hard material.</summary>
	private const float GlancingIncidence = 0.35f;
	private const float GlancingRicochetChance = 0.60f;
	private const float HeadOnRicochetChanceMetal = 0.12f;
	private const float HeadOnRicochetChanceGeneric = 0.04f;

	/// <summary>How close a bullet's path has to pass to a listener for the whip-crack flyby (metres),
	/// and the same for a rocket, which is far bigger and louder.</summary>
	private const float FlyByRadius = 3.0f;
	private const float RocketFlyByRadius = 9.0f;
	/// <summary>No flyby for a shot fired point-blank at the listener, or one that ends right next
	/// to them (that is the listener being hit, or the impact beside them, not a near miss).</summary>
	private const float FlyByMinShooterDistance = 2.5f;
	private const float FlyByMinImpactDistance = 1.5f;

	// Internal rather than private: TouchControls' weapon-switch/quick-melee buttons read this
	// directly (length + Karambit's index) instead of hardcoding numbers that would silently go
	// stale if this list is ever reordered or extended.
	internal static readonly string[] Loadout =
		{ "AK-47", "M4", "P90", "Kriss Vector", "Glock 19", "Desert Eagle", "FN 502", "Shotgun", "Karambit", "Bazooka", "Sniper" };

	private static readonly string[] HitMarkerSounds =
	{
		"res://audio/Clips/Weapons/Impacts/Feedback/hit_marker.mp3",
	};

	private WeaponAttachment _attachment = null!;
	private ViewModelManager? _viewmodel;
	private WeaponRegistry _registry = null!;
	private Camera3D _camera = null!;
	private CharacterBody3D _player = null!;
	private PlayerMovement _movement = null!;
	private PlayerAnimationController? _animation;
	private RotationWindowTracker _rotationWindow = null!;
	private Health? _health;
	private CharacterLoadout? _loadout;
	private AudioStreamPlayer? _hitMarker;
	private readonly RandomNumberGenerator _rng = new();

	/// <summary>
	/// true: every loadout weapon remembers its own magazine and reserve while you carry another one,
	/// and all of them are refilled when you respawn. This is what makes "ammo per weapon" real, and
	/// what the pause menu's Items screen shows.
	/// false: the original behaviour - equipping a weapon always hands it back full, so swapping is
	/// an instant free reload and non-equipped weapons always read as full.
	/// </summary>
	private const bool KeepAmmoPerWeapon = true;

	private WeaponData? _currentWeapon;
	private int _currentAmmo;
	private int _currentReserve;
	private float _cooldownRemaining;
	private float _reloadRemaining;
	private int _appliedWeaponIndex = -1;
	private int _impactSoundsThisShot;
	private int _fleshSoundsThisShot;
	/// <summary>Counts trigger pulls. All eight pellets of one shotgun blast share a number, which is
	/// how the career tracker tells "one shot killed two players" from "two shots".</summary>
	private int _shotSerial;

	// Ammo of every loadout slot that is NOT the equipped one (the equipped weapon's live numbers
	// are _currentAmmo/_currentReserve above). _hasSavedAmmo[i] is false until slot i has been
	// carried at least once, so a never-used weapon still reads as full.
	private readonly int[] _savedAmmo = new int[Loadout.Length];
	private readonly int[] _savedReserve = new int[Loadout.Length];
	private readonly bool[] _hasSavedAmmo = new bool[Loadout.Length];

	/// <summary>
	/// Index into Loadout, replicated by PlayerNetworkSynchronizer. The authority writes it when
	/// the player switches; every other peer receives it and applies the model in the setter.
	/// </summary>
	[Export]
	public int ReplicatedWeaponIndex
	{
		get => _replicatedWeaponIndex;
		set
		{
			_replicatedWeaponIndex = value;
			ApplyWeaponIndex(value);
		}
	}
	private int _replicatedWeaponIndex;

	// Read-only surface for the HUD. Kept separate from the fields above so nothing outside this
	// script can mutate combat state directly.
	public string WeaponName => _currentWeapon?.WeaponName ?? string.Empty;
	public int CurrentAmmo => _currentAmmo;
	public int MagazineSize => _currentWeapon?.MagazineSize ?? 0;

	/// <summary>Rounds left in reserve, beyond what's loaded. Always 0 for a melee weapon.</summary>
	public int CurrentReserve => _currentReserve;
	public bool IsReloading => _reloadRemaining > 0.0f;
	public WeaponData.FireModeType FireMode => _currentWeapon?.FireMode ?? WeaponData.FireModeType.FullAuto;

	// Training Center hooks; regular matches do not subscribe to these events.
	public WeaponData? CurrentWeaponData => _currentWeapon;
	public event System.Action<WeaponData, int>? ShotFired;
	public event System.Action<WeaponData>? ReloadStarted;
	public event System.Action<WeaponData>? WeaponEquipped;

	public void RefillAmmo()
	{
		if (_currentWeapon == null) return;
		_currentAmmo = _currentWeapon.MagazineSize;
		_currentReserve = _currentWeapon.MaxReserveAmmo;
		_reloadRemaining = 0.0f;
	}

	public void TopUpReserve()
	{
		if (_currentWeapon != null) _currentReserve = _currentWeapon.MaxReserveAmmo;
	}

	public void DrainMagazine()
	{
		if (_currentWeapon != null && _currentWeapon.MagazineSize > 0) _currentAmmo = 0;
	}

	public void SetLoadedRounds(int rounds)
	{
		if (_currentWeapon != null && _currentWeapon.MagazineSize > 0)
			_currentAmmo = Mathf.Clamp(rounds, 0, _currentWeapon.MagazineSize);
	}

	public bool EquipByName(string weaponName)
	{
		int index = System.Array.IndexOf(Loadout, weaponName);
		return index >= 0 && TryEquip(index);
	}

	/// <summary>Practice range rack: equip any registered weapon without changing the player's saved
	/// weapon set. This entry point is used only by the offline shooting range.</summary>
	public bool EquipByNameForPractice(string weaponName)
	{
		int index = System.Array.IndexOf(Loadout, weaponName);
		if (index < 0 || !PlayerIdentity.HasAuthority(_player) || (_health != null && _health.IsDead)) return false;
		ReplicatedWeaponIndex = index;
		return _appliedWeaponIndex == index;
	}

	/// <summary>Slot in Loadout of the weapon that is actually equipped right now (-1 before the
	/// first equip in _Ready has run).</summary>
	public int EquippedIndex => _appliedWeaponIndex;

	public static int LoadoutCount => Loadout.Length;

	/// <summary>Stats for a loadout slot, or null if the slot is out of range or not in the registry.</summary>
	public WeaponData? GetWeaponData(int index)
	{
		if (_registry == null || index < 0 || index >= Loadout.Length) return null;
		foreach (var weapon in _registry.Weapons)
			if (weapon.WeaponName == Loadout[index]) return weapon;
		return null;
	}

	/// <summary>Magazine and reserve for any loadout slot: live numbers for the equipped weapon, the
	/// remembered ones for a weapon carried earlier, full for one never used (or for every weapon when
	/// KeepAmmoPerWeapon is off). Melee reads 0/0 - MagazineSize 0 is how the rest of the game marks
	/// "no ammo" weapons.</summary>
	public bool TryGetAmmo(int index, out int magazine, out int reserve)
	{
		magazine = 0;
		reserve = 0;
		WeaponData? weapon = GetWeaponData(index);
		if (weapon == null) return false;

		if (index == _appliedWeaponIndex) { magazine = _currentAmmo; reserve = _currentReserve; }
		else if (KeepAmmoPerWeapon && _hasSavedAmmo[index]) { magazine = _savedAmmo[index]; reserve = _savedReserve[index]; }
		else { magazine = weapon.MagazineSize; reserve = weapon.MaxReserveAmmo; }
		return true;
	}

	/// <summary>Same effect as pressing that slot's number key: used by the pause menu's Weapon Set
	/// screen. Only the owning device may change its own weapon, and not while dead (the number keys
	/// are ignored then too).</summary>
	public bool TryEquip(int index)
	{
		if (!PlayerIdentity.HasAuthority(_player)) return false;
		if (index < 0 || index >= Loadout.Length) return false;
		if (!CanCarryIndex(index)) return false;
		if (_health != null && _health.IsDead) return false;
		ReplicatedWeaponIndex = index;
		return true;
	}

	/// <summary>Hub weapon wall: hold a weapon that was just written into the active set.</summary>
	public void TryEquipFromHub(int index) => TryEquip(index);

	// ---- what this person may carry --------------------------------------------------------------

	/// <summary>Demolisher is the only class that carries the heavy weapon (the Bazooka).</summary>
	private bool IsDemolisher => _loadout?.Data != null
		? _loadout.Data.Class == CharacterData.CharacterClass.Demolisher
		: WeaponRules.IsDemolisher(PlayerProfile.OperatorId);

	/// <summary>Loadout index of the weapon in one carry slot of the active weapon set, or -1 when the
	/// slot does not exist for this operator (Heavy, unless Demolisher) or holds nothing equippable.</summary>
	public int CarriedIndexFor(WeaponSlot slot)
	{
		if (!WeaponRules.SlotAvailable(slot, IsDemolisher)) return -1;
		string name = PlayerProfile.GetSetWeapon(PlayerProfile.ActiveWeaponSet, slot);
		return string.IsNullOrEmpty(name) ? -1 : System.Array.IndexOf(Loadout, name);
	}

	/// <summary>True when this Loadout index is one of the weapons the person may hold right now.</summary>
	public bool CanCarryIndex(int index)
	{
		for (int slot = 0; slot < WeaponRules.SlotCount; slot++)
			if (CarriedIndexFor((WeaponSlot)slot) == index) return true;
		return false;
	}

	/// <summary>Next carried weapon after the one in hand (touch SWAP button).</summary>
	private void CycleCarried()
	{
		int current = ReplicatedWeaponIndex;
		int firstSlot = -1;
		for (int slot = 0; slot < WeaponRules.SlotCount; slot++)
		{
			if (CarriedIndexFor((WeaponSlot)slot) == current) { firstSlot = slot; break; }
		}
		for (int step = 1; step <= WeaponRules.SlotCount; step++)
		{
			int next = CarriedIndexFor((WeaponSlot)((firstSlot + step + WeaponRules.SlotCount) % WeaponRules.SlotCount));
			if (next >= 0 && next != current) { ReplicatedWeaponIndex = next; return; }
		}
	}

	public override void _Ready()
	{
		_rng.Randomize();
		_player = (CharacterBody3D)GetParent();
		_movement = (PlayerMovement)GetParent();
		_attachment = GetNode<WeaponAttachment>("../Armature/WeaponAttachment");
		_viewmodel = GetNodeOrNull<ViewModelManager>("../Head/CameraShake/ViewModelManager");
		_camera = GetNode<Camera3D>("../Head/CameraShake/FirstPersonCamera");
		_rotationWindow = GetNode<RotationWindowTracker>("../RotationWindow");
		_health = GetNodeOrNull<Health>("../Health");
		_loadout = GetNodeOrNull<CharacterLoadout>("../CharacterLoadout");
		_animation = GetNodeOrNull<PlayerAnimationController>("../AnimationController");
		_registry = ResourceLoader.Load<WeaponRegistry>("res://scripts/data/WeaponRegistry.tres")!;

		// The hit-marker tick is a non-positional UI sound: only the person at this machine gets one
		// (a bot the host simulates is 'authority' here too and must stay silent).
		if (PlayerIdentity.IsLocalHuman(_player)) _hitMarker = BuildHitMarker();
		if (PlayerIdentity.HasAuthority(_player))
		{
			if (_health != null && KeepAmmoPerWeapon) _health.Respawned += OnRespawned;
		}
		// The lobby's "Choose Weapon" pick - for the person at this machine only, never a bot.
		// Deferred so it goes through the replicated-index setter after the spawn snapshot.
		if (PlayerIdentity.IsLocalHuman(_player)) Callable.From(EquipStartingWeapon).CallDeferred();

		// Apply whatever index is already set. On the authority that is 0 (the AK); on a remote
		// copy the synchronizer may already have delivered the real value.
		ApplyWeaponIndex(_replicatedWeaponIndex);
	}

	/// <summary>Switches to the weapon picked in the lobby. A pick that is not in Loadout (a weapon
	/// still being built) leaves the default slot alone.</summary>
	private void EquipStartingWeapon()
	{
		if (!IsInstanceValid(this) || !PlayerIdentity.IsLocalHuman(_player)) return;
		int chosen = System.Array.IndexOf(Loadout, PlayerProfile.StartingWeaponName);
		if (chosen > 0) ReplicatedWeaponIndex = chosen;
	}

	public override void _ExitTree()
	{
		if (_health != null && IsInstanceValid(_health)) _health.Respawned -= OnRespawned;
	}

	public override void _Process(double delta)
	{
		if (!PlayerIdentity.HasAuthority(_player)) return;

		// While the pause menu is open no gameplay key or button reaches this weapon (the timers
		// below still run, so a reload that was already under way finishes normally).
		bool inputBlocked = GameUiState.BlocksGameplayInput;
		BotControls? bot = _movement.Bot;

		// A person carries two primaries, one pistol, one knife and - Demolisher only - one heavy, so
		// the keys are the five carry slots (1 Primary 1, 2 Primary 2, 3 Pistol, 4 Knife, 5 Heavy),
		// not the nine weapons in Loadout. Bots are not limited by this (they pick their own).
        if (bot == null && !inputBlocked && !_movement.IsVehicleDriver)
		{
			for (int slot = 0; slot < WeaponRules.SlotCount; slot++)
			{
				string action = $"weapon_{slot + 1}";
				// A missing action logs an error every frame in Godot, so ask InputMap first.
				if (!InputMap.HasAction(action) || !Input.IsActionJustPressed(action)) continue;
				int carried = CarriedIndexFor((WeaponSlot)slot);
				if (carried >= 0) ReplicatedWeaponIndex = carried;
			}
			if (InputMap.HasAction("weapon_cycle_next") && Input.IsActionJustPressed("weapon_cycle_next")) CycleCarried();
		}

		if (_health != null && _health.IsDead) return;

		float step = (float)delta;
		_cooldownRemaining = Mathf.Max(0.0f, _cooldownRemaining - step);
		if (_reloadRemaining > 0.0f)
		{
			_reloadRemaining -= step;
			if (_reloadRemaining <= 0.0f) FinishReload();
		}

        if (! _movement.IsVehicleDriver && (bot != null ? bot.ConsumeReload() : (!inputBlocked && Input.IsActionJustPressed("reload")))) StartReload();

		// Rate-limited by FireRateRPM below. Semi-auto weapons (Glock, Desert Eagle, shotgun)
		// require a fresh press per shot - holding the trigger on them fires one round and then
		// waits for the cooldown to drain without firing again. Full-auto keeps firing on hold.
		bool fireRequested = FireMode == WeaponData.FireModeType.FullAuto
            ? (bot != null ? bot.FireHeld : (!inputBlocked && !_movement.IsVehicleDriver && Input.IsActionPressed("fire")))
            : (bot != null ? bot.ConsumeFirePulse() : (!inputBlocked && !_movement.IsVehicleDriver && Input.IsActionJustPressed("fire")));
		if (fireRequested && _cooldownRemaining <= 0.0f) TryFire();
	}

	// -------------------------------------------------------------------------------------------
	// Equipping
	// -------------------------------------------------------------------------------------------

	private void ApplyWeaponIndex(int index)
	{
		if (_registry == null || index == _appliedWeaponIndex) return;
		if (index < 0 || index >= Loadout.Length)
		{
			GD.PushError($"Weapon index {index} is outside the {Loadout.Length}-slot test loadout.");
			return;
		}

		foreach (var weapon in _registry.Weapons)
		{
			if (weapon.WeaponName != Loadout[index]) continue;
			RememberOutgoingAmmo();
			_appliedWeaponIndex = index;
			_currentWeapon = weapon;
			if (KeepAmmoPerWeapon && _hasSavedAmmo[index])
			{
				_currentAmmo = _savedAmmo[index];
				_currentReserve = _savedReserve[index];
			}
			else
			{
				_currentAmmo = weapon.MagazineSize;
				_currentReserve = weapon.MaxReserveAmmo;
			}
			_cooldownRemaining = 0.0f;
			_reloadRemaining = 0.0f;
			_animation?.CancelReload();
			_attachment.Equip(weapon);
			_viewmodel?.EquipWeapon(weapon.WeaponName);
			WeaponEquipped?.Invoke(weapon);
			return;
		}
		GD.PushError($"Test loadout weapon '{Loadout[index]}' is not registered.");
	}

	/// <summary>Stores the numbers of the weapon that is about to be put away, so they are still
	/// there when it is picked up again.</summary>
	private void RememberOutgoingAmmo()
	{
		if (!KeepAmmoPerWeapon || _currentWeapon == null) return;
		if (_appliedWeaponIndex < 0 || _appliedWeaponIndex >= Loadout.Length) return;
		_savedAmmo[_appliedWeaponIndex] = _currentAmmo;
		_savedReserve[_appliedWeaponIndex] = _currentReserve;
		_hasSavedAmmo[_appliedWeaponIndex] = true;
	}

	/// <summary>Respawning resupplies the whole loadout. Without this, once ammo is per-weapon a
	/// player who has shot everything dry would have no way to ever get more.</summary>
	private void OnRespawned()
	{
		for (int i = 0; i < Loadout.Length; i++) _hasSavedAmmo[i] = false;
		if (_currentWeapon == null) return;
		_currentAmmo = _currentWeapon.MagazineSize;
		_currentReserve = _currentWeapon.MaxReserveAmmo;
		_reloadRemaining = 0.0f;
		_animation?.CancelReload();
	}

	// -------------------------------------------------------------------------------------------
	// Firing
	// -------------------------------------------------------------------------------------------

	private void TryFire()
	{
		if (_currentWeapon == null) return;
		bool infiniteAmmo = _currentWeapon.MagazineSize <= 0;

		if (!infiniteAmmo && _currentAmmo <= 0)
		{
			// Dry fire: a click and a rate limit, so holding the trigger on an empty mag does not
			// spam. Only auto-reload if a reload is not already running, and only if there's
			// reserve ammo left to pull from - otherwise this is just "out of ammo".
			_attachment.PlayEmpty();
			_cooldownRemaining = 0.35f;
			if (_reloadRemaining <= 0.0f && _currentReserve > 0) StartReload();
			return;
		}

		// A shell-by-shell reload is interruptible - that is the point of it. Firing mid-tube keeps
		// whatever shells are already loaded.
		if (_reloadRemaining > 0.0f)
		{
			if (!_currentWeapon.ShellByShellReload) return;
			_reloadRemaining = 0.0f;
			_animation?.CancelReload();
			// Everyone else's copy of this reload sound is still playing - cut it off too.
			Rpc(MethodName.RpcCancelReloadEffects);
		}

		_cooldownRemaining = _currentWeapon.FireRateRPM > 0.0f ? 60.0f / _currentWeapon.FireRateRPM : 0.5f;
		if (!infiniteAmmo) _currentAmmo--;

		Rpc(MethodName.RpcFireEffects);
		ApplyRecoil();

		_impactSoundsThisShot = 0;
		_fleshSoundsThisShot = 0;
		_shotSerial++;

		int pellets = Mathf.Max(1, _currentWeapon.PelletsPerShot);
		ShotFired?.Invoke(_currentWeapon, pellets);
		for (int i = 0; i < pellets; i++)
			FireHitscan(spreadDegrees: pellets > 1 || _currentWeapon.SpreadDegrees > 0.0f
				? _currentWeapon.SpreadDegrees
				: 0.0f);
	}

	/// <summary>Fire audio, muzzle flash and the upper-body fire animation. Broadcast so remote
	/// peers see and hear the shot; CallLocal so the shooter runs the same path.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	public void RpcFireEffects()
	{
		if (!IsAuthorizedEffectSender()) return;
		_attachment.PlayFire();
		_animation?.PlayFire();
		_viewmodel?.Play("fire");
		// Bots (host only) hear the shot: everyone nearby learns roughly where it came from.
		if (_currentWeapon != null && _currentWeapon.WeaponClass != "Melee")
			BotSenses.ReportGunshot(_player.GlobalPosition, Mathf.Min(_currentWeapon.FireMaxDistanceMeters, 80.0f), _player);
	}

	/// <summary>Reload audio and animation. Reliable, unlike fire: a dropped reload would leave a
	/// remote observer watching an idle pose for two seconds.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	public void RpcReloadEffects()
	{
		if (!IsAuthorizedEffectSender()) return;
		_attachment.PlayReload();
		_animation?.PlayReload();
		_viewmodel?.PlayReload(_currentWeapon?.ReloadTime ?? 0.0f, _currentAmmo == 0);
	}

	/// <summary>A shell-by-shell reload interrupted by firing: stop the reload sound on every peer.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	public void RpcCancelReloadEffects()
	{
		if (IsAuthorizedEffectSender()) _attachment.StopReload();
	}

	private bool IsAuthorizedEffectSender()
	{
		long sender = Multiplayer.GetRemoteSenderId();
		return sender == 0 || sender == _player.GetMultiplayerAuthority();
	}

	/// <summary>
	/// Recoil is applied to the Head pivot, not just to the visible camera, so it moves the actual
	/// point of aim - the hitscan ray comes off the camera. It decays back to zero over
	/// RecoilRecoveryMs, which is what makes a long burst walk upward and then settle.
	/// </summary>
	private void ApplyRecoil()
	{
		if (_currentWeapon == null) return;
		float horizontal = _rng.RandfRange(-_currentWeapon.RecoilHorizontal, _currentWeapon.RecoilHorizontal);
		// Aiming down sights cuts the kick roughly in half, which is the usual reason to ADS at all.
		float multiplier = _movement.IsAiming ? 0.5f : 1.0f;
		// Operator kit (e.g. a steady-hands buff); 1.0 when no operator or no such effect.
		multiplier *= _loadout?.GetModifier(CharacterEffectIds.WeaponRecoilMult, 1.0f) ?? 1.0f;
		_movement.AddRecoil(_currentWeapon.RecoilVertical * multiplier, horizontal * multiplier,
			_currentWeapon.RecoilRecoveryMs);
	}

	private void FireHitscan(float spreadDegrees)
	{
		bool melee = _currentWeapon!.WeaponClass == "Melee";
		float range = melee ? MeleeRange : MaxRange;
		Vector3 from = _camera.GlobalPosition;
		Vector3 direction = -_camera.GlobalTransform.Basis.Z;
		if (spreadDegrees > 0.0f) direction = ApplySpread(direction, spreadDegrees);
		Vector3 to = from + direction * range;

		var query = PhysicsRayQueryParameters3D.Create(from, to,
			exclude: new Godot.Collections.Array<Rid> { _player.GetRid() });
		var result = _player.GetWorld3D().DirectSpaceState.IntersectRay(query);
		bool hitSomething = result.Count > 0;
		Vector3 impactPoint = hitSomething ? (Vector3)result["position"] : to;
		if (hitSomething && result["collider"].As<Node>() is IShootable shootable)
		{
			HandleShootableHit(shootable, impactPoint, from, direction, melee);
			return;
		}

		// One message per single-projectile shot, so anyone the shot passes close to hears it go by
		// (a bullet's whip-crack, or a rocket's doppler whoosh). Skipped for a shotgun's pellets -
		// eight of these per pull would be a wall of noise - and for melee.
		if (!melee && _currentWeapon.PelletsPerShot <= 1)
		{
			bool rocket = IsRocket(_currentWeapon);
			Rpc(MethodName.RpcShotPath, from, impactPoint, rocket);
			// Rpc() never runs on the machine that sent it, and a bot is fired on the host - which is
			// also where a human may be listening - so a bot's shot is handed to that listener directly.
			if (PlayerIdentity.IsBot(_player)) HearShotPath(from, impactPoint, rocket);
		}

		if (!hitSomething) return;

		Node? collider = result["collider"].As<Node>();
		if (collider is not CharacterBody3D body)
		{
			// Geometry hit: no damage, but the impact is what tells the shooter where the round
			// actually went, which matters a lot once spread is in play.
			ReportGeometryImpact(melee, impactPoint, (Vector3)result["normal"], direction, collider);
			return;
		}

		var targetHealth = body.GetNodeOrNull<Health>("Health");
		if (targetHealth == null || targetHealth == _health) return;

		// Head zone first: it decides both the damage (FR-WP-05 uses the weapon's own stat) and the
		// headshot style bonus.
		bool headshot = IsHeadZoneHit(body, impactPoint);
		float damage = headshot && _currentWeapon.HeadshotDamage > 0.0f
			? _currentWeapon.HeadshotDamage
			: _currentWeapon.BodyDamage;
		KillStyle styles = CaptureKillStyle(headshot, melee);

		// Identity, not authority: a bot shares the host's authority but has its own id.
		long attackerId = PlayerIdentity.IdOf(_player);
		int targetId = targetHealth.GetMultiplayerAuthority();
		// Career stats: only the person at this machine, and only a hit on someone still alive. Read
		// before the damage is sent so the victim's health is still what it was when the bullet landed.
		if (!targetHealth.IsDead && PlayerIdentity.IsLocalHuman(_player))
			CareerTracker.Instance?.NoteHit(PlayerIdentity.IdOf(body), _currentWeapon.WeaponName,
				from.DistanceTo(impactPoint), _currentAmmo, _currentWeapon.MagazineSize, _shotSerial,
				targetHealth.CurrentHealth, targetHealth.MaxHealth);
		if (targetId == Multiplayer.GetUniqueId())
		{
			// The victim lives on THIS machine (host player or a bot): an RPC to yourself is a no-op,
			// so apply the damage directly.
			targetHealth.ReceiveDamage(damage, attackerId, (int)styles, KillStyleBonus.DefaultBaseKillPoints);
		}
		else
		{
			targetHealth.RpcId(targetId, Health.MethodName.ReceiveDamage, damage, attackerId,
				(int)styles, KillStyleBonus.DefaultBaseKillPoints);
		}
		// Broadcast, not local-only: this runs on the shooter's device (client-authoritative
		// hitscan), but a hit landing is something everyone nearby should hear, not just the
		// person who pulled the trigger.
		if (_fleshSoundsThisShot < MaxImpactSoundsPerShot)
		{
			_fleshSoundsThisShot++;
			Rpc(MethodName.RpcFleshImpact, impactPoint);
		}
		PlayHitMarker();
	}

	private void HandleShootableHit(IShootable target, Vector3 impactPoint, Vector3 from,
		Vector3 direction, bool melee)
	{
		WeaponData weapon = _currentWeapon!;
		bool headshot = target.IsHeadHit(impactPoint);
		float damage = headshot && weapon.HeadshotDamage > 0.0f ? weapon.HeadshotDamage : weapon.BodyDamage;
		KillStyle styles = CaptureKillStyle(headshot, melee);
		target.ReceiveShot(new ShotInfo
		{
			Damage = damage, IsHeadshot = headshot, Styles = styles, HitPoint = impactPoint,
			Direction = direction, Distance = from.DistanceTo(impactPoint),
			AttackerPeerId = _player.GetMultiplayerAuthority(), WeaponName = weapon.WeaponName,
			WeaponClass = weapon.WeaponClass,
		});
		if (target.IsFleshTarget) Rpc(MethodName.RpcFleshImpact, impactPoint);
		else _attachment.PlayImpact(impactPoint);
		PlayHitMarker();
	}

	private static bool IsRocket(WeaponData weapon) => weapon.WeaponClass == "Launcher";

	/// <summary>A bullet hitting world geometry: work out what it hit (and whether it skips off),
	/// then tell every peer so they all hear the same sound. Decided here, on the shooter's device,
	/// because only it knows the shot's direction and the surface normal.</summary>
	private void ReportGeometryImpact(bool melee, Vector3 point, Vector3 normal, Vector3 direction, Node? collider)
	{
		if (_currentWeapon == null) return;
		// A knife swing into a wall is not a bullet hit - it keeps the weapon's own quiet local thud.
		if (melee)
		{
			_attachment.PlayImpact(point);
			return;
		}
		if (_impactSoundsThisShot >= MaxImpactSoundsPerShot) return;
		_impactSoundsThisShot++;

		SurfaceType surface = SurfaceClassifier.Classify(collider);
		bool ricochet = RollRicochet(surface, direction, normal);
		Rpc(MethodName.RpcGeometryImpact, point, (int)surface, ricochet);
	}

	/// <summary>Hard surfaces skip bullets, soft ones swallow them: metal and plain wall can ricochet
	/// (far more often on a glancing hit), wood and glass never do. A shotgun's pellets and the
	/// Bazooka never do either.</summary>
	private bool RollRicochet(SurfaceType surface, Vector3 direction, Vector3 normal)
	{
		if (_currentWeapon == null || _currentWeapon.PelletsPerShot > 1 ||
			!_currentWeapon.ImpactUsesSurfaceVariation) return false;
		if (surface == SurfaceType.Wood || surface == SurfaceType.Glass) return false;

		float incidence = Mathf.Abs(direction.Dot(normal)); // 1 = head-on, 0 = grazing
		float chance = incidence < GlancingIncidence
			? GlancingRicochetChance
			: (surface == SurfaceType.Metal ? HeadOnRicochetChanceMetal : HeadOnRicochetChanceGeneric);
		return _rng.Randf() < chance;
	}

	/// <summary>Broadcast from the shooter's geometry-hit result: every peer plays the surface
	/// sound (and ricochet, if one was rolled) at the impact point.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	public void RpcGeometryImpact(Vector3 worldPosition, int surface, bool ricochet)
	{
		if (!IsAuthorizedEffectSender() || surface < 0 || surface > (int)SurfaceType.Glass) return;
		_attachment.PlayGeometryImpact(worldPosition, (SurfaceType)surface, ricochet);
	}

	/// <summary>
	/// Runs on every peer EXCEPT the shooter (no CallLocal). If the shot's path passed within
	/// earshot of this device's listener - the active camera - play the flyby at the closest point on
	/// that path. The check is done here, on the listener's own device, so the shooter only has to
	/// send one small message per shot instead of working out who might be near.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	public void RpcShotPath(Vector3 from, Vector3 to, bool rocket)
	{
		if (IsAuthorizedEffectSender()) HearShotPath(from, to, rocket);
	}

	private void HearShotPath(Vector3 from, Vector3 to, bool rocket)
	{
		// Never hear your own shots. (IsLocalHuman, not raw authority: a bot is 'authority' on the
		// host too, and the host's human should hear the bot's shots go by.)
		if (PlayerIdentity.IsLocalHuman(_player)) return;
		Camera3D? listener = GetViewport().GetCamera3D();
		if (listener == null) return;

		Vector3 ear = listener.GlobalPosition;
		Vector3 path = to - from;
		float lengthSquared = path.LengthSquared();
		if (lengthSquared < 0.01f) return;

		// Where along the path is the listener nearest? Outside 0..1 the shot never went past them.
		float t = (ear - from).Dot(path) / lengthSquared;
		if (t <= 0.0f || t >= 1.0f) return;

		Vector3 closest = from + path * t;
		float radius = rocket ? RocketFlyByRadius : FlyByRadius;
		if (closest.DistanceTo(ear) > radius) return;
		if (from.DistanceTo(ear) < FlyByMinShooterDistance) return;
		if (to.DistanceTo(ear) < FlyByMinImpactDistance) return;

		_attachment.PlayFlyBy(closest, rocket);
	}

	/// <summary>The shared "hit a person" sound, broadcast from the shooter's hitscan result to
	/// every peer including itself.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	public void RpcFleshImpact(Vector3 worldPosition)
	{
		if (IsAuthorizedEffectSender()) _attachment.PlayFleshImpact(worldPosition);
	}

	/// <summary>Scatters a direction inside a cone. Uniform over the disc rather than over the
	/// angle, so a shotgun pattern does not clump in the middle.</summary>
	private Vector3 ApplySpread(Vector3 direction, float spreadDegrees)
	{
		float maxRadius = Mathf.Tan(Mathf.DegToRad(spreadDegrees));
		float radius = maxRadius * Mathf.Sqrt(_rng.Randf());
		float angle = _rng.RandfRange(0.0f, Mathf.Tau);

		Vector3 right = _camera.GlobalTransform.Basis.X;
		Vector3 up = _camera.GlobalTransform.Basis.Y;
		return (direction
			+ right * (radius * Mathf.Cos(angle))
			+ up * (radius * Mathf.Sin(angle))).Normalized();
	}

	/// <summary>True when the ray's impact point falls inside the head sphere of the target. Uses
	/// the live Head node, so crouched targets get the same test as standing ones.</summary>
	private static bool IsHeadZoneHit(Node3D target, Vector3 hitPoint)
	{
		var head = target.GetNodeOrNull<Node3D>("Head");
		return head != null && hitPoint.DistanceTo(head.GlobalPosition) <= HeadZoneRadius;
	}

	/// <summary>
	/// Everything the kill is scored from, read at the moment of firing (Part 6 calls these "simple
	/// state checks at the moment of a confirmed kill"; for hitscan fire, fire time is that moment).
	/// Melee is exempt from the ranged-only styles - a knife swing is never hip-fire or a no-scope.
	/// </summary>
	private KillStyle CaptureKillStyle(bool headshot, bool melee)
	{
		KillStyle styles = headshot ? KillStyle.Headshot : KillStyle.None;
		// Not scoring flags: they tell the victim's device how this death should sound - a blade
		// kill always makes a sound, a rocket kill never does (Health.DetermineIfDeathStruggles).
		if (melee) styles |= KillStyle.Melee;
		if (_currentWeapon != null && IsRocket(_currentWeapon)) styles |= KillStyle.Explosive;
		if (!melee)
		{
			// IsAiming, not "State == Aim". Aim stopped being a movement state when ADS was pulled
			// out of the state machine, so the old comparison read false whenever the player was
			// aiming while moving - handing out a hip-fire bonus for a careful ADS shot.
			bool aimingDownSights = _movement.IsAiming;
			if (!aimingDownSights) styles |= KillStyle.HipFire;
			// FR-SC-03: the spin is measured over the rolling window ending at this shot.
			if (_rotationWindow.HasFullSpin(aimingDownSights)) styles |= KillStyle.NoScope360;
		}

		if (_health != null && _health.MaxHealth > 0.0f &&
			_health.CurrentHealth <= _health.MaxHealth * KillStyleBonus.VergeOfDeathHealthFraction)
			styles |= KillStyle.VergeOfDeath;
		return styles;
	}

	// -------------------------------------------------------------------------------------------
	// Reloading
	// -------------------------------------------------------------------------------------------

	private void StartReload()
	{
		if (_currentWeapon == null || _reloadRemaining > 0.0f) return;
		bool infiniteAmmo = _currentWeapon.MagazineSize <= 0;
		if (infiniteAmmo || _currentAmmo >= _currentWeapon.MagazineSize || _currentReserve <= 0) return;
		_reloadRemaining = _currentWeapon.ReloadTime > 0.0f ? _currentWeapon.ReloadTime : 0.1f;
		ReloadStarted?.Invoke(_currentWeapon);
		Rpc(MethodName.RpcReloadEffects);
	}

	private void FinishReload()
	{
		_reloadRemaining = 0.0f;
		if (_currentWeapon == null) return;

		if (_currentWeapon.ShellByShellReload)
		{
			// One shell per cycle, then start the next one if the tube still has room and the
			// reserve still has a shell to feed it. ReloadTime is the per-shell time for these, so
			// a full Mossberg tube takes 6 x 0.5 s and can be cut short at any point by firing.
			if (_currentReserve <= 0) return;
			_currentAmmo++;
			_currentReserve--;
			if (_currentAmmo < _currentWeapon.MagazineSize && _currentReserve > 0) StartReload();
			return;
		}

		int needed = _currentWeapon.MagazineSize - _currentAmmo;
		int drawn = Mathf.Min(needed, _currentReserve);
		_currentAmmo += drawn;
		_currentReserve -= drawn;
	}

	// -------------------------------------------------------------------------------------------

	private static AudioStreamPlayer? BuildHitMarkerPlayer(Node parent, string path)
	{
		var stream = ResourceLoader.Load<AudioStream>(path);
		if (stream == null) return null;
		var player = new AudioStreamPlayer { Name = "HitMarker", Stream = stream, VolumeDb = -14.0f };
		parent.AddChild(player);
		return player;
	}

	/// <summary>Non-positional, owner-only: a hit marker is feedback about your own shot, so it
	/// should not be a 3D sound and no other peer should hear it.</summary>
	private AudioStreamPlayer? BuildHitMarker() =>
		BuildHitMarkerPlayer(this, HitMarkerSounds[0]);

	private void PlayHitMarker()
	{
		if (_hitMarker == null) return;
		var stream = ResourceLoader.Load<AudioStream>(HitMarkerSounds[_rng.RandiRange(0, HitMarkerSounds.Length - 1)]);
		if (stream != null) _hitMarker.Stream = stream;
		_hitMarker.Play();
	}

	/// <summary>Refills the current weapon (a bot's 'spawn loadout' on respawn).</summary>
	public void RestockAmmo()
	{
		if (_currentWeapon == null) return;
		_currentAmmo = _currentWeapon.MagazineSize;
		_currentReserve = _currentWeapon.MaxReserveAmmo;
		_reloadRemaining = 0.0f;
		_animation?.CancelReload();
	}
}
