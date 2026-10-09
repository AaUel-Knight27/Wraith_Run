using Godot;

/// <summary>
/// The rule the training center is built on: EVERY body in training - the trainee, enemies, friends -
/// uses the shared character (art/characters/AshenCorvus.fbx). More characters will be added to the game later; none of them
/// may ever appear here, and no character's kit may change how training plays.
///
/// Three places enforce it:
///  1. Bots/friends/targets are built by InstantiateBody(), which only ever loads MeshPath.
///  2. PrepareTrainee() strips the trainee's CharacterLoadout node (the thing that would apply an
///     operator's tint/kit) and the two networking nodes an offline player must not have.
///  3. VerifyTrainee() shouts (PushError) if the trainee's Armature is not the shared character.
/// </summary>
public static class SharedCharacterModel
{
    public const string MeshPath = "res://art/characters/AshenCorvus.fbx";
    public const string PlayerScenePath = "res://scenes/Player.tscn";

    /// <summary>Yaw correction the project uses everywhere: AshenCorvus.fbx faces +Z, gameplay forward is -Z
    /// (Player.tscn's Armature carries exactly this 180 degree turn).</summary>
    public static readonly Basis ArmatureBasis = new Basis(new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1));

    public static Node3D? InstantiateBody()
    {
        var scene = ResourceLoader.Load<PackedScene>(MeshPath);
        if (scene == null)
        {
            GD.PushError($"SharedCharacterModel: could not load {MeshPath}.");
            return null;
        }
        Node3D? body = scene.Instantiate<Node3D>();
        if (body != null) body.Name = "Armature";
        return body;
    }

    /// <summary>Call BEFORE the player enters the tree. Frees nodes that must not exist in training.</summary>
    public static void PrepareTrainee(CharacterBody3D player)
    {
        // No operator kit, no operator tint: training is the class baseline, on the shared character.
        FreeIfPresent(player, "CharacterLoadout");
        // These two only make sense with a MultiplayerSpawner-driven session; PlayerNetworkSynchronizer
        // throws in _Ready if its sibling MultiplayerSynchronizer is missing, so both go together.
        FreeIfPresent(player, "NetworkSynchronizerSetup");
        FreeIfPresent(player, "MultiplayerSynchronizer");
    }

    public static bool VerifyTrainee(CharacterBody3D player)
    {
        Node? armature = player.GetNodeOrNull<Node>("Armature");
        bool ok = armature != null && armature.SceneFilePath == MeshPath && player.GetNodeOrNull<Node>("CharacterLoadout") == null;
        if (!ok)
            GD.PushError("Training Center: the trainee is not the shared character (Armature should be " + MeshPath
                + " and there must be no CharacterLoadout). Training only ever runs on the shared character.");
        return ok;
    }

    private static void FreeIfPresent(Node parent, string childName)
    {
        Node? child = parent.GetNodeOrNull<Node>(childName);
        if (child == null) return;
        parent.RemoveChild(child);
        child.Free();
    }
}
