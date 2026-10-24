using Godot;
using System;

[GlobalClass]
public partial class InspectM4AndShotgun : Node
{
	public override void _Ready()
	{
		GD.Print("=== INSPECT M4 vs SHOTGUN ===");
		TestWeapon("M4", "res://data/viewmodels/M4.tres");
		TestWeapon("Shotgun", "res://data/viewmodels/Shotgun.tres");
		GetTree().Quit();
	}

	private void TestWeapon(string name, string tresPath)
	{
		GD.Print($"\n>>> Testing {name} <<<");
		var data = ResourceLoader.Load<ViewModelWeaponData>(tresPath);
		var cam = new Camera3D();
		AddChild(cam);

		var vm = new ViewModelBase { Name = $"{name}VM" };
		vm.Configure(data);
		cam.AddChild(vm);

		var rig = vm.GetNodeOrNull<Node3D>("AnimationRig");
		if (rig != null)
		{
			GD.Print($"{name}: rig.Transform.Origin={rig.Transform.Origin}, rot={rig.Transform.Basis.GetEuler() * (180.0f / Mathf.Pi)}, scale={rig.Transform.Basis.Scale}");
		}

		GD.Print($"{name}: vm.Position={vm.Position}, vm.Rotation={vm.RotationDegrees}, ScaleOffset={vm.ScaleOffset}");

		cam.RemoveChild(vm);
		vm.QueueFree();
		cam.QueueFree();
	}
}
