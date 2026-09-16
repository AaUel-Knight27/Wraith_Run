using Godot;

/// <summary>Configures the scene's synchronizer at runtime so the player scene stays readable.</summary>
public partial class PlayerNetworkSynchronizer : Node
{
    public override void _Ready()
    {
        var synchronizer = GetParent().GetNode<MultiplayerSynchronizer>("MultiplayerSynchronizer");
        var config = new SceneReplicationConfig();
        config.AddProperty(new NodePath(".:position"));
        config.AddProperty(new NodePath(".:rotation")); // body yaw
        config.AddProperty(new NodePath("Head:rotation")); // first-person head pitch
        config.AddProperty(new NodePath(".:ReplicatedMovementState"));
        config.AddProperty(new NodePath(".:ReplicatedAimState"));
        config.AddProperty(new NodePath("Health:CurrentHealth"));
        synchronizer.ReplicationConfig = config;
    }
}
