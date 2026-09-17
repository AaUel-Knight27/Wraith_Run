using Godot;

/// <summary>
/// Temporary test loadout. Owns ammo, fire-rate, and reload state for the equipped weapon and
/// resolves hitscan fire against Health. Model attachment stays centralized in WeaponAttachment.
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

	/// <summary>Radius of the sphere around the Head node that counts as a head hit. The player rig is
	/// a single capsule, so the head zone is a geometric test against the live Head position rather
	/// than a separate hitbox - which also means it tracks crouching for free.</summary>
	private const float HeadZoneRadius = 0.25f;

	private static readonly string[] Loadout = { "AK-47", "Shotgun", "Glock 19", "Karambit" };

	private WeaponAttachment _attachment = null!;
	private WeaponRegistry _registry = null!;
	private Camera3D _camera = null!;
	private CharacterBody3D _player = null!;
	private PlayerMovement _movement = null!;
	private RotationWindowTracker _rotationWindow = null!;
	private Health? _health;

	private WeaponData? _currentWeapon;
	private int _currentAmmo;
	private float _cooldownRemaining;
	private float _reloadRemaining;

		// Read-only surface for the HUD. Kept separate from the fields above so nothing outside
		// this script can mutate combat state directly.
		public string WeaponName => _currentWeapon?.WeaponName ?? string.Empty;
		public int CurrentAmmo => _currentAmmo;
		public int MagazineSize => _currentWeapon?.MagazineSize ?? 0;
		public bool IsReloading => _reloadRemaining > 0.0f;
		public WeaponData.FireModeType FireMode => _currentWeapon?.FireMode ?? WeaponData.FireModeType.FullAuto;

	public override void _Ready()
	{
		_player = (CharacterBody3D)GetParent();
		_movement = (PlayerMovement)GetParent();
		_attachment = GetNode<WeaponAttachment>("../Armature/WeaponAttachment");
		_camera = GetNode<Camera3D>("../Head/FirstPersonCamera");
		_rotationWindow = GetNode<RotationWindowTracker>("../RotationWindow");
		_health = GetNodeOrNull<Health>("../Health");
		_registry = ResourceLoader.Load<WeaponRegistry>("res://scripts/data/WeaponRegistry.tres")!;
		Equip(0);
	}

	public override void _Process(double delta)
	{
		if (!_player.IsMultiplayerAuthority()) return;

		for (int i = 0; i < Loadout.Length; i++)
			if (Input.IsActionJustPressed($"weapon_{i + 1}")) Equip(i);

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
		// waits for the cooldown to drain without firing again, which is the behaviour the
		// TODO here was asking for. Full-auto keeps firing on hold.
		bool fireRequested = FireMode == WeaponData.FireModeType.FullAuto
			? Input.IsActionPressed("fire")
			: Input.IsActionJustPressed("fire");
		if (fireRequested && _cooldownRemaining <= 0.0f && _reloadRemaining <= 0.0f)
			TryFire();
	}

	private void Equip(int index)
	{
		foreach (var weapon in _registry.Weapons)
		{
			if (weapon.WeaponName != Loadout[index]) continue;
			_currentWeapon = weapon;
			_currentAmmo = weapon.MagazineSize;
			_cooldownRemaining = 0.0f;
			_reloadRemaining = 0.0f;
			_attachment.Equip(weapon);
			return;
		}
		GD.PushError($"Test loadout weapon '{Loadout[index]}' is not registered.");
	}

	private void TryFire()
	{
		if (_currentWeapon == null) return;
		bool infiniteAmmo = _currentWeapon.MagazineSize <= 0;
		if (!infiniteAmmo && _currentAmmo <= 0) { StartReload(); return; }

		_cooldownRemaining = _currentWeapon.FireRateRPM > 0.0f ? 60.0f / _currentWeapon.FireRateRPM : 0.5f;
		if (!infiniteAmmo) _currentAmmo--;
		_attachment.PlayFire();
		FireHitscan();
	}

	private void FireHitscan()
	{
		bool melee = _currentWeapon!.WeaponClass == "Melee";
		float range = melee ? MeleeRange : MaxRange;
		Vector3 from = _camera.GlobalPosition;
		Vector3 to = from + (-_camera.GlobalTransform.Basis.Z) * range;

		var query = PhysicsRayQueryParameters3D.Create(from, to,
			exclude: new Godot.Collections.Array<Rid> { _player.GetRid() });
		var result = _player.GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (result.Count == 0) return;

		if (result["collider"].As<Node>() is not CharacterBody3D body) return;
		var targetHealth = body.GetNodeOrNull<Health>("Health");
		if (targetHealth == null || targetHealth == _health) return;

		// Head zone first: it decides both the damage (FR-WP-05 uses the weapon's own stat, and
		// HeadshotDamage finally gets used instead of body damage for every hit) and the headshot
		// style bonus.
		bool headshot = IsHeadZoneHit(body, (Vector3)result["position"]);
		float damage = headshot && _currentWeapon.HeadshotDamage > 0.0f
			? _currentWeapon.HeadshotDamage
			: _currentWeapon.BodyDamage;
		KillStyle styles = CaptureKillStyle(headshot, melee);

		int attackerId = _player.GetMultiplayerAuthority();
		int targetId = targetHealth.GetMultiplayerAuthority();
		targetHealth.RpcId(targetId, Health.MethodName.ReceiveDamage, damage, (long)attackerId,
			(int)styles, KillStyleBonus.DefaultBaseKillPoints);
	}

	/// <summary>True when the ray's impact point falls inside the head sphere of the target. Uses the
	/// live Head node, so crouched targets get the same honest test as standing ones.</summary>
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
			bool aimingDownSights = _movement.State == PlayerMovement.MovementState.Aim;
			if (!aimingDownSights) styles |= KillStyle.HipFire;
			// FR-SC-03: the spin is measured over the rolling window ending at this shot.
			if (_rotationWindow.HasFullSpin(aimingDownSights)) styles |= KillStyle.NoScope360;
		}

		if (_health != null && _health.MaxHealth > 0.0f &&
			_health.CurrentHealth <= _health.MaxHealth * KillStyleBonus.VergeOfDeathHealthFraction)
			styles |= KillStyle.VergeOfDeath;
		return styles;
	}

	private void StartReload()
	{
		if (_currentWeapon == null || _reloadRemaining > 0.0f) return;
		bool infiniteAmmo = _currentWeapon.MagazineSize <= 0;
		if (infiniteAmmo || _currentAmmo >= _currentWeapon.MagazineSize) return;
		_reloadRemaining = _currentWeapon.ReloadTime > 0.0f ? _currentWeapon.ReloadTime : 0.1f;
		_attachment.PlayReload();
	}

	private void FinishReload()
	{
		if (_currentWeapon != null) _currentAmmo = _currentWeapon.MagazineSize;
		_reloadRemaining = 0.0f;
	}
}
