using Godot;
using System;

[GlobalClass]
public partial class FindSniperScopeReticle : Node
{
	public override async void _Ready()
	{
		GD.Print("=== FINDING SNIPER SCOPE RETICLE ===");
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

		var data = ResourceLoader.Load<ViewModelWeaponData>("res://data/viewmodels/Sniper.tres").Duplicate() as ViewModelWeaponData;
		var vm = new ViewModelBase { Name = "SniperVM" };
		vm.Configure(data);
		camera.AddChild(vm);

		for (int f = 0; f < 5; f++) await ToSignal(GetTree(), "process_frame");

		var animPlayer = vm.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		var skel = vm.FindChild("Skeleton3D", true, false) as Skeleton3D;
		var rifleMesh = vm.FindChild("Rifle", true, false) as MeshInstance3D;

		// Play Rig|SRifle_Shot_sight at t=0
		animPlayer.Play("Rig|SRifle_Shot_sight");
		animPlayer.Seek(0.0, true);
		for (int f = 0; f < 3; f++) await ToSignal(GetTree(), "process_frame");

		// Find the vertices of the scope on Rifle mesh:
		// In Rifle mesh, the scope lenses are along the rifle barrel.
		// Let's test a range of AimPositionOffsets around (-0.074, 0.14, -0.1)
		// specifically adjusting X and Y to find what centers the scope reticle on (640, 360)!
		
		// If in Sniper_ADS_fire.png, scope was at roughly X=600, Y=250:
		// Center is (640, 360).
		// Scope needs to move RIGHT (+X screen) by ~40px, and DOWN (+Y screen) by ~110px.
		// In camera space:
		// Moving gun RIGHT is +X in ViewPosition / AimPositionOffset.
		// Moving gun DOWN is -Y in ViewPosition / AimPositionOffset.
		
		// Let's test a grid of AimPositionOffsets:
		Vector3 baseAim = data.AimPositionOffset; // currently (-0.074, 0.14, -0.1)
		
		float[] dX = new float[] { -0.02f, -0.01f, 0.0f, 0.01f, 0.02f, 0.03f };
		float[] dY = new float[] { -0.06f, -0.04f, -0.02f, 0.0f, 0.02f };

		// Let's test a few specific aim offsets and save screenshots with a red crosshair at (640, 360)!
		Vector3[] testAims = new[] {
			new Vector3(-0.074f, 0.14f, -0.1f),           // original
			new Vector3(-0.064f, 0.11f, -0.1f),
			new Vector3(-0.060f, 0.09f, -0.1f),
			new Vector3(-0.055f, 0.08f, -0.1f),
			new Vector3(-0.050f, 0.07f, -0.1f),
			new Vector3(-0.045f, 0.06f, -0.1f)
		};

		vm.SetAiming(true);

		for (int i = 0; i < testAims.Length; i++)
		{
			data.AimPositionOffset = testAims[i];
			vm.Configure(data);
			vm.ApplyData();
			animPlayer.Play("Rig|SRifle_Shot_sight");
			animPlayer.Seek(0.0, true);

			for (int f = 0; f < 5; f++)
			{
				vm._Process(0.016);
				await ToSignal(GetTree(), "process_frame");
			}

			Image img = viewport.GetTexture().GetImage();
			// Draw crosshair at (640, 360):
			for (int x = 620; x <= 660; x++) img.SetPixel(x, 360, Colors.Red);
			for (int y = 340; y <= 380; y++) img.SetPixel(640, y, Colors.Red);

			string path = $"/home/aauel/Documents/Projects/Games/wraith_run_godot/scripts/tests/Sniper_test_aim_{i}.png";
			img.SavePng(path);
			GD.Print($"Saved {path} with aimOffset={testAims[i]}");
		}

		GetTree().Quit();
	}
}
