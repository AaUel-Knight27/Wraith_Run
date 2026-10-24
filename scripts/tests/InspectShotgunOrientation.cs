using Godot;
using System;

[GlobalClass]
public partial class InspectShotgunOrientation : Node
{
	public override async void _Ready()
	{
		GD.Print("=== INSPECTING SHOTGUN ORIENTATION IN RIG ===");
		var scene = ResourceLoader.Load<PackedScene>("res://art/animations/player/combat/benelli m4/source/HandWithGloves_AnimateRDY.fbx");
		var inst = scene.Instantiate<Node3D>();
		AddChild(inst);

		var animPlayer = inst.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		var skel = inst.FindChild("Skeleton3D", true, false) as Skeleton3D;
		var gunMesh = inst.FindChild("benelli_m4_TTI_Benelli_M4_0", true, false) as MeshInstance3D;
		var sleeveMesh = inst.FindChild("sleeve", true, false) as MeshInstance3D;

		await ToSignal(GetTree(), "process_frame");
		await ToSignal(GetTree(), "process_frame");

		GD.Print("Rig children:");
		foreach (Node c in inst.GetChildren())
		{
			if (c is Node3D n) GD.Print($"  {n.Name}: pos={n.Position} rot={n.RotationDegrees} scale={n.Scale}");
		}

		if (animPlayer != null && animPlayer.HasAnimation("Rig|Rig|Rig|Rig|Rig|M4_Idle"))
		{
			animPlayer.Play("Rig|Rig|Rig|Rig|Rig|M4_Idle");
			animPlayer.Advance(0.1);
			await ToSignal(GetTree(), "process_frame");
		}

		// Inspect gun bone
		int mainBone = skel.FindBone("Main");
		GD.Print($"Bone 'Main' (# {mainBone}):");
		Transform3D globalPose = skel.GetBoneGlobalPose(mainBone);
		Transform3D worldBone = skel.GlobalTransform * globalPose;
		GD.Print($"  globalPose origin={globalPose.Origin} rot={globalPose.Basis.GetEuler() * (180.0f / Mathf.Pi)}");
		GD.Print($"  worldBone origin={worldBone.Origin} rot={worldBone.Basis.GetEuler() * (180.0f / Mathf.Pi)}");

		// Inspect all bones
		for (int i = 0; i < skel.GetBoneCount(); i++)
		{
			string b = skel.GetBoneName(i);
			if (b.Contains("shoulder") || b.Contains("arm") || b.Contains("hand") || b.Contains("body") || b.Contains("pose"))
			{
				Transform3D gp = skel.GetBoneGlobalPose(i);
				Transform3D wb = skel.GlobalTransform * gp;
				GD.Print($"  Bone '{b}' (#{i}): origin={wb.Origin} rot={wb.Basis.GetEuler() * (180.0f / Mathf.Pi)}");
			}
		}

		GetTree().Quit();
	}
}
