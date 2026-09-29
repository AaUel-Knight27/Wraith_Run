using Godot;

/// <summary>
/// Autoload (see project.godot). A full-screen overlay shown while LanMenu hands off into
/// GameWorld - covers the one-frame scene-swap hitch and gives the "Preparing the battlefield"
/// beat the menu mockups show. Two call sites only, both one line each:
///   - LanMenu.LeaveForGame() calls Show() right before the deferred scene change.
///   - GameWorld._Ready() calls Hide() once players have spawned.
/// Self-contained and built entirely in code (background image, bar, label, tips) rather than a
/// separate .tscn, so there is nothing else to wire up - drop this file in scripts/autoloads/,
/// add the one project.godot autoload line, and it works.
/// </summary>
public partial class LoadingScreen : CanvasLayer
{
	public static LoadingScreen Instance { get; private set; } = null!;

	private const string BackgroundImagePath = "res://art/ui/loading/loading_background.png";

	/// <summary>Never disappears faster than this, even if GameWorld._Ready() fires the same
	/// frame Show() was called - a loading screen that flashes for one frame reads as a glitch,
	/// not as "the game loaded fast".</summary>
	private const float MinDisplaySeconds = 1.6f;

	private const float FadeSeconds = 0.35f;
	private const float TipRotateSeconds = 2.6f;

	private static readonly string[] Tips =
	{
		"Tip: Headshots and clean one-shot kills end it fast - anything else, they go down fighting.",
		"Tip: A shot heard from far away arrives a beat late - real distance, real delay.",
		"Tip: Work together. One drives, one shoots.",
		"Tip: Melee never goes quietly, no matter how hard you hit.",
	};

	private ColorRect _fade = null!;
	private ProgressBar _bar = null!;
	private Label _tipLabel = null!;
	private Tween? _barTween;
	private Tween? _fadeTween;
	private Timer _tipTimer = null!;
	private ulong _shownAtMsec;
	private bool _visible;
	private int _tipIndex = -1;
	private readonly RandomNumberGenerator _rng = new();

	public override void _Ready()
	{
		Instance = this;
		Layer = 100;
		ProcessMode = ProcessModeEnum.Always;
		_rng.Randomize();
		Build();
		SetOverlayVisible(false, immediate: true);
	}

	/// <summary>Shows the overlay and starts the fill-and-tips animation. Safe to call more than
	/// once in a row (a second call just resets the timer).</summary>
	public new void Show()
	{
		_shownAtMsec = Time.GetTicksMsec();
		_bar.Value = 0.0;
		_tipIndex = -1;
		RollTip();
		_tipTimer.Start(TipRotateSeconds);
		SetOverlayVisible(true, immediate: true);

		_barTween?.Kill();
		_barTween = CreateTween();
		// Eases toward ~92%, not 100 - the last stretch is left for Hide() to complete, so the
		// bar visually "finishes" right when the match is actually ready rather than sitting
		// full while the player waits on nothing.
		_barTween.TweenProperty(_bar, "value", 92.0, MinDisplaySeconds * 0.85)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
	}

	/// <summary>Completes the bar and fades the overlay out, but never before MinDisplaySeconds
	/// has elapsed since Show() - see the class doc.</summary>
	public new void Hide()
	{
		if (!_visible) return;
		float elapsed = (Time.GetTicksMsec() - _shownAtMsec) / 1000.0f;
		float remaining = Mathf.Max(0.0f, MinDisplaySeconds - elapsed);
		if (remaining <= 0.0f)
		{
			FinishHide();
			return;
		}
		var timer = GetTree().CreateTimer(remaining);
		timer.Timeout += FinishHide;
	}

	private void FinishHide()
	{
		if (!_visible) return;
		_tipTimer.Stop();
		_barTween?.Kill();
		_barTween = CreateTween();
		_barTween.TweenProperty(_bar, "value", 100.0, 0.2)
			.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);

		_fadeTween?.Kill();
		_fadeTween = CreateTween();
		_fadeTween.TweenProperty(_fade.GetParent<CanvasItem>(), "modulate:a", 0.0, FadeSeconds)
			.SetDelay(0.15f);
		_fadeTween.TweenCallback(Callable.From(() => SetOverlayVisible(false, immediate: true)));
	}

	private void SetOverlayVisible(bool visible, bool immediate)
	{
		_visible = visible;
		Visible = visible;
		if (immediate && visible) _fade.GetParent<CanvasItem>().Modulate = new Color(1, 1, 1, 1);
	}

	private void RollTip()
	{
		if (Tips.Length == 0) return;
		int next = Tips.Length == 1 ? 0 : _rng.RandiRange(0, Tips.Length - 1);
		if (next == _tipIndex) next = (next + 1) % Tips.Length;
		_tipIndex = next;
		_tipLabel.Text = Tips[_tipIndex];
	}

	// -------------------------------------------------------------------------------------------
	// Layout - built once in code, no .tscn to keep in sync.
	// -------------------------------------------------------------------------------------------
	private void Build()
	{
		var root = new Control { Name = "Root" };
		root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		root.MouseFilter = Control.MouseFilterEnum.Stop;
		AddChild(root);

		var bg = new TextureRect
		{
			Name = "Background",
			Texture = GD.Load<Texture2D>(BackgroundImagePath),
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
		};
		bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		bg.ClipContents = true;
		root.AddChild(bg);

		// Darken the lower third so the bar/label/tip stay legible over any background.
		var scrim = new ColorRect { Name = "Scrim", Color = new Color(0, 0, 0, 0.55f) };
		scrim.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
		scrim.OffsetTop = -170;
		root.AddChild(scrim);

		var column = new VBoxContainer { Name = "Column", Alignment = BoxContainer.AlignmentMode.Center };
		column.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
		column.OffsetTop = -140;
		column.OffsetBottom = -36;
		column.OffsetLeft = 48;
		column.OffsetRight = -48;
		column.AddThemeConstantOverride("separation", 10);
		root.AddChild(column);

		var loadingLabel = new Label { Name = "LoadingLabel", Text = "LOADING..." };
		loadingLabel.AddThemeFontSizeOverride("font_size", 22);
		loadingLabel.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.92f));
		loadingLabel.HorizontalAlignment = HorizontalAlignment.Left;
		column.AddChild(loadingLabel);

		_bar = new ProgressBar { Name = "Bar", MinValue = 0, MaxValue = 100, ShowPercentage = false };
		_bar.CustomMinimumSize = new Vector2(0, 14);
		var barBg = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.12f), CornerRadiusTopLeft = 2,
			CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2 };
		var barFill = new StyleBoxFlat { BgColor = new Color(0.82f, 0.09f, 0.11f), CornerRadiusTopLeft = 2,
			CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2 };
		_bar.AddThemeStyleboxOverride("background", barBg);
		_bar.AddThemeStyleboxOverride("fill", barFill);
		column.AddChild(_bar);

		_tipLabel = new Label { Name = "TipLabel" };
		_tipLabel.AddThemeFontSizeOverride("font_size", 14);
		_tipLabel.AddThemeColorOverride("font_color", new Color(0.78f, 0.78f, 0.78f));
		_tipLabel.HorizontalAlignment = HorizontalAlignment.Left;
		column.AddChild(_tipLabel);

		_tipTimer = new Timer { Name = "TipTimer", OneShot = false, WaitTime = TipRotateSeconds };
		_tipTimer.Timeout += RollTip;
		AddChild(_tipTimer);

		// Modulate on the root Control is what FinishHide fades - keep a reference the tween
		// methods above can reach without another field.
		_fade = scrim;
	}
}
