using Godot;

/// <summary>
/// The very first thing the player sees. Full-screen title art, "TAP TO CONTINUE" pulsing at the
/// bottom, advances to the main menu on any key, mouse click, or touch. Set as project.godot's
/// run/main_scene - scenes/MainMenu.tscn (the actual LanMenu) is now one step further in.
/// Self-contained, built in code, same pattern as LoadingScreen.cs.
/// </summary>
public partial class SplashScreen : Control
{
	private const string BackgroundImagePath = "res://art/ui/splash/splash_screen.png";
	// First run still goes through the old menu's CREATE ACCOUNT screen; after that the game
	// starts in the hub (the walkable warehouse) instead of a full-screen menu.
	private static string NextScenePath => PlayerAccount.SetupDone ? "res://scenes/Hub.tscn" : "res://scenes/MainMenu.tscn";
	private bool _advancing;

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Stop;
		SetAnchorsPreset(LayoutPreset.FullRect);
		Build();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (_advancing) return;
		bool isPress = (@event is InputEventKey key && key.Pressed && !key.Echo)
			|| (@event is InputEventMouseButton mouse && mouse.Pressed)
			|| (@event is InputEventScreenTouch touch && touch.Pressed)
			|| (@event is InputEventJoypadButton pad && pad.Pressed);
		if (!isPress) return;
		Advance();
	}

	private void Advance()
	{
		_advancing = true;
		var tween = CreateTween();
		tween.TweenProperty(this, "modulate:a", 0.0, 0.3).SetTrans(Tween.TransitionType.Sine);
		tween.TweenCallback(Callable.From(() => GetTree().ChangeSceneToFile(NextScenePath)));
	}

	private void Build()
	{
		var bg = new TextureRect
		{
			Name = "Background",
			Texture = GD.Load<Texture2D>(BackgroundImagePath),
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
		};
		bg.SetAnchorsPreset(LayoutPreset.FullRect);
		bg.ClipContents = true;
		AddChild(bg);
		// "TAP TO CONTINUE" is already part of the art (edited in from the original "PRESS ANY
		// KEY"); nothing further to draw here - _UnhandledInput does the rest.
	}
}
