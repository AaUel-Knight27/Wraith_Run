using Godot;

/// <summary>
/// A player's avatar: one of 10 emblems on one of 8 colours, drawn as a tinted icon on a dark plate
/// with a coloured frame. It reuses the square icons already in res://art/ui/icons, so it needs no
/// new art - and because an avatar is just two small numbers (see PlayerCard), sending one to
/// another player costs nothing and can never fail to load.
///
/// The palette sizes live in AvatarCatalog (EmblemCount / ColorCount); keep the arrays below the
/// same length.
/// </summary>
public static class AvatarView
{
    /// <summary>Icon file names (without .svg), all 24x24 so they sit square on the plate.</summary>
    public static readonly string[] EmblemIcons =
    {
        "class-operator", "class-demolisher", "class-driver", "class-fixer", "class-medic",
        "helmet-gear", "crosshair", "weapon", "user", "bot",
    };

    public static readonly string[] EmblemNames =
    {
        "Operator", "Demolisher", "Driver", "Fixer", "Medic",
        "Helmet", "Crosshair", "Weapon", "Soldier", "Robot",
    };

    public static readonly Color[] Palette =
    {
        new(0.93f, 0.07f, 0.09f), // crimson (the game's accent)
        new(0.91f, 0.44f, 0.16f), // orange
        new(0.88f, 0.71f, 0.23f), // amber
        new(0.30f, 0.76f, 0.39f), // green
        new(0.17f, 0.72f, 0.69f), // teal
        new(0.29f, 0.55f, 0.94f), // blue
        new(0.60f, 0.42f, 0.94f), // violet
        new(0.81f, 0.82f, 0.84f), // steel
    };

    public static string IconAt(int emblem) => EmblemIcons[AvatarCatalog.ClampEmblem(emblem)];

    public static Color ColorAt(int color) => Palette[AvatarCatalog.ClampColor(color)];

    /// <summary>A square avatar of the given pixel size.</summary>
    public static Control Create(int emblem, int color, int size)
    {
        Color tint = ColorAt(color);

        var plate = new PanelContainer
        {
            CustomMinimumSize = new Vector2(size, size),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        plate.AddThemeStyleboxOverride("panel",
            MenuStyle.Box(tint.Darkened(0.72f), tint, size >= 64 ? 3 : 2, 4));

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        plate.AddChild(center);
        int glyph = Mathf.RoundToInt(size * 0.60f);
        center.AddChild(MenuStyle.Icon(IconAt(emblem), glyph, 0, tint.Lightened(0.30f)));
        return plate;
    }

    /// <summary>The avatar for a card.</summary>
    public static Control Create(PlayerCard card, int size) => Create(card.AvatarEmblem, card.AvatarColor, size);

    /// <summary>Placeholder for somebody whose card has not arrived yet.</summary>
    public static Control CreateUnknown(int size)
    {
        var plate = new PanelContainer
        {
            CustomMinimumSize = new Vector2(size, size),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
        };
        plate.AddThemeStyleboxOverride("panel", MenuStyle.Box(MenuStyle.RowFill, MenuStyle.Hairline, 2, 4));
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        plate.AddChild(center);
        center.AddChild(MenuStyle.Icon("user", Mathf.RoundToInt(size * 0.60f), 0, MenuStyle.TextMuted));
        return plate;
    }
}
