using Godot;
using System.Collections.Generic;

/// <summary>
/// Networked health and death/respawn. Lives on the owning peer's Player instance.
/// CurrentHealth is replicated outward by PlayerNetworkSynchronizer, so every peer's copy of
/// this node ends up running the same setter - that's what actually disables the hitbox and
/// plays the death animation on remote clients, not just the local owner.
///
/// It is also the authoritative end of the kill path: the HP deduction below only ever runs on the
/// owning device, which makes that device the single legitimate publisher of its own death. The
/// style flags that decide what a kill is worth (headshot / hip-fire / spin / verge-of-death) are
/// captured by the attacker as it fires and travel with the damage RPC, so scoring here stays a
/// pure calculation over data the victim's device can trust.
/// </summary>
public partial class Health : Node
{
    [Signal] public delegate void DiedEventHandler();
    [Signal] public delegate void RespawnedEventHandler();
    /// <summary>Fired on the owning peer after a hit lands (used by bots to know they were shot and by whom).</summary>
    [Signal] public delegate void DamagedEventHandler(float amount, long attackerId);

    /// <summary>FR-PL-07 requires the respawn delay to sit between 3 and 5 seconds.</summary>
    private const float RespawnDelaySeconds = 3.0f;

    /// <summary>
    /// Team Deathmatch only: a kill that was NOT instant (see DetermineIfDeathStruggles) leaves the
    /// player calling for a medic for this long before they are eliminated. Public because
    /// PlayerAudio runs the same countdown on every peer to time the "I need a medic" calls and the
    /// final struggle - both sides read this one number so they can never drift apart. The normal
    /// respawn delay above starts counting once this window is over.
    /// </summary>
    public const float TeamBleedOutSeconds = 20.0f;

    /// <summary>
    /// Half-extent of the square area a Free-For-All respawn is drawn from (see
    /// RespawnPointSelector.SelectAnywhere). Deliberately matched to the current 60x60 Warzone arena -
    /// widen it together with the map. Team Deathmatch ignores this and uses the respawning player's
    /// own TeamSpawnArea instead. Also used as the fallback half-extent for a bot respawn with no nav
    /// grid built yet (see BotNav.PickRespawnPosition).
    /// </summary>
    [Export] public float SpawnAreaHalfExtent { get; set; } = RespawnPointSelector.DefaultMapHalfExtent;

    [Export] public float MaxHealth { get; set; } = 100.0f;

    // Only the owning peer's ReceiveDamage call ever writes this directly. Everyone else's
    // copy receives it through replication and just reacts in the setter below.
    [Export]
    public float CurrentHealth
    {
        get => _currentHealth;
        set
        {
            bool wasAlive = _currentHealth > 0.0f || !_hasInitialized;
            _hasInitialized = true;
            _currentHealth = Mathf.Clamp(value, 0.0f, MaxHealth);
            ApplyVisualState(wasAlive, _currentHealth > 0.0f);
            if (wasAlive && _currentHealth <= 0.0f) EmitSignal(SignalName.Died);
        }
    }

    public bool IsDead => _currentHealth <= 0.0f;
    public float RespawnTimeRemaining => _respawnTimer;

    private float _currentHealth;
    private bool _hasInitialized;
    private float _respawnTimer;
    private CharacterBody3D _player = null!;
    private CollisionShape3D? _collision;
    private PlayerAnimationController? _animation;
    private CharacterLoadout? _loadout;
    private KillFeed? _killFeed;

    /// <summary>Damage each attacker has dealt since this player last spawned. Part 6 pays the assist
    /// to the non-killing damager, so this ledger has to outlive a single hit.</summary>
    private readonly Dictionary<long, float> _damageContribution = new();
    private readonly RandomNumberGenerator _botRng = new();

    private Vector3 _deathPosition;
    private bool _deathAnnounced;

    public override void _Ready()
    {
        _player = (CharacterBody3D)GetParent();
        _collision = _player.GetNodeOrNull<CollisionShape3D>("CollisionShape3D");
        _animation = _player.GetNodeOrNull<PlayerAnimationController>("AnimationController");
        _loadout = _player.GetNodeOrNull<CharacterLoadout>("CharacterLoadout");
        _killFeed = KillFeed.From(this);
        _botRng.Randomize();

        // Shiv's "On-foot max HP -10%" drawback and anything like it (CharacterEffectIds.
        // OnFootMaxHealthMult). No operator equipped -> GetModifier returns 1.0 -> unchanged.
        MaxHealth *= _loadout?.GetModifier(CharacterEffectIds.OnFootMaxHealthMult, 1.0f) ?? 1.0f;
        CurrentHealth = MaxHealth;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!IsMultiplayerAuthority() || !IsDead) return;
        _respawnTimer -= (float)delta;
        if (_respawnTimer <= 0.0f) Respawn();
    }

    /// <summary>
    /// Called by an attacker's client via RpcId targeted at this player's owning peer. Mirrors
    /// the client-authoritative model PlayerMovement already uses for position: this only takes
    /// effect on the instance that actually owns the node, never on a remote observer's copy.
    ///
    /// styleFlags and basePoints are the attacker's read of the shot (head zone, aim state, spin
    /// window, the shooter's own HP) and the mode's base kill value. They ride along with the damage
    /// because only the shooter can observe them, and they are what the kill is scored from here.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveDamage(float amount, long attackerId, int styleFlags, int basePoints)
    {
        if (!IsMultiplayerAuthority() || IsDead || amount <= 0.0f) return;
        if (IsFriendlyFire(attackerId)) return;
        _damageContribution[attackerId] = GetContribution(attackerId) + amount;

        CurrentHealth -= amount;
        EmitSignal(SignalName.Damaged, amount, attackerId);
        if (!IsDead) return;

        _deathPosition = _player.GlobalPosition;
        bool struggles = DetermineIfDeathStruggles((KillStyle)styleFlags);
        // Team Deathmatch: a non-instant death is a bleed-out - medic calls for
        // TeamBleedOutSeconds, then elimination, then the usual respawn delay. Free-For-All has no
        // medics (nobody is coming to help), and an instant death has nothing to call out, so both
        // go straight to the normal respawn delay.
        bool bleedsOut = struggles && MatchManager.Instance != null &&
            MatchManager.Instance.GameMode == MatchManager.Mode.TeamDeathmatch;
        _respawnTimer = bleedsOut ? TeamBleedOutSeconds + RespawnDelaySeconds : RespawnDelaySeconds;
        AnnounceDeath(attackerId, styleFlags, basePoints, struggles);
    }

    /// <summary>
    /// Whether this death makes a sound. Returns true for a death that does ("struggles"), false
    /// for an instant one that is completely silent - nobody hears anything, not even the victim.
    ///
    ///  - Melee: ALWAYS a sound. A knife to the throat is an instant kill, but there is air in the
    ///    throat, so it is not a silent one - melee wins over everything below, headshot bit or not.
    ///  - Headshot: instant and silent (any gun, incl. a sniper once one exists).
    ///  - Explosive (Bazooka hit): instant and silent.
    ///  - Anything else - a chest/body shot, even one that kills outright - the victim makes a
    ///    sound while dying, heard by whoever is close enough.
    ///
    /// Decided once, here, on the victim's own authoritative device from the flags the shooter sent
    /// with the damage, and carried to every peer through KillFeed.KillAnnounced so everyone agrees.
    /// </summary>
    private static bool DetermineIfDeathStruggles(KillStyle styles)
    {
        if ((styles & KillStyle.Melee) != 0) return true;
        bool instant = (styles & (KillStyle.Headshot | KillStyle.Explosive)) != 0;
        return !instant;
    }

    /// <summary>Team Deathmatch, Friendly Fire off, and the attacker is on this player's own team -
    /// this is the check that actually counts (FR-MP-06: validated on the receiving player's own
    /// authoritative device). WeaponSwitcher.FireHitscan skips the same case a moment earlier on
    /// the shooter's side purely so a friendly hit gives no hit-marker feedback; that copy is only
    /// a courtesy, not a security boundary.</summary>
    private bool IsFriendlyFire(long attackerId)
    {
        MatchManager? match = MatchManager.Instance;
        if (match == null || match.FriendlyFire) return false;
        return match.AreTeammates(attackerId, PlayerIdentity.IdOf(_player));
    }

    /// <summary>
    /// Publishes the confirmed kill to every peer. Only this device deducted the HP that ended the
    /// player, so only this device may announce it - KillFeed rejects announcements from any peer
    /// other than the victim.
    /// </summary>
    private void AnnounceDeath(long killerId, int styleFlags, int basePoints, bool struggles)
    {
        // A replicated second lethal hit can still land while the victim is already dead (IsDead
        // guards that), but a late RPC could target the respawn; never score the same death twice.
        if (_deathAnnounced) return;
        _deathAnnounced = true;

        if (_killFeed == null)
        {
            GD.PushWarning("KillFeed autoload unavailable - this kill was not scored or shown.");
            return;
        }

        int totalPoints = KillStyleBonus.Evaluate(basePoints, (KillStyle)styleFlags);
        SelectAssist(killerId, out long assistPeerId, out int assistPoints);
        _killFeed.Publish(killerId, PlayerIdentity.IdOf(_player), styleFlags, basePoints,
            totalPoints, assistPeerId, assistPoints, struggles);
    }

    /// <summary>Highest non-killing damage contributor takes the assist; Part 6 never stacks an
    /// assist onto the killer's own total.</summary>
    private void SelectAssist(long killerId, out long assistPeerId, out int assistPoints)
    {
        assistPeerId = -1;
        assistPoints = 0;
        float bestContribution = 0.0f;
        foreach (KeyValuePair<long, float> entry in _damageContribution)
        {
            if (entry.Key == killerId || entry.Value <= bestContribution) continue;
            bestContribution = entry.Value;
            assistPeerId = entry.Key;
        }
        if (assistPeerId >= 0) assistPoints = KillStyleBonus.EvaluateAssist(bestContribution);
    }

    private float GetContribution(long attackerId) =>
        _damageContribution.TryGetValue(attackerId, out float dealt) ? dealt : 0.0f;

    private void Respawn()
    {
        // Bots come back where the map's walkable grid says it is safe and far from everyone; people use
        // the mode-appropriate selector below.
        _player.Position = PlayerIdentity.IsBot(_player)
            ? BotNav.PickRespawnPosition(_player, _deathPosition, SpawnAreaHalfExtent, _botRng)
            : SelectHumanRespawnPoint();
        _player.Velocity = Vector3.Zero;
        _damageContribution.Clear();
        _deathAnnounced = false;
        // The setter drives the animation reset (see ApplyVisualState) so that the owning peer and
        // every remote observer take the same path. Calling ResetAfterRespawn again here would be
        // redundant, and it used to be the ONLY call - which is why remote copies stayed dead.
        CurrentHealth = MaxHealth;
        EmitSignal(SignalName.Respawned);
    }

    /// <summary>
    /// v1.1 respawn rules for a human player (bots are handled separately above): Team Deathmatch
    /// comes back in the player's own TeamSpawnArea; everything else (Free-For-All) comes back
    /// completely at random anywhere in the arena, with no minimum distance from the death location -
    /// a deliberate departure from the old FR-PL-06 rule that RespawnPointSelector.Select still
    /// implements for whatever still calls it directly (BotNav's no-nav-grid fallback).
    /// </summary>
    private Vector3 SelectHumanRespawnPoint()
    {
        MatchManager? match = MatchManager.Instance;
        if (match != null && match.GameMode == MatchManager.Mode.TeamDeathmatch)
        {
            int team = match.GetTeam(PlayerIdentity.IdOf(_player));
            if (team >= 0 && TeamSpawnArea.TryGet(_player, team, out Vector3 center, out Vector2 halfExtent))
                return RespawnPointSelector.SelectInArea(_player, center, halfExtent, _player.GetRid());
            GD.PushWarning($"No TeamSpawnArea configured for team {team}; falling back to a map-wide random respawn.");
        }
        return RespawnPointSelector.SelectAnywhere(_player, SpawnAreaHalfExtent, _player.GetRid());
    }

    /// <summary>
    /// Reacts to a health change. Both transitions are handled here, on purpose: this runs on the
    /// owning peer AND on every remote copy, because CurrentHealth is replicated and each peer's
    /// setter fires. Respawn() only ever executes on the owner, so hanging the animation reset off
    /// Respawn() left every other peer's copy of this player frozen in the death pose forever -
    /// that was the "respawns as a corpse" bug.
    ///
    /// Only the alive-to-dead and dead-to-alive EDGES act. Firing on every write would restart the
    /// locomotion state machine on each point of damage taken.
    /// </summary>
    private void ApplyVisualState(bool wasAlive, bool alive)
    {
        // The body mesh always stays visible now - death is shown by actually playing a death
        // animation (see PlayDeath below), not by hiding the model. Collision still turns off
        // so a corpse doesn't block foot traffic during the respawn delay.
        if (_collision != null) _collision.Disabled = !alive;
        if (wasAlive && !alive) _animation?.PlayDeath();
        else if (!wasAlive && alive) _animation?.ResetAfterRespawn();
    }
}
