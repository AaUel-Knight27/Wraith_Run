using Godot;
using System;

[GlobalClass]
public partial class TestShotgunWithCameraTransform : Node
{
	public override async void _Ready()
	{
		GD.Print("=== TESTING SHOTGUN WITH RIG CAMERA ALIGNMENT ===");
		var viewport = new SubViewport();
		viewport.Size = new Vector2I(1280, 720);
		viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
		AddChild(viewport);

		var env = new Godot.Environment();
		env.BackgroundMode = Godot.Environment.BGMode.Color;
		env.BackgroundColor = new Color(0.15f, 0.15f, 0.18f);
		env.AmbientLightColor = new Color(1.0f, 1.0f, 1.0f);
		var world3D = new World3D { Environment = env };
		viewport.World3D = world3D;

		var light = new DirectionalLight3D();
		light.RotationDegrees = new Vector3(-45, 45, 0);
		viewport.AddChild(light);

		var camera = new Camera3D();
		camera.Fov = 75.0f;
		camera.Near = 0.005f;
		camera.Far = 100.0f;
		camera.Current = true;
		viewport.AddChild(camera);

		var data = ResourceLoader.Load<ViewModelWeaponData>("res://data/viewmodels/Shotgun.tres").Duplicate() as ViewModelWeaponData;
		data.UseRigCameraTransform = true;
		data.ViewScale = new Vector3(0.042f, 0.042f, 0.042f);
		data.ViewRotationDegrees = Vector3.Zero; // M4 uses Zero!
		data.NormalizationReferenceRig = data.RigScene;
		data.NormalizationReferenceArmMeshNames = new Godot.Collections.Array<string> { "sleeve", "hardknuckle" };

		Vector3[] testPos = new[] {
			new Vector3(0.12f, -0.10f, -0.04f), // Exact M4 position
			new Vector3(0.08f, -0.08f, -0.04f),
			new Vector3(0.05f, -0.06f, -0.04f),
			new Vector3(0.00f, -0.05f, -0.04f),
			new Vector3(-0.02f, -0.05f, -0.04f),
			new Vector3(0.05f, -0.10f, -0.04f)
		};

		for (int i = 0; i < testPos.Length; i++)
		{
			data.ViewPosition = testPos[i];
			var vm = new ViewModelBase { Name = $"ShotgunVM_{i}" };
			vm.Configure(data);
			camera.AddChild(vm);

			// Add the authored camera to the rig before vm sets up:
			var rig = vm.GetNodeOrNull<Node3D>("AnimationRig");
			if (rig != null && rig.FindChild("Camera3D", true, false) == null)
			{
				var rigCam = new Camera3D { Name = "Camera" };
				rigCam.Position = new Vector3(0, 34.000217f, 0);
				rigCam.RotationDegrees = new Vector3(0, 180, 0);
				rig.AddChild(rigCam);
				vm.ApplyData();
			}

			for (int f = 0; f < 5; f++) await ToSignal(GetTree(), "process_frame");

			Image img = viewport.GetTexture().GetImage();
			string path = $"/home/aauel/Documents/Projects/Games/wraith_run_godot/scripts/tests/Shotgun_cam_test_{i}.png";
			img.SavePng(path);
			GD.Print($"Saved {path} with pos={testPos[i]}");

			camera.RemoveChild(vm);
			vm.QueueFree();
			await ToSignal(GetTree(), "process_frame");
		}

		GetTree().Quit();
	}
}
