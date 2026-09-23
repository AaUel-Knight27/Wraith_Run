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

	// Kriss Vector excluded: no model until its mesh is re-sourced (lost in FBX->GLB conversion).
	private static readonly string[] Loadout =
		{ "AK-47", "M4", "P90", "Glock 19", "Desert Eagle", "Shotgun", "Karambit", "Bazooka" };

	private static readonly string[] HitMarkerSounds =
	{
		"res://audio/Clips/Target Hit/TargetHit-001.wav",
		"res://audio/Clips/Target Hit/TargetHit-002.wav",
		"res://audio/Clips/Target Hit/TargetHit-003.wav",
		"res://audio/Clips/Target Hit/TargetHit-004.wav",
		"res://audio/Clips/Target Hit/TargetHit-005.wav",
	};

	private WeaponAttachment _attachment = null!;
	private WeaponRegistry _registry = null!;
	private Camera3D _camera = null!;
	private CharacterBody3D _player = null!;
	private PlayerMovement _movement = null!;
	private PlayerAnimationController? _animation;
	private RotationWindowTracker _rotationWindow = null!;
	private Health? _health;
	private AudioStreamPlayer? _hitMarker;
	private readonly RandomNumberGenerator _rng = new();

	private WeaponData? _currentWeapon;
	private int _currentAmmo;
	private float _cooldownRemaining;
	private float _reloadRemaining;
	private int _appliedWeaponIndex = -1;

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
	public bool IsReloading => _reloadRemaining > 0.0f;
	public WeaponData.FireModeType FireMode => _currentWeapon?.FireMode ?? WeaponData.FireModeType.FullAuto;

	public override void _Ready()
	{
		_rng.Randomize();
		_player = (CharacterBody3D)GetParent();
		_movement = (PlayerMovement)GetParent();
		_attachment = GetNode<WeaponAttachment>("../Armature/WeaponAttachment");
		_camera = GetNode<Camera3D>("../Head/FirstPersonCamera");
		_rotationWindow = GetNode<RotationWindowTracker>("../RotationWindow");
		_health = GetNodeOrNull<Health>("../Health");
		_animation = GetNodeOrNull<PlayerAnimationController>("../AnimationController");
		_registry = ResourceLoader.Load<WeaponRegistry>("res://scripts/data/WeaponRegistry.tres")!;

		if (_player.IsMultiplayerAuthority()) _hitMarker = BuildHitMarker();

		// Apply whatever index is already set. On the authority that is 0 (the AK); on a remote
		// copy the synchronizer may already have delivered the real value.
		ApplyWeaponIndex(_replicatedWeaponIndex);
	}

	public override void _Process(double delta)
	{
		if (!_player.IsMultiplayerAuthority()) return;

		for (int i = 0; i < Loadout.Length; i++)
			if (Input.IsActionJustPressed($"weapon_{i + 1}")) ReplicatedWeaponIndex = i;

		if (_health != null && _health.IsDead) return;

		float step = (float)delta;
		_cooldownRemaining = Mathf.Max(0.0f, _cooldownRemaining - step);
		if (_reloadRemaining > 0.0f)
		{
			_reloadRemaining -= step;
			if (_reloadRemaining <= 0.0f) FinishReload();
		}

		if (Input.IsActionJustPressed("reload")) StartReload();

		// Rate-limited by FireRateRPM below. Semi-auto weapons (Glock, Desert Eagle, shotgun)
		// require a fresh press per shot - holding the trigger on them fires one round and then
		// waits for the cooldown to drain without firing again. Full-auto keeps firing on hold.
		bool fireRequested = FireMode == WeaponData.FireModeType.FullAuto
			? Input.IsActionPressed("fire")
			: Input.IsActionJustPressed("fire");
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
			_appliedWeaponIndex = index;
			_currentWeapon = weapon;
			_currentAmmo = weapon.MagazineSize;
			_cooldownRemaining = 0.0f;
			_reloadRemaining = 0.0f;
			_animation?.CancelReload();
			_attachment.Equip(weapon);
			return;
		}
		GD.PushError($"Test loadout weapon '{Loadout[index]}' is not registered.");
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
			// spam. Only auto-reload if a reload is not already running.
			_attachment.PlayEmpty();
			_cooldownRemaining = 0.35f;
			if (_reloadRemaining <= 0.0f) StartReload();
			return;
		}

		// A shell-by-shell reload is interruptible - that is the point of it. Firing mid-tube keeps
		// whatever shells are already loaded.
		if (_reloadRemaining > 0.0f)
		{
			if (!_currentWeapon.ShellByShellReload) return;
			_reloadRemaining = 0.0f;
			_animation?.CancelReload();
		}

		_cooldownRemaining = _currentWeapon.FireRateRPM > 0.0f ? 60.0f / _currentWeapon.FireRateRPM : 0.5f;
		if (!infiniteAmmo) _currentAmmo--;

		Rpc(MethodName.RpcFireEffects);
		ApplyRecoil();

		int pellets = Mathf.Max(1, _currentWeapon.PelletsPerShot);
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
		_attachment.PlayFire();
		_animation?.PlayFire();
	}

	/// <summary>Reload audio and animation. Reliable, unlike fire: a dropped reload would leave a
	/// remote observer watching an idle pose for two seconds.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	public void RpcReloadEffects()
	{
		_attachment.PlayReload();
		_animation?.PlayReload();
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
		if (result.Count == 0) return;

		var impactPoint = (Vector3)result["position"];

		if (result["collider"].As<Node>() is not CharacterBody3D body)
		{
			// Geometry hit: no damage, but the impact is what tells the shooter where the round
			// actually went, which matters a lot once spread is in play.
			_attachment.PlayImpact(impactPoint);
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

		int attackerId = _player.GetMultiplayerAuthority();
		int targetId = targetHealth.GetMultiplayerAuthority();
		targetHealth.RpcId(targetId, Health.MethodName.ReceiveDamage, damage, (long)attackerId,
			(int)styles, KillStyleBonus.DefaultBaseKillPoints);
		// Broadcast, not local-only: this runs on the shooter's device (client-authoritative
		// hitscan), but a hit landing is something everyone nearby should hear, not just the
		// person who pulled the trigger.
		Rpc(MethodName.RpcFleshImpact, impactPoint);
		PlayHitMarker();
	}

	/// <summary>The shared "hit a person" sound, broadcast from the shooter's hitscan result to
	/// every peer including itself.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	public void RpcFleshImpact(Vector3 worldPosition) => _attachment.PlayFleshImpact(worldPosition);

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
		if (infiniteAmmo || _currentAmmo >= _currentWeapon.MagazineSize) return;
		_reloadRemaining = _currentWeapon.ReloadTime > 0.0f ? _currentWeapon.ReloadTime : 0.1f;
		Rpc(MethodName.RpcReloadEffects);
	}

	private void FinishReload()
	{
		_reloadRemaining = 0.0f;
		if (_currentWeapon == null) return;

		if (_currentWeapon.ShellByShellReload)
		{
			// One shell per cycle, then start the next one if the tube still has room. ReloadTime
			// is the per-shell time for these, so a full Mossberg tube takes 6 x 0.5 s and can be
			// cut short at any point by pulling the trigger.
			_currentAmmo = Mathf.Min(_currentAmmo + 1, _currentWeapon.MagazineSize);
			if (_currentAmmo < _currentWeapon.MagazineSize) StartReload();
			return;
		}
		_currentAmmo = _currentWeapon.MagazineSize;
	}

	// -------------------------------------------------------------------------------------------

	private static AudioStreamPlayer? BuildHitMarkerPlayer(Node parent, string path)
	{
		var stream = ResourceLoader.Load<AudioStream>(path);
		if (stream == null) return null;
		var player = new AudioStreamPlayer { Name = "HitMarker", Stream = stream, VolumeDb = -4.0f };
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
}
