using System;
using Godot;

/// <summary>
/// Look-and-feel for every menu screen. Values are sampled from the design mockups: pure-black
/// buttons with a thin red border, near-black panels with a dark-red hairline, one hot red for
/// titles and the primary action, white Rajdhani text.
///
/// Screens build their controls through these helpers, so a palette or font change happens in one
/// place. Everything is built in code (like LanMenu always was) - there is no theme resource to
/// keep in sync with the scene.
/// </summary>
public static class MenuStyle
{
    private static Color Hex(uint rgb, float a = 1.0f) => new(
        ((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f, a);

    // ---- palette ----------------------------------------------------------------------------
    public static readonly Color Ink = Hex(0x000000);
    public static readonly Color PanelFill = Hex(0x060708, 0.94f);
    public static readonly Color RowFill = Hex(0x0b0c0e);
    public static readonly Color Hairline = Hex(0x4a0e10);
    public static readonly Color Border = Hex(0xc8231f);
    public static readonly Color BorderHot = Hex(0xff3a30);
    public static readonly Color HoverFill = Hex(0x1a0606);
    public static readonly Color PressFill = Hex(0x240808);
    public static readonly Color Accent = Hex(0xee1218);
    public static readonly Color PlayFill = Hex(0x8d0c0a);
    public static readonly Color PlayFillHover = Hex(0xa80f0c);
    public static readonly Color PlayFillPressed = Hex(0x6d0a08);
    public static readonly Color PlayBorder = Hex(0xd00808);
    public static readonly Color SelectedFill = Hex(0x480404);
    public static readonly Color SelectedBorder = Hex(0x9a0a08);
    public static readonly Color DisabledFill = Hex(0x3f0305);
    public static readonly Color DisabledBorder = Hex(0x5a1214);
    public static readonly Color TextMain = Hex(0xf4f1f1);
    public static readonly Color TextDim = Hex(0xc9c2c3);
    public static readonly Color TextMuted = Hex(0x7c7576);
    public static readonly Color Glow = Hex(0xff1414, 0.38f);

    // ---- fonts ------------------------------------------------------------------------------
    // Rajdhani (SIL OFL) in three weights. If a file is missing the menu still works with
    // Godot's fallback font - it just stops matching the mockups.
    private static Font _bold;
    private static Font _semi;
    private static Font _medium;
    public static Font Bold => _bold ??= LoadFont("700");
    public static Font SemiBold => _semi ??= LoadFont("600");
    public static Font Medium => _medium ??= LoadFont("500");

    private static Font LoadFont(string weight)
    {
        string path = $"res://art/ui/fonts/rajdhani-latin-{weight}-normal.woff2";
        Font font = ResourceLoader.Exists(path) ? ResourceLoader.Load<Font>(path) : null;
        if (font == null) GD.PushWarning($"Menu font missing: {path}. Using the default font.");
        return font ?? ThemeDB.FallbackFont;
    }

    /// <summary>The same font with extra space between letters - used for the wordmark.</summary>
    public static Font Spaced(Font baseFont, int pixels) =>
        new FontVariation { BaseFont = baseFont, SpacingGlyph = pixels };

    // ---- small building blocks --------------------------------------------------------------

    /// <summary>Anchors then offsets, in that order (setting an anchor can nudge the opposite one).</summary>
    public static void Place(Control c, float aL, float aT, float aR, float aB,
        float oL, float oT, float oR, float oB)
    {
        c.AnchorLeft = aL; c.AnchorTop = aT; c.AnchorRight = aR; c.AnchorBottom = aB;
        c.OffsetLeft = oL; c.OffsetTop = oT; c.OffsetRight = oR; c.OffsetBottom = oB;
    }

    public static void Fill(Control c) => Place(c, 0, 0, 1, 1, 0, 0, 0, 0);

    public static Label Text(string text, int size, Color color, Font font = null,
        HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = align,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontOverride("font", font ?? Medium);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    /// <summary>White SVG icon from res://art/ui/icons, tinted by Modulate.</summary>
    public static TextureRect Icon(string name, int width, int height = 0, Color? tint = null)
    {
        string path = $"res://art/ui/icons/{name}.svg";
        Texture2D texture = null;
        if (ResourceLoader.Exists(path)) texture = ResourceLoader.Load<Texture2D>(path);
        else GD.PushWarning($"Menu icon missing: {path}");
        return new TextureRect
        {
            Texture = texture,
            CustomMinimumSize = new Vector2(width, height <= 0 ? width : height),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.Linear,
            Modulate = tint ?? Colors.White,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
    }

    public static StyleBoxFlat Box(Color fill, Color border, int borderWidth = 1, int radius = 2,
        int glow = 0)
    {
        var box = new StyleBoxFlat { BgColor = fill, BorderColor = border };
        box.SetBorderWidthAll(borderWidth);
        box.SetCornerRadiusAll(radius);
        if (glow > 0) { box.ShadowSize = glow; box.ShadowColor = Glow; }
        return box;
    }

    private static StyleBoxFlat Ring(int radius)
    {
        var ring = new StyleBoxFlat { DrawCenter = false, BorderColor = BorderHot };
        ring.SetBorderWidthAll(2);
        ring.SetCornerRadiusAll(radius);
        return ring;
    }

    private static Button NewButton(Vector2 minSize, Action onPressed)
    {
        var button = new Button
        {
            CustomMinimumSize = minSize,
            FocusMode = Control.FocusModeEnum.All,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        button.Pressed += onPressed;
        return button;
    }

    private static void StyleButton(Button b, StyleBox normal, StyleBox hover, StyleBox pressed,
        StyleBox disabled, int ringRadius = 2)
    {
        b.AddThemeStyleboxOverride("normal", normal);
        b.AddThemeStyleboxOverride("hover", hover);
        b.AddThemeStyleboxOverride("pressed", pressed);
        b.AddThemeStyleboxOverride("hover_pressed", pressed);
        b.AddThemeStyleboxOverride("disabled", disabled);
        // Drawn on top of the normal/hover box while the button has keyboard or gamepad focus.
        b.AddThemeStyleboxOverride("focus", Ring(ringRadius));
    }

    /// <summary>A control that lays out its children but never eats mouse input, so the Button
    /// underneath still receives hover and click.</summary>
    private static MarginContainer Padding(int horizontal, int vertical)
    {
        var pad = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        pad.AddThemeConstantOverride("margin_left", horizontal);
        pad.AddThemeConstantOverride("margin_right", horizontal);
        pad.AddThemeConstantOverride("margin_top", vertical);
        pad.AddThemeConstantOverride("margin_bottom", vertical);
        Fill(pad);
        return pad;
    }

    // ---- buttons ----------------------------------------------------------------------------

    /// <summary>Black button, thin red border, icon on the left and a title (plus optional
    /// subtitle) - Choose Operator, Customize Player, Train, and the Play-screen choices.</summary>
    public static Button Card(string icon, string title, string subtitle, Vector2 size,
        Action onPressed, int titleSize = 24, int iconSize = 44, string badge = null)
    {
        var button = NewButton(size, onPressed);
        StyleButton(button, Box(Ink, Border), Box(HoverFill, BorderHot, 1, 2, 10),
            Box(PressFill, BorderHot), Box(Ink, Hairline));

        var pad = Padding(16, 8);
        button.AddChild(pad);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 14);
        pad.AddChild(row);

        var iconHolder = new Control
        {
            CustomMinimumSize = new Vector2(iconSize, iconSize),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var iconRect = Icon(icon, iconSize);
        iconHolder.AddChild(iconRect);
        if (badge != null)
        {
            // Icon shrinks toward the bottom-left so a small tag (the "AI" on the Train button)
            // can sit in the top-right corner without overlapping it.
            Place(iconRect, 0, 0, 1, 1, 0, 9, -9, 0);
            var tag = Text(badge, 17, TextMain, Bold);
            Place(tag, 1, 0, 1, 0, -24, -4, 4, 16);
            iconHolder.AddChild(tag);
        }
        else Fill(iconRect);
        row.AddChild(iconHolder);

        var texts = new VBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        texts.AddThemeConstantOverride("separation", -2);
        var titleLabel = Text(title, titleSize, TextMain, Bold);
        titleLabel.AddThemeConstantOverride("line_spacing", -5);
        texts.AddChild(titleLabel);
        if (!string.IsNullOrEmpty(subtitle)) texts.AddChild(Text(subtitle, 16, TextDim, Medium));
        row.AddChild(texts);
        return button;
    }

    /// <summary>The Customize Gun card: a wide icon stacked over a centred title (the mockup lays
    /// this one out vertically, unlike the icon-left cards).</summary>
    public static Button StackedCard(string icon, string title, Vector2 size, Action onPressed)
    {
        var button = NewButton(size, onPressed);
        StyleButton(button, Box(Ink, Border), Box(HoverFill, BorderHot, 1, 2, 10),
            Box(PressFill, BorderHot), Box(Ink, Hairline));
        var pad = Padding(12, 8);
        button.AddChild(pad);
        var stack = new VBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        stack.AddThemeConstantOverride("separation", 2);
        var glyph = Icon(icon, 112, 45);
        glyph.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        stack.AddChild(glyph);
        stack.AddChild(Text(title, 22, TextMain, Bold, HorizontalAlignment.Center));
        pad.AddChild(stack);
        return button;
    }

    /// <summary>The big red PLAY button on the main menu: play triangle, huge title, subtitle.</summary>
    public static Button Hero(string title, string subtitle, Vector2 size, Action onPressed)
    {
        var button = NewButton(size, onPressed);
        StyleButton(button, Box(PlayFill, PlayBorder, 2, 2, 14),
            Box(PlayFillHover, BorderHot, 2, 2, 22), Box(PlayFillPressed, BorderHot, 2),
            Box(DisabledFill, DisabledBorder, 2));

        var pad = Padding(26, 8);
        button.AddChild(pad);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 22);
        pad.AddChild(row);
        var arrow = Icon("play-solid", 40);
        arrow.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(arrow);

        var texts = new VBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        texts.AddThemeConstantOverride("separation", -8);
        texts.AddChild(Text(title, 54, TextMain, Bold));
        texts.AddChild(Text(subtitle, 20, Hex(0xf0dcdc), Medium));
        row.AddChild(texts);
        return button;
    }

    /// <summary>The slanted red action button used inside sub-screens (PLAY / START HOSTING).</summary>
    public static Button Primary(string text, Vector2 size, Action onPressed)
    {
        var button = NewButton(size, onPressed);
        StyleBoxFlat Slanted(Color fill, Color border, int glow)
        {
            var box = Box(fill, border, 2, 2, glow);
            box.Skew = new Vector2(-0.22f, 0.0f);
            return box;
        }
        StyleButton(button, Slanted(PlayFill, PlayBorder, 12), Slanted(PlayFillHover, BorderHot, 20),
            Slanted(PlayFillPressed, BorderHot, 0), Slanted(DisabledFill, DisabledBorder, 0));
        var label = Text(text, 28, TextMain, Bold, HorizontalAlignment.Center);
        label.VerticalAlignment = VerticalAlignment.Center;
        Fill(label);
        button.AddChild(label);
        return button;
    }

    /// <summary>Icon-only button (gear, power, back arrow) with a subtle hover plate.</summary>
    public static Button IconButton(string icon, int iconSize, Color tint, Action onPressed)
    {
        var button = NewButton(new Vector2(iconSize + 14, iconSize + 14), onPressed);
        StyleButton(button, new StyleBoxEmpty(), Box(HoverFill, Hairline), Box(PressFill, Border),
            new StyleBoxEmpty());
        var rect = Icon(icon, iconSize, 0, tint);
        Place(rect, 0, 0, 1, 1, 7, 7, -7, -7);
        button.AddChild(rect);
        return button;
    }

    /// <summary>A Host / Connect style row: filled and red-bordered when selected, with a radio
    /// dot on the right.</summary>
    public static Button SelectRow(string icon, string text, bool selected, Action onPressed)
    {
        var button = NewButton(new Vector2(0, 52), onPressed);
        Color fill = selected ? SelectedFill : RowFill;
        Color border = selected ? SelectedBorder : Hairline;
        StyleButton(button, Box(fill, border), Box(selected ? SelectedFill : HoverFill, Border),
            Box(PressFill, BorderHot), Box(RowFill, Hairline));

        var pad = Padding(14, 0);
        button.AddChild(pad);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 12);
        pad.AddChild(row);
        var glyph = Icon(icon, 24);
        glyph.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(glyph);
        var label = Text(text, 24, TextMain, SemiBold);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(label);

        var ring = new Panel
        {
            CustomMinimumSize = new Vector2(20, 20),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var ringStyle = Box(new Color(0, 0, 0, 0), selected ? Accent : TextMuted, 2, 10);
        ring.AddThemeStyleboxOverride("panel", ringStyle);
        if (selected)
        {
            var dot = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
            dot.AddThemeStyleboxOverride("panel", Box(Accent, Accent, 0, 5));
            Place(dot, 0.5f, 0.5f, 0.5f, 0.5f, -5, -5, 5, 5);
            ring.AddChild(dot);
        }
        row.AddChild(ring);
        return button;
    }

    /// <summary>One entry in a list (a discovered LAN game): name, detail text, chevron.</summary>
    public static Button Row(string title, string detail, Action onPressed)
    {
        var button = NewButton(new Vector2(0, 58), onPressed);
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        StyleButton(button, Box(RowFill, Hairline), Box(Hex(0x2a0808), Border),
            Box(PressFill, BorderHot), Box(RowFill, Hairline));

        var pad = Padding(16, 0);
        button.AddChild(pad);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 14);
        pad.AddChild(row);
        var name = Text(title, 22, TextMain, SemiBold);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        name.ClipText = true;
        row.AddChild(name);
        var info = Text(detail, 17, TextDim, Medium);
        info.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(info);
        var chevron = Icon("chevron-right", 22, 0, Accent);
        chevron.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(chevron);
        return button;
    }

    // ---- panels -----------------------------------------------------------------------------

    /// <summary>Near-black panel with a red uppercase header. Returns the panel; add content to
    /// <paramref name="body"/>.</summary>
    public static PanelContainer Panel(string header, string icon, out VBoxContainer body)
    {
        var panel = new PanelContainer();
        var style = Box(PanelFill, Hairline);
        style.SetContentMarginAll(16);
        panel.AddThemeStyleboxOverride("panel", style);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        panel.AddChild(column);

        var title = new HBoxContainer();
        title.AddThemeConstantOverride("separation", 10);
        if (icon != null)
        {
            var glyph = Icon(icon, 22, 0, Accent);
            glyph.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            title.AddChild(glyph);
        }
        title.AddChild(Text(header, 22, Accent, Bold));
        column.AddChild(title);

        body = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        column.AddChild(body);
        return panel;
    }

    /// <summary>A read-only "dropdown" row for a setting that only has one real value right now.</summary>
    public static PanelContainer InfoRow(string icon, string value)
    {
        var row = new PanelContainer();
        var style = Box(RowFill, Hairline);
        style.SetContentMarginAll(12);
        row.AddThemeStyleboxOverride("panel", style);
        var line = new HBoxContainer();
        line.AddThemeConstantOverride("separation", 12);
        var glyph = Icon(icon, 24);
        glyph.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        line.AddChild(glyph);
        var label = Text(value, 22, TextMain, SemiBold);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        line.AddChild(label);
        var lockIcon = Icon("lock", 18, 0, TextMuted);
        lockIcon.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        line.AddChild(lockIcon);
        row.AddChild(line);
        row.TooltipText = "More options are coming.";
        return row;
    }

    public static void StyleLineEdit(LineEdit edit)
    {
        var normal = Box(Ink, Hairline);
        normal.SetContentMarginAll(12);
        var focus = Box(Ink, Border);
        focus.SetContentMarginAll(12);
        edit.AddThemeStyleboxOverride("normal", normal);
        edit.AddThemeStyleboxOverride("focus", focus);
        edit.AddThemeFontOverride("font", SemiBold);
        edit.AddThemeFontSizeOverride("font_size", 22);
        edit.AddThemeColorOverride("font_color", TextMain);
        edit.AddThemeColorOverride("font_placeholder_color", TextMuted);
        edit.AddThemeColorOverride("caret_color", Accent);
        edit.AddThemeColorOverride("selection_color", Hex(0x8d0c0a, 0.6f));
    }

    // ---- backdrop ---------------------------------------------------------------------------

    /// <summary>
    /// Dark red-smoke backdrop behind every screen: a base colour, an optional background image,
    /// a glow behind where the operator stands, low fog, a vignette and a few drifting embers.
    /// Drop res://art/ui/menu_background.png (full screen) and/or res://art/ui/operator_menu.png
    /// (transparent PNG of the operator) into the project and they are picked up automatically.
    /// </summary>
    public static void AddBackdrop(Control parent)
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        Fill(root);
        parent.AddChild(root);

        var baseColor = new ColorRect { Color = Hex(0x0a0708), MouseFilter = Control.MouseFilterEnum.Ignore };
        Fill(baseColor);
        root.AddChild(baseColor);

        const string backgroundPath = "res://art/ui/menu_background.png";
        if (ResourceLoader.Exists(backgroundPath))
        {
            var picture = new TextureRect
            {
                Texture = ResourceLoader.Load<Texture2D>(backgroundPath),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            Fill(picture);
            root.AddChild(picture);
        }

        root.AddChild(GradientLayer(Hex(0x7a1210, 0.70f), Hex(0x7a1210, 0.0f),
            new Vector2(0.62f, 0.46f), new Vector2(1.02f, 0.46f), radial: true));
        root.AddChild(GradientLayer(Hex(0x3a0908, 0.0f), Hex(0x3a0908, 0.85f),
            new Vector2(0.0f, 0.5f), new Vector2(0.0f, 1.0f), radial: false));

        const string operatorPath = "res://art/ui/operator_menu.png";
        if (ResourceLoader.Exists(operatorPath))
        {
            var portrait = new TextureRect
            {
                Texture = ResourceLoader.Load<Texture2D>(operatorPath),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            Place(portrait, 0.36f, 0.06f, 0.78f, 1.0f, 0, 0, 0, 0);
            root.AddChild(portrait);
        }

        var vignette = new TextureRect
        {
            Texture = VignetteTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        Fill(vignette);
        root.AddChild(vignette);

        AddEmbers(root);
    }

    private static TextureRect GradientLayer(Color from, Color to, Vector2 fillFrom, Vector2 fillTo,
        bool radial)
    {
        var gradient = new Gradient
        {
            Offsets = new float[] { 0.0f, 1.0f },
            Colors = new Color[] { from, to },
        };
        var texture = new GradientTexture2D
        {
            Gradient = gradient,
            Width = 256,
            Height = 256,
            Fill = radial ? GradientTexture2D.FillEnum.Radial : GradientTexture2D.FillEnum.Linear,
            FillFrom = fillFrom,
            FillTo = fillTo,
        };
        var layer = new TextureRect
        {
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        Fill(layer);
        return layer;
    }

    private static GradientTexture2D VignetteTexture()
    {
        var gradient = new Gradient
        {
            Offsets = new float[] { 0.0f, 0.55f, 1.0f },
            Colors = new Color[] { Hex(0x000000, 0.0f), Hex(0x000000, 0.0f), Hex(0x000000, 0.88f) },
        };
        return new GradientTexture2D
        {
            Gradient = gradient,
            Width = 256,
            Height = 256,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1.05f, 0.5f),
        };
    }

    private static void AddEmbers(Control host)
    {
        var fade = new Gradient
        {
            Offsets = new float[] { 0.0f, 0.2f, 1.0f },
            Colors = new Color[] { Hex(0xff5a2a, 0.0f), Hex(0xff3a1a, 0.9f), Hex(0xff2010, 0.0f) },
        };
        var embers = new CpuParticles2D
        {
            Amount = 34,
            Lifetime = 7.0,
            Preprocess = 7.0,
            EmissionShape = CpuParticles2D.EmissionShapeEnum.Rectangle,
            EmissionRectExtents = new Vector2(576, 4),
            Direction = new Vector2(0, -1),
            Spread = 28.0f,
            Gravity = Vector2.Zero,
            InitialVelocityMin = 22.0f,
            InitialVelocityMax = 75.0f,
            ScaleAmountMin = 1.5f,
            ScaleAmountMax = 3.5f,
            ColorRamp = fade,
        };
        host.AddChild(embers);

        void Reposition()
        {
            embers.Position = new Vector2(host.Size.X * 0.5f, host.Size.Y + 8.0f);
            embers.EmissionRectExtents = new Vector2(Math.Max(host.Size.X * 0.5f, 1.0f), 4.0f);
        }
        host.Resized += Reposition;
        Reposition();
    }
}
