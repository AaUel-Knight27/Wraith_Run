using Godot;

/// <summary>
/// Temporary test loadout. Owns ammo, fire-rate, and reload state for the equipped weapon and
/// resolves hitscan fire against Health. Model attachment stays centralized in WeaponAttachment.
/// </summary>
public partial class WeaponSwitcher : Node
{
	private const float MaxRange = 500.0f;
	private const float MeleeRange = 2.5f;

	private static readonly string[] Loadout = { "AK-47", "Shotgun", "Glock 19", "Karambit" };

	private WeaponAttachment _attachment = null!;
	private WeaponRegistry _registry = null!;
	private Camera3D _camera = null!;
	private CharacterBody3D _player = null!;
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

	public override void _Ready()
	{
		_player = (CharacterBody3D)GetParent();
		_attachment = GetNode<WeaponAttachment>("../Armature/WeaponAttachment");
		_camera = GetNode<Camera3D>("../Head/FirstPersonCamera");
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
		// Rate-limited by FireRateRPM below. Semi-auto weapons (Glock, Desert Eagle) should
		// realistically require a fresh press per shot - not enforced yet, so holding fire
		// will currently spam them at their listed cadence. Revisit alongside recoil.
		if (Input.IsActionPressed("fire") && _cooldownRemaining <= 0.0f && _reloadRemaining <= 0.0f)
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

		// TODO: distinguish head vs. body hits once a dedicated head hitbox exists on the rig;
		// every hit currently counts as BodyDamage, and HeadshotDamage goes unused.
		if (result["collider"].As<Node>() is not CharacterBody3D body) return;
		var targetHealth = body.GetNodeOrNull<Health>("Health");
		if (targetHealth == null || targetHealth == _health) return;

		int attackerId = _player.GetMultiplayerAuthority();
		int targetId = targetHealth.GetMultiplayerAuthority();
		targetHealth.RpcId(targetId, Health.MethodName.ReceiveDamage, _currentWeapon.BodyDamage, attackerId);
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
