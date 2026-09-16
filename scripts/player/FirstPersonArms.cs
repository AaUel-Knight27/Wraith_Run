using Godot;
using System;

/// <summary>
/// Keeps the local first-person view-model to the arms-only mesh. Both meshes
/// share the imported Arms Skeleton3D, so this does not change retargeting.
/// </summary>
public partial class FirstPersonArms : Node3D
{
    // The imported FBX root carries a -1.65 m vertical authoring offset.
    // Keep it, but force the view-model forward of the camera at runtime.
    private static readonly Vector3 ViewModelOffset = new(0.18f, -1.65f, -0.62f);

    private MeshInstance3D _fullBodyMesh = null!;
    private MeshInstance3D _armsOnlyMesh = null!;

    public override void _Ready()
    {
        _fullBodyMesh = FindChild("Armature_Mesh", true, false) as MeshInstance3D
            ?? throw new InvalidOperationException("Armature_Arms is missing Armature_Mesh.");
        _armsOnlyMesh = FindChild("Armature_Mesh_ArmsOnly", true, false) as MeshInstance3D
            ?? throw new InvalidOperationException("Armature_Arms is missing Armature_Mesh_ArmsOnly.");
        if (GetParent().IsMultiplayerAuthority()) Position = ViewModelOffset;
        ApplyVisibility();
    }

    public override void _Process(double delta)
    {
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        bool local = GetParent().IsMultiplayerAuthority();
        _fullBodyMesh.Visible = !local;
        _armsOnlyMesh.Visible = local;
        var camera = GetParent().GetNode<Camera3D>("FirstPersonCamera");
        camera.Current = local;
    }
}
