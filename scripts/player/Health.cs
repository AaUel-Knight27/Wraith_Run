using Godot;

/// <summary>
/// Networked health and death/respawn. Lives on the owning peer's Player instance.
/// CurrentHealth is replicated outward by PlayerNetworkSynchronizer, so every peer's copy of
/// this node ends up running the same setter - that's what actually hides the model and
/// disables the hitbox on remote clients, not just the local owner.
/// </summary>
public partial class Health : Node
{
    [Signal] public delegate void DiedEventHandler();
    [Signal] public delegate void RespawnedEventHandler();

    private const float RespawnDelaySeconds = 3.0f;
    private static readonly Vector3 SpawnAreaExtents = new(15.0f, 0.0f, 15.0f);

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
    private CharacterBody3D _player = null!;
    private CollisionShape3D? _collision;
    private Node3D? _visualRoot;

    public override void _Ready()
    {
        _player = (CharacterBody3D)GetParent();
        _collision = _player.GetNodeOrNull<CollisionShape3D>("CollisionShape3D");
        _visualRoot = _player.GetNodeOrNull<Node3D>("Head/Armature_Arms");
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
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    public void ReceiveDamage(float amount, long attackerId)
    {
        if (!IsMultiplayerAuthority() || IsDead || amount <= 0.0f) return;
        CurrentHealth -= amount;
        if (IsDead) _respawnTimer = RespawnDelaySeconds;
    }

    private void Respawn()
    {
        var rng = new RandomNumberGenerator();
        _player.Position = new Vector3(
            rng.RandfRange(-SpawnAreaExtents.X, SpawnAreaExtents.X), 0.1f,
            rng.RandfRange(-SpawnAreaExtents.Z, SpawnAreaExtents.Z));
        _player.Velocity = Vector3.Zero;
        CurrentHealth = MaxHealth;
        EmitSignal(SignalName.Respawned);
    }

    private void ApplyVisualState(bool alive)
    {
        // References may not be assigned yet if replication delivers a value before this
        // node's own _Ready() runs; _Ready() re-applies the correct state once it does.
        if (_collision == null || _visualRoot == null) return;
        _collision.Disabled = !alive;
        _visualRoot.Visible = alive;
    }
}
