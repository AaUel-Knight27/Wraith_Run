using Godot;
using System;
using System.Collections.Generic;

[GlobalClass]
public partial class WeaponAnimationTester : Node
{
	public override async void _Ready()
	{
		GD.Print("==================================================");
		GD.Print("STARTING COMPREHENSIVE VIEWMODEL ANIMATION TESTS");
		GD.Print("==================================================");

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

		string[] states = { "draw", "idle", "fire", "reload", "reload_full", "walk", "run" };

		var viewport = new SubViewport();
		viewport.Size = new Vector2I(1280, 720);
		AddChild(viewport);

		var camera = new Camera3D();
		camera.Current = true;
		viewport.AddChild(camera);

		int passCount = 0;
		int failCount = 0;
		var failures = new List<string>();

		foreach (var weapon in weapons)
		{
			GD.Print($"\n>>> Testing Weapon: '{weapon}' <<<");
			string tresName = weapon.Replace(" ", "").Replace("-", "");
			string tresPath = $"res://data/viewmodels/{tresName}.tres";
			if (!ResourceLoader.Exists(tresPath))
			{
				GD.PrintErr($"FAIL: Resource not found: {tresPath}");
				failCount++;
				failures.Add($"{weapon}: Resource not found {tresPath}");
				continue;
			}

			var data = ResourceLoader.Load<ViewModelWeaponData>(tresPath);
			if (data == null)
			{
				GD.PrintErr($"FAIL: Could not load {tresPath}");
				failCount++;
				failures.Add($"{weapon}: Failed to load data");
				continue;
			}

			var vm = new ViewModelBase { Name = $"{tresName}VM" };
			vm.Configure(data);
			camera.AddChild(vm);

			// Let _Ready execute
			await ToSignal(GetTree(), "process_frame");

			var animPlayer = vm.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
			if (animPlayer == null)
			{
				GD.PrintErr($"FAIL: {weapon} has no AnimationPlayer");
				failCount++;
				failures.Add($"{weapon}: No AnimationPlayer");
				vm.QueueFree();
				continue;
			}

			var rig = vm.GetNodeOrNull<Node3D>("AnimationRig");
			if (rig == null)
			{
				GD.PrintErr($"FAIL: {weapon} has no AnimationRig");
				failCount++;
				failures.Add($"{weapon}: No AnimationRig");
				vm.QueueFree();
				continue;
			}

			// Test all animation states
			foreach (var state in states)
			{
				bool played = vm.Play(state);
				if (!played)
				{
					GD.Print($"  WARNING: state '{state}' not in ClipMap for {weapon}");
				}
				else
				{
					// Step animation
					animPlayer.Advance(0.1);
					await ToSignal(GetTree(), "process_frame");

					if (!vm.Position.IsFinite() || !vm.RotationDegrees.IsFinite())
					{
						GD.PrintErr($"FAIL: {weapon} invalid transform during '{state}': pos={vm.Position} rot={vm.RotationDegrees}");
						failCount++;
						failures.Add($"{weapon}: Invalid transform during {state}");
						continue;
					}

					if (!rig.Position.IsFinite() || !rig.RotationDegrees.IsFinite() || !rig.Scale.IsFinite())
					{
						GD.PrintErr($"FAIL: {weapon} rig invalid transform during '{state}': pos={rig.Position} scale={rig.Scale}");
						failCount++;
						failures.Add($"{weapon}: Invalid rig transform during {state}");
						continue;
					}

					GD.Print($"  State '{state}': OK (anim='{animPlayer.CurrentAnimation}', time={animPlayer.CurrentAnimationPosition:F2}/{animPlayer.CurrentAnimationLength:F2}s)");
					passCount++;
				}
			}

			// Test aiming if supported
			if (data.HasScope || weapon == "Sniper")
			{
				vm.SetAiming(true);
				for (int f = 0; f < 10; f++)
				{
					vm._Process(0.016);
					await ToSignal(GetTree(), "process_frame");
				}
				GD.Print($"  ADS State: pos={vm.Position} rot={vm.RotationDegrees}");
				if (!vm.Position.IsFinite() || !vm.RotationDegrees.IsFinite())
				{
					GD.PrintErr($"FAIL: {weapon} invalid transform during ADS");
					failCount++;
					failures.Add($"{weapon}: Invalid transform during ADS");
				}
				else
				{
					passCount++;
				}
				vm.SetAiming(false);
			}

			// Test muzzle fire
			vm.PlayMuzzleFire();
			await ToSignal(GetTree(), "process_frame");

			vm.QueueFree();
			await ToSignal(GetTree(), "process_frame");
		}

		GD.Print("\n==================================================");
		GD.Print($"TEST SUMMARY: {passCount} passed, {failCount} failed");
		if (failCount > 0)
		{
			GD.PrintErr("FAILURES:");
			foreach (var f in failures) GD.PrintErr($" - {f}");
		}
		else
		{
			GD.Print("ALL WEAPON ANIMATION TESTS PASSED PERFECTLY!");
		}
		GD.Print("==================================================");

		GetTree().Quit(failCount > 0 ? 1 : 0);
	}
}
