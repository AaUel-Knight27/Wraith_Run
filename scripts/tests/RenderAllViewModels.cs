using Godot;
using System;
using System.Collections.Generic;

[GlobalClass]
public partial class RenderAllViewModels : Node
{
	public override async void _Ready()
	{
		GD.Print("=== RENDERING ALL VIEWMODELS TO PNG ===");

		string[] weapons = {
			"AK-47",
			"M4",
			"Kriss Vector",
			"P90",
			"Shotgun",
			"Sniper",
			"Glock 19",
			"FN 502",
			"Desert Eagle"
		};

		var viewport = GetViewport();

		var env = new Godot.Environment();
		env.BackgroundMode = Godot.Environment.BGMode.Color;
		env.BackgroundColor = new Color(0.2f, 0.22f, 0.25f);
		env.AmbientLightColor = new Color(0.9f, 0.9f, 0.9f);
		env.AmbientLightEnergy = 1.0f;

		var world3D = GetTree().Root.World3D ?? new World3D();
		world3D.Environment = env;

		var light = new DirectionalLight3D();
		light.RotationDegrees = new Vector3(-45, 45, 0);
		light.LightEnergy = 1.5f;
		AddChild(light);

		var camera = new Camera3D();
		camera.Fov = 75.0f;
		camera.Near = 0.005f;
		camera.Far = 100.0f;
		camera.Current = true;
		AddChild(camera);

		foreach (var weapon in weapons)
		{
			string tresName = weapon.Replace(" ", "").Replace("-", "");
			string tresPath = $"res://data/viewmodels/{tresName}.tres";
			if (!ResourceLoader.Exists(tresPath)) continue;

			var data = ResourceLoader.Load<ViewModelWeaponData>(tresPath);
			if (data == null) continue;

			var vm = new ViewModelBase { Name = $"{tresName}VM" };
			vm.Configure(data);
			camera.AddChild(vm);

			vm.Play("idle");

			for (int i = 0; i < 10; i++)
			{
				await ToSignal(GetTree(), "process_frame");
			}

			Image img = viewport.GetTexture().GetImage();
			string savePath = $"/home/aauel/Documents/Projects/Games/wraith_run_godot/scripts/tests/{tresName}_render.png";
			img.SavePng(savePath);
			GD.Print($"Saved render for {weapon} -> {savePath}");

			if (data.HasScope || data.AimPositionOffset != Vector3.Zero || weapon == "Sniper")
			{
				vm.SetAiming(true);
				for (int i = 0; i < 25; i++)
				{
					vm._Process(0.016);
					await ToSignal(GetTree(), "process_frame");
				}
				Image adsImg = viewport.GetTexture().GetImage();
				string adsPath = $"/home/aauel/Documents/Projects/Games/wraith_run_godot/scripts/tests/{tresName}_ads_render.png";
				adsImg.SavePng(adsPath);
				GD.Print($"Saved ADS render for {weapon} -> {adsPath}");
			}

			camera.RemoveChild(vm);
			vm.QueueFree();
			await ToSignal(GetTree(), "process_frame");
		}

		GD.Print("=== ALL RENDERS COMPLETE ===");
		GetTree().Quit();
	}
}
