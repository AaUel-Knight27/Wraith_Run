using Godot;

/// <summary>
/// Backstop for the player's MultiplayerSynchronizer.
///
/// The replication config now lives in Player.tscn as a real SceneReplicationConfig sub-resource.
/// It has to: MultiplayerSpawner starts replication for a spawned node before that node's children
/// reach _Ready, so building the config here was always one frame too late and the engine logged
/// "on_replication_start: ... !sync->get_replication_config_ptr() is true ... ERR_UNCONFIGURED"
/// on every spawn.
///
/// This script stays as a guard rather than being deleted: if the scene's config ever goes missing
/// (a bad merge, someone clearing it in the inspector), it rebuilds the same property list at
/// runtime rather than leaving the player silently unsynchronized.
/// </summary>
public partial class PlayerNetworkSynchronizer : Node
{
	public override void _Ready()
	{
		var synchronizer = GetParent().GetNode<MultiplayerSynchronizer>("MultiplayerSynchronizer");
		if (synchronizer.ReplicationConfig != null) return;

		GD.PushWarning("Player.tscn's MultiplayerSynchronizer had no replication config; rebuilding "
			+ "it at runtime. Spawned players will log ERR_UNCONFIGURED until the scene is fixed.");

		var config = new SceneReplicationConfig();
		config.AddProperty(new NodePath(".:position"));
		config.AddProperty(new NodePath(".:rotation")); // body yaw
		config.AddProperty(new NodePath("Head:rotation")); // first-person head pitch
		config.AddProperty(new NodePath(".:ReplicatedMovementState"));
		config.AddProperty(new NodePath(".:ReplicatedAimState"));
		config.AddProperty(new NodePath("Health:CurrentHealth"));
		config.AddProperty(new NodePath("WeaponSwitcher:ReplicatedWeaponIndex"));
		// Scores are awarded by the owning device only (see PlayerScore), then replicated out so
		// every peer can display the same total.
		config.AddProperty(new NodePath("Score:TotalPoints"));
		synchronizer.ReplicationConfig = config;
	}
}
