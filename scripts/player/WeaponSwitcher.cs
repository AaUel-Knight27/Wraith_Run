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

	/// <summary>Every live player is added to this group in PlayerMovement._Ready() - the same
	/// pattern GameWorld's own "spawn_point" group already uses. Lets a hitscan shot find every
	/// OTHER player on the map without the map or the match system having to hand it a list.</summary>
	private const string PlayersGroup = "players";

	/// <summary>How close (metres, perpendicular distance) a shot has to pass a bystander's head to
	/// read as a "whiz by" rather than just an unheard miss.</summary>
	private const float FlybyMissRadius = 1.4f;

	/// <summary>Skip the flyby check within this distance of the muzzle - a near-miss at
	/// point-blank doesn't read as a "whiz", it reads as barely missing at all, and it would
	/// otherwise fire constantly in a close-range duel.</summary>
	private const float FlybyMinDistanceFromShooter = 2.5f;

	/// <summary>Radius of the sphere around the Head node that counts as a head hit. The player rig
	/// is a single capsule, so the head zone is a geometric test against the live Head position
	/// rather than a separate hitbox.</summary>
	private const float HeadZoneRadius = 0.25f;

	// Kriss Vector excluded: no model until its mesh is re-sourced (lost in FBX->GLB conversion).
	// Internal rather than private: TouchControls' weapon-switch/quick-melee buttons read this
	// directly (length + Karambit's index) instead of hardcoding numbers that would silently go
	// stale if this list is ever reordered or extended.
	internal static readonly string[] Loadout =
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
	private WeaponAttachment? _viewmodelAttachment;
	private FirstPersonMp5Viewmodel? _viewmodel;
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

	private WeaponData? _currentWeapon;
	private int _currentAmmo;
	private int _currentReserve;
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

	/// <summary>Rounds left in reserve, beyond what's loaded. Always 0 for a melee weapon.</summary>
	public int CurrentReserve => _currentReserve;
	public bool IsReloading => _reloadRemaining > 0.0f;
	public WeaponData.FireModeType FireMode => _currentWeapon?.FireMode ?? WeaponData.FireModeType.FullAuto;

	public override void _Ready()
	{
		_rng.Randomize();
		_player = (CharacterBody3D)GetParent();
		_movement = (PlayerMovement)GetParent();
		_attachment = GetNode<WeaponAttachment>("../Armature/WeaponAttachment");
		// Both optional: a scene that hasn't picked up the Player.tscn viewmodel changes (see
		// MP5_VIEWMODEL_README.md) still runs exactly as before - GetNodeOrNull rather than GetNode,
		// same as _animation below, and every call site already null-conditions on it.
		_viewmodelAttachment = GetNodeOrNull<WeaponAttachment>("../Head/FirstPersonCamera/FirstPersonMp5Viewmodel/WeaponAttachment");
		_viewmodel = GetNodeOrNull<FirstPersonMp5Viewmodel>("../Head/FirstPersonCamera/FirstPersonMp5Viewmodel");
		_camera = GetNode<Camera3D>("../Head/FirstPersonCamera");
		_rotationWindow = GetNode<RotationWindowTracker>("../RotationWindow");
		_health = GetNodeOrNull<Health>("../Health");
		_loadout = GetNodeOrNull<CharacterLoadout>("../CharacterLoadout");
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

		// Controller-friendly cycling (shoulder buttons - see SettingsManager's controller
		// bindings). Nothing on keyboard/mouse drives these: a controller is the only input
		// device without enough face buttons for eight direct weapon_N presses.
		if (Input.IsActionJustPressed("weapon_cycle_next"))
			ReplicatedWeaponIndex = (_appliedWeaponIndex + 1) % Loadout.Length;
		if (Input.IsActionJustPressed("weapon_cycle_prev"))
			ReplicatedWeaponIndex = (_appliedWeaponIndex - 1 + Loadout.Length) % Loadout.Length;

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
			_currentReserve = weapon.MaxReserveAmmo;
			_cooldownRemaining = 0.0f;
			_reloadRemaining = 0.0f;
			_animation?.CancelReload();
			_attachment.Equip(weapon);
			_viewmodelAttachment?.Equip(weapon);
			_viewmodel?.PlayEquip();
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
		_viewmodelAttachment?.PlayFire();
		_viewmodel?.PlayShoot();
		_animation?.PlayFire();
	}

	/// <summary>Reload audio and animation. Reliable, unlike fire: a dropped reload would leave a
	/// remote observer watching an idle pose for two seconds.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
		TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	public void RpcReloadEffects()
	{
		_attachment.PlayReload();
		_viewmodelAttachment?.PlayReload();
		_viewmodel?.PlayReload();
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
		// Operator recoil perks (CharacterEffectIds.WeaponRecoilMult). No operator equipped, or the
		// node missing entirely -> GetModifier returns 1.0 -> unchanged.
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

		Node? hitCollider = result.Count > 0 ? result["collider"].As<Node>() : null;
		Vector3 rayEnd = result.Count > 0 ? (Vector3)result["position"] : to;
		// A blade has no "whiz by" - only a fired round does. Checked against the ray's actual end
		// (the impact point when something was hit, otherwise the full range) so a clean miss can
		// still buzz a bystander standing beyond whatever the shot eventually hit, if anything.
		if (!melee) CheckBulletFlybys(from, rayEnd, hitCollider);

		if (result.Count == 0) return;

		var impactPoint = rayEnd;

		if (hitCollider is not CharacterBody3D body)
		{
			// Geometry hit: no damage, but the impact is what tells the shooter where the round
			// actually went, which matters a lot once spread is in play. Broadcast, like the flesh
			// impact below - a bullet cracking into a wall or a shipping container near someone is
			// something they should hear too, not just the shooter.
			Rpc(MethodName.RpcSurfaceImpact, impactPoint, (int)SurfaceClassifier.Classify(hitCollider));
			return;
		}

		var targetHealth = body.GetNodeOrNull<Health>("Health");
		if (targetHealth == null || targetHealth == _health) return;
		if (IsFriendlyTarget(targetHealth)) return;

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

	/// <summary>True when Friendly Fire is off and the hit target is a Team Deathmatch teammate.
	/// This is a client-side courtesy skip only, so a friendly hit gives no hit-marker feedback -
	/// Health.ReceiveDamage enforces the same rule again on the victim's own authoritative device,
	/// which is the check that actually matters.</summary>
	private bool IsFriendlyTarget(Health targetHealth)
	{
		MatchManager? match = MatchManager.Instance;
		if (match == null || match.FriendlyFire) return false;
		return match.AreTeammates(_player.GetMultiplayerAuthority(), targetHealth.GetMultiplayerAuthority());
	}

	/// <summary>The shared "hit a person" sound, broadcast from the shooter's hitscan result to
	/// every peer including itself.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	public void RpcFleshImpact(Vector3 worldPosition) => _attachment.PlayFleshImpact(worldPosition);

	/// <summary>A bullet hitting geometry (not a person) - surfaceType is a cast SurfaceType, sent
	/// as an int the same way styleFlags already rides a KillStyle through Health.ReceiveDamage's
	/// RPC. Broadcast for the same reason RpcFleshImpact is: everyone nearby should hear a shot
	/// crack into a wall or a shipping container, not just the shooter.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	public void RpcSurfaceImpact(Vector3 worldPosition, int surfaceType) =>
		_attachment.PlayImpact(worldPosition, (SurfaceType)surfaceType);

	/// <summary>A shot that passed close to (but did not hit) this bystander. Broadcast rather than
	/// targeted at just the near-missed player - it is a positional one-shot like every other impact
	/// sound here, so ordinary 3D falloff already makes it loud for whoever it actually passed and
	/// faint or inaudible for everyone else.</summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true,
		TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	public void RpcBulletFlyby(Vector3 worldPosition) => _attachment.PlayBulletFlyby(worldPosition);

	/// <summary>Checks every other living player against the shot's actual path (from the muzzle to
	/// wherever it ended up - an impact point, or the full range on a clean miss) and broadcasts a
	/// flyby one-shot near anyone the ray passed close to. hitCollider is whoever/whatever the shot
	/// actually landed on, if anything - excluded here so the player who was actually hit gets the
	/// impact/hit-marker feedback instead of a redundant "near miss" whiz.</summary>
	private void CheckBulletFlybys(Vector3 from, Vector3 rayEnd, Node? hitCollider)
	{
		foreach (Node node in GetTree().GetNodesInGroup(PlayersGroup))
		{
			if (node is not CharacterBody3D otherPlayer || otherPlayer == _player) continue;
			if (hitCollider == otherPlayer) continue;

			var otherHealth = otherPlayer.GetNodeOrNull<Health>("Health");
			if (otherHealth == null || otherHealth.IsDead) continue;

			// Rough head height rather than the capsule's origin (feet) - a shot passing at ankle
			// height twenty centimetres away should not "whiz" past someone's ear.
			Vector3 ear = otherPlayer.GlobalPosition + Vector3.Up * 1.5f;
			Vector3 closest = ClosestPointOnSegment(from, rayEnd, ear);
			if ((closest - from).Length() < FlybyMinDistanceFromShooter) continue;
			if ((closest - ear).Length() > FlybyMissRadius) continue;

			Rpc(MethodName.RpcBulletFlyby, closest);
		}
	}

	/// <summary>Closest point on segment a-b to point p, clamped to the segment (not the infinite
	/// line) - a shot's near-miss check should never register past where the round actually stops.</summary>
	private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
	{
		Vector3 ab = b - a;
		float lengthSquared = ab.LengthSquared();
		if (lengthSquared < 0.0001f) return a;
		float t = Mathf.Clamp((p - a).Dot(ab) / lengthSquared, 0.0f, 1.0f);
		return a + ab * t;
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
		if (melee) styles |= KillStyle.Melee;
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
		var player = new AudioStreamPlayer { Name = "HitMarker", Stream = stream, VolumeDb = -4.0f, Bus = "UI" };
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
