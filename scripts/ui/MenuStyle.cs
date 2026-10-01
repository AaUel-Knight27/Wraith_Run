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

    /// <summary>The one place every menu button is built, so it is also the one place every press
    /// makes its sound. The sound plays first, then the action - which may rebuild or leave the
    /// screen (UiSounds is an autoload, so the sound is not cut off with it).</summary>
    private static Button NewButton(Vector2 minSize, Action onPressed,
        UiSounds.Sound sound = UiSounds.Sound.Click)
    {
        var button = new Button
        {
            CustomMinimumSize = minSize,
            FocusMode = Control.FocusModeEnum.All,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        button.Pressed += () => UiSounds.Play(sound);
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
        var button = NewButton(size, onPressed, UiSounds.Sound.Start);
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
        texts.AddThemeConstantOverride("separation", string.IsNullOrEmpty(subtitle) ? 0 : -8);
        texts.AddChild(Text(title, 54, TextMain, Bold));
        if (!string.IsNullOrEmpty(subtitle)) texts.AddChild(Text(subtitle, 20, Hex(0xf0dcdc), Medium));
        row.AddChild(texts);
        return button;
    }

    /// <summary>The slanted red action button used inside sub-screens (PLAY / START HOSTING).</summary>
    public static Button Primary(string text, Vector2 size, Action onPressed,
        UiSounds.Sound sound = UiSounds.Sound.Start)
    {
        var button = NewButton(size, onPressed, sound);
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
        var button = NewButton(new Vector2(0, 52), onPressed, UiSounds.Sound.Select);
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

    /// <summary>A labelled on/off row - clicking anywhere toggles it. Caller owns the state; this
    /// just draws the current value and reports the flip, same division of labour as SelectRow.</summary>
    public static Button ToggleRow(string icon, string text, bool on, Action<bool> onChanged)
    {
        var button = NewButton(new Vector2(0, 52), () => onChanged(!on), UiSounds.Sound.Select);
        StyleButton(button, Box(RowFill, Hairline), Box(HoverFill, Border),
            Box(PressFill, BorderHot), Box(RowFill, Hairline));

        var pad = Padding(14, 0);
        button.AddChild(pad);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 12);
        pad.AddChild(row);
        if (icon != null)
        {
            var glyph = Icon(icon, 22);
            glyph.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(glyph);
        }
        var label = Text(text, 21, TextMain, SemiBold);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(label);

        const float pillW = 46, pillH = 26, knob = 18;
        var pill = new Panel
        {
            CustomMinimumSize = new Vector2(pillW, pillH),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        pill.AddThemeStyleboxOverride("panel", Box(on ? Accent : RowFill, on ? BorderHot : Hairline, 1, (int)(pillH / 2)));
        var dot = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        dot.AddThemeStyleboxOverride("panel", Box(TextMain, TextMain, 0, (int)(knob / 2)));
        dot.Position = on ? new Vector2(pillW - 4 - knob, (pillH - knob) / 2) : new Vector2(4, (pillH - knob) / 2);
        dot.Size = new Vector2(knob, knob);
        pill.AddChild(dot);
        row.AddChild(pill);
        return button;
    }

    /// <summary>A labelled slider with a live value readout. `onChanged` fires on every drag tick
    /// (for the label and for whatever in-memory field the caller is driving); `onCommit` fires
    /// once when the drag ends, for callers that only want to persist settings at that point
    /// rather than on every tick.</summary>
    public static Control SliderRow(string label, float value, float min, float max, float step,
        string format, Action<float> onChanged, Action onCommit = null)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);

        var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var name = Text(label, 18, TextDim, Medium);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        top.AddChild(name);
        var valueLabel = Text(string.Format(format, value), 18, TextMain, SemiBold,
            HorizontalAlignment.Right);
        top.AddChild(valueLabel);
        box.AddChild(top);

        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step, Value = value,
            CustomMinimumSize = new Vector2(0, 20),
        };
        slider.AddThemeStyleboxOverride("slider", Box(RowFill, Hairline, 1, 4));
        slider.AddThemeStyleboxOverride("grabber_area", Box(Accent, BorderHot, 1, 4));
        slider.AddThemeStyleboxOverride("grabber_area_highlight", Box(Accent, BorderHot, 1, 4));
        slider.ValueChanged += v =>
        {
            valueLabel.Text = string.Format(format, (float)v);
            onChanged((float)v);
        };
        if (onCommit != null) slider.DragEnded += _ => onCommit();
        box.AddChild(slider);
        return box;
    }

    /// <summary>A rebindable key row: a label on the left, a pill-shaped key button on the right.
    /// `listening` swaps the pill's text/colour to "PRESS A KEY..." while the caller is capturing
    /// the next keypress - the row itself does not know how that capture works, only how to draw
    /// the two states.</summary>
    public static Control KeybindRow(string label, string currentKeyText, bool listening, Action onPressed)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        var name = Text(label, 20, TextDim, Medium);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(name);

        var keyButton = NewButton(new Vector2(170, 44), onPressed);
        StyleButton(keyButton,
            Box(listening ? PressFill : RowFill, listening ? BorderHot : Hairline, 1, 4),
            Box(HoverFill, Border, 1, 4), Box(PressFill, BorderHot, 1, 4), Box(RowFill, Hairline, 1, 4));
        var keyLabel = Text(listening ? "PRESS A KEY..." : currentKeyText, 17,
            listening ? Accent : TextMain, SemiBold, HorizontalAlignment.Center);
        keyLabel.VerticalAlignment = VerticalAlignment.Center;
        Fill(keyLabel);
        keyButton.AddChild(keyLabel);
        row.AddChild(keyButton);
        return row;
    }

    /// <summary>One entry in a list (a discovered LAN game): name, detail text, chevron.</summary>
    public static Button Row(string title, string detail, Action onPressed,
        UiSounds.Sound sound = UiSounds.Sound.Select)
    {
        var button = NewButton(new Vector2(0, 58), onPressed, sound);
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

    // ---- loadout screens (operators / weapons / colours) -------------------------------------

    /// <summary>Small outlined tag: EQUIPPED, IN GAME, PLANNED...</summary>
    public static PanelContainer Chip(string text, Color color, int size = 13, bool filled = false)
    {
        var chip = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var style = Box(filled ? color : new Color(0, 0, 0, 0), color, 1, 2);
        style.ContentMarginLeft = 7; style.ContentMarginRight = 7;
        style.ContentMarginTop = 1; style.ContentMarginBottom = 1;
        chip.AddThemeStyleboxOverride("panel", style);
        chip.AddChild(Text(text, size, filled ? Ink : color, Bold));
        return chip;
    }

    /// <summary>Text that wraps to the width its parent gives it.</summary>
    public static Label Wrapped(string text, int size, Color color, Font font = null)
    {
        var label = Text(text, size, color, font);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return label;
    }

    public static Control Divider() => new ColorRect
    {
        Color = Hairline,
        CustomMinimumSize = new Vector2(0, 2),
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    /// <summary>Hairline-framed dark box for the 3D stage.</summary>
    public static PanelContainer Frame()
    {
        var frame = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        frame.AddThemeStyleboxOverride("panel", Box(new Color(0, 0, 0, 0.30f), Hairline));
        return frame;
    }

    /// <summary>One list entry: icon, title + subtitle, optional colour chips and a tag.
    /// Filled red when it is the one being previewed.</summary>
    public static Button ListRow(string icon, int iconWidth, int iconHeight, string title, string subtitle,
        bool selected, Action onPressed, Color[] chips = null, string tag = null, Color? tagColor = null)
    {
        var button = NewButton(new Vector2(0, 62), onPressed);
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        StyleButton(button,
            Box(selected ? SelectedFill : RowFill, selected ? SelectedBorder : Hairline),
            Box(selected ? SelectedFill : HoverFill, Border),
            Box(PressFill, BorderHot),
            Box(RowFill, Hairline));

        var pad = Padding(12, 6);
        button.AddChild(pad);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 12);
        pad.AddChild(row);

        var glyph = Icon(icon, iconWidth, iconHeight, selected ? TextMain : Accent);
        glyph.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(glyph);

        var texts = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        texts.AddThemeConstantOverride("separation", -2);
        var titleLabel = Text(title, 22, TextMain, Bold);
        titleLabel.ClipText = true;
        titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        texts.AddChild(titleLabel);
        var subLabel = Text(subtitle, 15, selected ? TextDim : TextMuted, Medium);
        subLabel.ClipText = true;
        subLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        texts.AddChild(subLabel);
        row.AddChild(texts);

        if (chips != null && chips.Length > 0)
        {
            var strip = new HBoxContainer
            {
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            strip.AddThemeConstantOverride("separation", 2);
            foreach (Color c in chips)
                strip.AddChild(new ColorRect { Color = c, CustomMinimumSize = new Vector2(9, 24), MouseFilter = Control.MouseFilterEnum.Ignore });
            row.AddChild(strip);
        }
        if (!string.IsNullOrEmpty(tag))
            row.AddChild(Chip(tag, tagColor ?? Accent, 12, filled: false));
        return button;
    }

    /// <summary>Small square filter tab: an icon or a short label ("ALL").</summary>
    public static Button Tab(string icon, string label, bool selected, string tooltip, Action onPressed)
    {
        var button = NewButton(new Vector2(42, 40), onPressed);
        button.TooltipText = tooltip;
        StyleButton(button,
            Box(selected ? SelectedFill : RowFill, selected ? SelectedBorder : Hairline),
            Box(selected ? SelectedFill : HoverFill, Border),
            Box(PressFill, BorderHot),
            Box(RowFill, Hairline));
        if (icon != null)
        {
            var glyph = Icon(icon, 22, 0, selected ? TextMain : Accent);
            Place(glyph, 0, 0, 1, 1, 8, 8, -8, -8);
            button.AddChild(glyph);
        }
        else
        {
            var text = Text(label, 16, selected ? TextMain : Accent, Bold, HorizontalAlignment.Center);
            text.VerticalAlignment = VerticalAlignment.Center;
            Fill(text);
            button.AddChild(text);
        }
        return button;
    }

    /// <summary>Wide tab with an icon and a label (the weapon category row: ASSAULT RIFLES, SMGS...).
    /// Filled red when it is the open one.</summary>
    public static Button WideTab(string icon, string label, bool selected, Action onPressed)
    {
        var button = NewButton(new Vector2(0, 42), onPressed);
        StyleButton(button,
            Box(selected ? SelectedFill : RowFill, selected ? SelectedBorder : Hairline),
            Box(selected ? SelectedFill : HoverFill, Border),
            Box(PressFill, BorderHot),
            Box(RowFill, Hairline));

        var pad = Padding(14, 4);
        button.AddChild(pad);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 10);
        pad.AddChild(row);
        var glyph = Icon(icon, 34, 18, selected ? TextMain : Accent);
        glyph.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(glyph);
        var text = Text(label, 18, selected ? TextMain : TextDim, Bold);
        text.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(text);

        // A Button does not size itself to its children, so give it the width of its contents
        // (measured, with a character-count estimate as the floor) or every tab collapses to
        // nothing and they all draw on top of each other.
        float estimate = 28 + 34 + 10 + label.Length * 12.5f;
        button.CustomMinimumSize = new Vector2(Mathf.Max(estimate, pad.GetCombinedMinimumSize().X), 42);
        return button;
    }

    /// <summary>A tall whole-card button the caller fills (one per weapon set). Red-filled when it is
    /// the active one. Returns the button; add rows to <paramref name="content"/>.</summary>
    public static Button SetCard(bool active, Action onPressed, out VBoxContainer content)
    {
        var button = NewButton(new Vector2(0, 0), onPressed, UiSounds.Sound.Select);
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        button.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        StyleButton(button,
            Box(active ? SelectedFill : Ink, active ? SelectedBorder : Border, active ? 2 : 1),
            Box(active ? SelectedFill : HoverFill, BorderHot, 2, 2, 10),
            Box(PressFill, BorderHot),
            Box(RowFill, Hairline));
        var pad = Padding(16, 14);
        button.AddChild(pad);
        content = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        content.AddThemeConstantOverride("separation", 4);
        pad.AddChild(content);
        return button;
    }

    /// <summary>Labelled thin bar: "FIRE RATE ........ 600".</summary>
    public static Control StatBar(string label, string value, float fraction)
    {
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 3);

        var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var name = Text(label, 15, TextMuted, SemiBold);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        top.AddChild(name);
        top.AddChild(Text(value, 17, TextMain, Bold, HorizontalAlignment.Right));
        box.AddChild(top);

        var track = new Control { CustomMinimumSize = new Vector2(0, 6), MouseFilter = Control.MouseFilterEnum.Ignore };
        var back = new ColorRect { Color = Hex(0x1d1113), MouseFilter = Control.MouseFilterEnum.Ignore };
        Fill(back);
        track.AddChild(back);
        var bar = new ColorRect { Color = Accent, MouseFilter = Control.MouseFilterEnum.Ignore };
        Place(bar, 0, 0, Mathf.Clamp(fraction, 0.0f, 1.0f), 1, 0, 0, 0, 0);
        track.AddChild(bar);
        box.AddChild(track);
        return box;
    }

    /// <summary>Square colour swatch; ringed white when it is the current colour.</summary>
    public static Button Swatch(Color color, bool selected, Action onPressed)
    {
        var button = NewButton(new Vector2(38, 38), onPressed);
        StyleButton(button,
            Box(color, selected ? TextMain : Hairline, selected ? 3 : 1, 2),
            Box(color, BorderHot, 2, 2),
            Box(color, BorderHot, 3, 2),
            Box(color, Hairline, 1, 2));
        return button;
    }

    /// <summary>A whole-tile button whose content the caller fills (main-screen loadout tiles).
    /// Returns the button; add rows to <paramref name="content"/>.</summary>
    public static Button Tile(Vector2 size, Action onPressed, out VBoxContainer content)
    {
        var button = NewButton(size, onPressed);
        StyleButton(button, Box(Ink, Border), Box(HoverFill, BorderHot), Box(PressFill, BorderHot),
            Box(RowFill, Hairline));
        var pad = Padding(14, 10);
        button.AddChild(pad);
        content = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        content.AddThemeConstantOverride("separation", 0);
        pad.AddChild(content);
        return button;
    }

    /// <summary>Outlined (not filled) secondary button, e.g. RESET COLORS or CUSTOMIZE.</summary>
    public static Button Outline(string text, Action onPressed, string icon = null, int height = 46)
    {
        var button = NewButton(new Vector2(0, height), onPressed);
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        StyleButton(button, Box(Ink, Border), Box(HoverFill, BorderHot), Box(PressFill, BorderHot),
            Box(RowFill, Hairline));
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        Fill(center);
        button.AddChild(center);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 10);
        center.AddChild(row);
        if (icon != null)
        {
            var glyph = Icon(icon, 24, 0, Accent);
            glyph.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.AddChild(glyph);
        }
        row.AddChild(Text(text, 20, TextMain, Bold));
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
    /// Drop res://art/ui/menu_background.png (full screen) into the project and it is picked up
    /// automatically. The operator in the middle of the lobby is a live 3D OperatorStage now.
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
            new Vector2(0.5f, 0.62f), new Vector2(1.02f, 0.62f), radial: true));
        root.AddChild(GradientLayer(Hex(0x3a0908, 0.0f), Hex(0x3a0908, 0.85f),
            new Vector2(0.0f, 0.5f), new Vector2(0.0f, 1.0f), radial: false));

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
