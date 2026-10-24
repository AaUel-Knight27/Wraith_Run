using Godot;
using System;

[GlobalClass]
public partial class InspectRigs : Node
{
	public override void _Ready()
	{
		GD.Print("=== INSPECT RIGS ===");
		Inspect("M4", "res://art/animations/player/combat/M4/source/m4_assult_rfile.fbx");
		Inspect("Shotgun", "res://art/animations/player/combat/benelli m4/source/HandWithGloves_AnimateRDY.fbx");
		Inspect("Sniper", "res://art/animations/player/combat/Sniper/source/BoltActionRifle.fbx");
		GetTree().Quit();
	}

	private void Inspect(string name, string path)
	{
		GD.Print($"\n--- Inspecting {name} ({path}) ---");
		var scene = ResourceLoader.Load<PackedScene>(path);
		if (scene == null) { GD.PrintErr($"Failed to load {path}"); return; }
		var inst = scene.Instantiate<Node3D>();
		AddChild(inst);

		var cams = inst.FindChildren("*", "Camera3D", true, false);
		GD.Print($"Cameras found: {cams.Count}");
		foreach (Node c in cams)
		{
			if (c is Camera3D cam)
			{
				GD.Print($"  Camera name='{cam.Name}' transform={cam.Transform} fov={cam.Fov}");
			}
		}

		var skel = inst.FindChild("Skeleton3D", true, false) as Skeleton3D;
		if (skel != null)
		{
			GD.Print($"Skeleton bone count: {skel.GetBoneCount()}");
			for (int i = 0; i < skel.GetBoneCount(); i++)
			{
				string bName = skel.GetBoneName(i);
				if (bName.Contains("cam", StringComparison.OrdinalIgnoreCase) || bName.Contains("root", StringComparison.OrdinalIgnoreCase) || bName.Contains("main", StringComparison.OrdinalIgnoreCase))
				{
					GD.Print($"  Bone {i}: '{bName}' pose={skel.GetBonePose(i)}");
				}
			}
		}

		var animPlayer = inst.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		if (animPlayer != null)
		{
			GD.Print($"Animations ({animPlayer.GetAnimationList().Length}):");
			foreach (var a in animPlayer.GetAnimationList())
			{
				GD.Print($"  Anim: '{a}' len={animPlayer.GetAnimation(a).Length}");
			}
		}

		inst.QueueFree();
	}
}
