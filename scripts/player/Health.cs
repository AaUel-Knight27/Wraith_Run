using Godot;
using System.Collections.Generic;

/// <summary>
/// Networked health and death/respawn. Lives on the owning peer's Player instance.
/// CurrentHealth is replicated outward by PlayerNetworkSynchronizer, so every peer's copy of
/// this node runs the same setter and disables the hitbox when the player dies.
///
/// Health is currently replicated from each player's owning device. Incoming damage claims are
/// bound to the authenticated sender and checked against that attacker's current weapon, but the
/// hit itself is still client-reported; competitive play needs host-side shot validation.
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
    /// Downed (struggling) state: HP at or below this fraction of max, but above zero. Applies in every
    /// mode. A downed player can be revived by a teammate's medic inside DownedWindowSeconds; a hit
    /// while downed finishes him instantly and silently; the window running out is a struggling death.
    /// </summary>
    public const float DownedThresholdFraction = 0.20f;

    /// <summary>How long a downed player waits for a medic before the struggling death.</summary>
    public const float DownedWindowSeconds = 15.0f;

    /// <summary>Hold T this long, near a downed teammate, to revive them.</summary>
    public const float ReviveHoldSeconds = 2.5f;

    /// <summary>How close (metres) and how square-on a reviver must be.</summary>
    public const float ReviveRangeMetres = 2.5f;

    /// <summary>Hold Y this long while downed to give up. The death is silent.</summary>
    public const float GiveUpHoldSeconds = 1.0f;

    /// <summary>Respawn delay after giving up (faster than the normal RespawnDelaySeconds).</summary>
    public const float GiveUpRespawnSeconds = 1.0f;

    /// <summary>HP a player is brought back to when a medic revives him.</summary>
    public const float ReviveHealthFraction = 0.30f;

    /// <summary>
    /// Half-extent of the square area a Free-For-All respawn is drawn from (see
    /// RespawnPointSelector.SelectAnywhere). Kept inside the Prison interior floor bounds; widen it
    /// when the usable floor changes. Team Deathmatch ignores this and uses the respawning player's
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
            ApplyVisualState(_currentHealth > 0.0f);
            if (wasAlive && _currentHealth <= 0.0f) EmitSignal(SignalName.Died);
        }
    }

    public bool IsDead => _currentHealth <= 0.0f;
    public float RespawnTimeRemaining => _respawnTimer;

    private float _currentHealth;
    private bool _hasInitialized;
    private float _respawnTimer;
    private float _downedTimer;
    private bool _wasDowned;
    private long _lastAttackerId;
    private int _lastStyleFlags;
    private int _lastBasePoints;
    private float _reviveProgress;
    private float _giveUpProgress;
    private CharacterBody3D _player = null!;
    private CollisionShape3D? _collision;
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
        if (!PlayerIdentity.HasAuthority(this)) return;
        if (!HubState.Active) TickDownedInteractions((float)delta);
        if (IsDead)
        {
            _respawnTimer -= (float)delta;
            if (_respawnTimer <= 0.0f) Respawn();
            return;
        }
        bool downed = IsDowned;
        if (downed && !_wasDowned) _downedTimer = DownedWindowSeconds;
        _wasDowned = downed;
        if (!downed) return;
        _downedTimer -= (float)delta;
        // No medic came in time: the struggle ends in a death the whole match hears.
        if (_downedTimer <= 0.0f) Die(_lastAttackerId, _lastStyleFlags, _lastBasePoints, struggles: true);
    }

    /// <summary>
    /// Called by an attacker's client via RpcId targeted at this player's owning peer. The owner
    /// checks the RPC sender, attacker identity, weapon damage and payload bounds before applying it.
    ///
    /// styleFlags and basePoints are still client-reported shot context, so this is not a trusted
    /// competitive scoring boundary until shots are validated by the host.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveDamage(float amount, long attackerId, int styleFlags, int basePoints)
    {
        if (!PlayerIdentity.HasAuthority(this) || IsDead || amount <= 0.0f) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (Multiplayer.HasMultiplayerPeer() && sender != 0 && sender != attackerId) return;
        if (!IsValidDamageClaim(amount, attackerId, styleFlags, basePoints)) return;
        // The hub is a place to stand around and try weapons: shots land, nobody gets hurt.
        if (HubState.Active) return;
        if (IsFriendlyFire(attackerId)) return;
        _damageContribution[attackerId] = GetContribution(attackerId) + amount;
        _lastAttackerId = attackerId;
        _lastStyleFlags = styleFlags;
        _lastBasePoints = basePoints;

        // Already struggling on the ground: the next hit finishes him instantly and silently.
        if (IsDowned)
        {
            Die(attackerId, styleFlags, basePoints, struggles: false);
            return;
        }

        CurrentHealth -= amount;
        EmitSignal(SignalName.Damaged, amount, attackerId);
        if (!IsDead)
        {
            // Dropped into the downed band (alive, but on the ground until a medic arrives or the window ends).
            return;
        }

        // Lethal from above the downed band: the death is decided by the shot itself (headshot and
        // explosive are instant and silent; anything else makes the struggle sound).
        bool struggles = DetermineIfDeathStruggles((KillStyle)styleFlags);
        _deathPosition = _player.GlobalPosition;
        _respawnTimer = RespawnDelaySeconds;
        AnnounceDeath(attackerId, styleFlags, basePoints, struggles);
    }

    private bool IsValidDamageClaim(float amount, long attackerId, int styleFlags, int basePoints)
    {
        if (basePoints != KillStyleBonus.DefaultBaseKillPoints || (styleFlags & ~63) != 0) return false;
        CharacterBody3D? attacker = FindPlayer(attackerId);
        if (attacker == null) return false;
        long sender = Multiplayer.GetRemoteSenderId();
        if (Multiplayer.HasMultiplayerPeer() && sender != 0 &&
            (PlayerIdentity.IsBot(attacker) ? sender != 1 : sender != attacker.GetMultiplayerAuthority())) return false;
        WeaponData? weapon = attacker.GetNodeOrNull<WeaponSwitcher>("WeaponSwitcher")?.CurrentWeaponData;
        if (weapon == null) return false;
        bool legalAmount = Mathf.IsEqualApprox(amount, weapon.BodyDamage) ||
            (weapon.HeadshotDamage > 0.0f && Mathf.IsEqualApprox(amount, weapon.HeadshotDamage));
        return legalAmount;
    }

    private CharacterBody3D? FindPlayer(long peerId)
    {
        Node? players = _player.GetParent();
        if (players == null) return null;
        foreach (Node child in players.GetChildren())
            if (child is CharacterBody3D body && PlayerIdentity.IdOf(body) == peerId) return body;
        return null;
    }

    /// <summary>Whether this player is downed: alive, but at or below DownedThresholdFraction of max HP.</summary>
    public bool IsDowned => !IsDead && CurrentHealth > 0.0f && CurrentHealth <= MaxHealth * DownedThresholdFraction;

    /// <summary>Seconds left before a downed player dies of the struggle (0 when not downed).</summary>
    public float DownedTimeRemaining => IsDowned ? _downedTimer : 0.0f;

    /// <summary>Owner-side death from the downed state or a finishing hit. Respawns after RespawnDelaySeconds.</summary>
    private void Die(long killerId, int styleFlags, int basePoints, bool struggles)
    {
        if (IsDead) return;
        _deathPosition = _player.GlobalPosition;
        _respawnTimer = RespawnDelaySeconds;
        CurrentHealth = 0.0f;
        AnnounceDeath(killerId, styleFlags, basePoints, struggles);
    }

    /// <summary>
    /// A teammate's medic revives this downed player. Called by the medic's client through an RPC to the
    /// owning peer; the owner validates the state and the teammate relationship before restoring HP.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveRevive(long medicPeerId)
    {
        if (!PlayerIdentity.HasAuthority(this) || !IsDowned) return;
        if (HubState.Active) return;
        long sender = Multiplayer.GetRemoteSenderId();
        CharacterBody3D? medic = FindPlayer(medicPeerId);
        if (medic == null || medic == _player) return;
        if (Multiplayer.HasMultiplayerPeer() && sender != 0 &&
            (PlayerIdentity.IsBot(medic) ? sender != 1 : sender != medic.GetMultiplayerAuthority())) return;
        if (_player.GlobalPosition.DistanceTo(medic.GlobalPosition) > ReviveRangeMetres) return;
        if (medic.GetNodeOrNull<Health>("Health")?.IsDead != false) return;
        MatchManager? match = MatchManager.Instance;
        if (match != null && match.IsTeamMode && !match.AreTeammates(medicPeerId, PlayerIdentity.IdOf(_player))) return;
        _downedTimer = 0.0f;
        _wasDowned = false;
        CurrentHealth = MaxHealth * ReviveHealthFraction;
    }

    /// <summary>
    /// Whether this death makes a sound. Returns true for a death that does ("struggles"), false
    /// for an instant one that is completely silent - nobody hears anything, not even the victim.
    ///
    ///  - Melee: ALWAYS a sound. A knife to the throat is an instant kill, but there is air in the
    ///    throat, so it is not a silent one - melee wins over everything below, headshot bit or not.
    ///  - Headshot: instant and silent (any gun, incl. a sniper once one exists).
    ///  - Explosive hit: instant and silent.
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
        if (match != null && match.IsTeamMode)
        {
            int team = match.GetTeam(PlayerIdentity.IdOf(_player));
            if (team >= 0 && TeamSpawnArea.TryGet(_player, team, out Vector3 center, out Vector2 teamHalfExtent))
                return RespawnPointSelector.SelectInArea(_player, center, teamHalfExtent, _player.GetRid());
            GD.PushWarning($"No TeamSpawnArea configured for team {team}; falling back to a map-wide random respawn.");
        }
        float halfExtent = match?.SelectedMap switch
        {
            MatchManager.ArenaMap.Warehouse => 7.0f,
            MatchManager.ArenaMap.DefaultMap => 26.0f,
            _ => SpawnAreaHalfExtent,
        };
        return RespawnPointSelector.SelectAnywhere(_player, halfExtent, _player.GetRid());
    }

    /// <summary>
    /// Reacts to health changes on the owning peer and each remote copy, because CurrentHealth is
    /// replicated and each peer's setter fires. Collision changes only on alive/dead transitions.
    /// </summary>
    private void ApplyVisualState(bool alive)
    {
        if (_collision != null) _collision.Disabled = !alive;
    }

    /// <summary>Owner-side: give up while downed, and revive a downed teammate in front of us.</summary>
    private void TickDownedInteractions(float dt)
    {
        string? prompt = null;

        if (IsDowned)
        {
            if (Input.IsActionPressed(GameUiState.GiveUpAction))
            {
                _giveUpProgress += dt;
                if (_giveUpProgress >= GiveUpHoldSeconds)
                {
                    _giveUpProgress = 0.0f;
                    Die(_lastAttackerId, _lastStyleFlags, _lastBasePoints, struggles: false);
                    _respawnTimer = GiveUpRespawnSeconds;
                    ShowPrompt(null);
                    return;
                }
                prompt = $"GIVING UP  {(int)(100f * _giveUpProgress / GiveUpHoldSeconds)}%";
            }
            else
            {
                _giveUpProgress = 0.0f;
                prompt = "HOLD Y TO GIVE UP";
            }
            _reviveProgress = 0.0f;
            ShowPrompt(prompt);
            return;
        }
        _giveUpProgress = 0.0f;

        Health? target = FindDownedTeammateInFront();
        if (target != null)
        {
            if (Input.IsActionPressed(GameUiState.ReviveAction))
            {
                _reviveProgress += dt;
                if (_reviveProgress >= ReviveHoldSeconds)
                {
                    _reviveProgress = 0.0f;
                    long victimAuthority = target._player.GetMultiplayerAuthority();
                    if (!Multiplayer.HasMultiplayerPeer() || victimAuthority == Multiplayer.GetUniqueId())
                        target.ReceiveRevive(PlayerIdentity.IdOf(_player));
                    else
                        target.RpcId((int)victimAuthority, Health.MethodName.ReceiveRevive, PlayerIdentity.IdOf(_player));
                    ShowPrompt(null);
                    return;
                }
                prompt = $"REVIVING  {(int)(100f * _reviveProgress / ReviveHoldSeconds)}%";
            }
            else
            {
                _reviveProgress = 0.0f;
                prompt = "HOLD T TO REVIVE";
            }
        }
        else
        {
            _reviveProgress = 0.0f;
        }
        ShowPrompt(prompt);
    }

    /// <summary>The nearest downed teammate within reach and roughly in front of this player, or null.</summary>
    private Health? FindDownedTeammateInFront()
    {
        Node? players = _player.GetParent();
        if (players == null) return null;
        MatchManager? match = MatchManager.Instance;
        long me = PlayerIdentity.IdOf(_player);
        Vector3 forward = -_player.GlobalBasis.Z;
        forward.Y = 0.0f;
        forward = forward.Normalized();

        Health? best = null;
        float bestDistance = float.MaxValue;
        foreach (Node child in players.GetChildren())
        {
            if (child == _player || child is not CharacterBody3D body) continue;
            Health? other = body.GetNodeOrNull<Health>("Health");
            if (other == null || !other.IsDowned) continue;
            if (match != null && match.IsTeamMode && !match.AreTeammates(me, PlayerIdentity.IdOf(body))) continue;

            Vector3 toBody = body.GlobalPosition - _player.GlobalPosition;
            toBody.Y = 0.0f;
            float distance = toBody.Length();
            if (distance < 0.01f || distance > ReviveRangeMetres) continue;
            if (forward.Dot(toBody / distance) < 0.5f) continue; // about 60 degrees either side
            if (distance < bestDistance) { best = other; bestDistance = distance; }
        }
        return best;
    }

    private void ShowPrompt(string? text)
    {
        _player.GetNodeOrNull<PlayerHud>("Hud")?.SetPrompt(text);
    }
}
