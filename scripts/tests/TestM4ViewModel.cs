using Godot;
using System;

[GlobalClass]
public partial class TestM4ViewModel : Node
{
	public override async void _Ready()
	{
		GD.Print("=== TESTING M4 VIEWMODEL SETUP ===");
		var cam = new Camera3D();
		AddChild(cam);

		var data = ResourceLoader.Load<ViewModelWeaponData>("res://data/viewmodels/M4.tres");
		GD.Print($"M4 Data: UseRigCameraTransform={data.UseRigCameraTransform}, ViewScale={data.ViewScale}, ViewPosition={data.ViewPosition}, ViewRotationDegrees={data.ViewRotationDegrees}");

		var vm = new ViewModelBase { Name = "M4VM" };
		vm.Configure(data);
		cam.AddChild(vm);

		await ToSignal(GetTree(), "process_frame");
		await ToSignal(GetTree(), "process_frame");

		var rig = vm.GetNodeOrNull<Node3D>("AnimationRig");
		GD.Print($"ViewModel Position={vm.Position}, Scale={vm.Scale}");
		if (rig != null)
		{
			GD.Print($"Rig Position={rig.Position}, Scale={rig.Scale}, GlobalPosition={rig.GlobalPosition}");
			foreach (Node child in rig.GetChildren())
			{
				if (child is Node3D n3d)
				{
					GD.Print($"  Rig Child '{n3d.Name}' pos={n3d.Position} rot={n3d.RotationDegrees} scale={n3d.Scale}");
				}
			}
		}

		// Check meshes
		var skel = vm.FindChild("Skeleton3D", true, false) as Skeleton3D;
		if (skel != null)
		{
			GD.Print($"Skeleton Bone count={skel.GetBoneCount()}");
			for (int i = 0; i < skel.GetBoneCount(); i++)
			{
				string bName = skel.GetBoneName(i);
				if (bName.ToLower().Contains("cam") || bName.ToLower().Contains("head") || bName.ToLower().Contains("root"))
				{
					GD.Print($"  Bone {i}: '{bName}' pose={skel.GetBonePosePosition(i)} rot={skel.GetBonePoseRotation(i)}");
				}
			}
		}

		foreach (Node n in vm.FindChildren("*", "MeshInstance3D", true, false))
		{
			if (n is MeshInstance3D mesh && mesh.Visible)
			{
				GD.Print($"Mesh '{mesh.Name}': visible={mesh.Visible}, AABB={mesh.GetAabb()}, GlobalPos={mesh.GlobalPosition}");
			}
		}

		GetTree().Quit();
	}
}
