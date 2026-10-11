using Godot;

/// <summary>
/// The game's mouse pointer: a black arrow with a thin white edge, drawn here in code (no image file to
/// import). The white edge keeps it visible on the game's dark menus, where a plain black arrow would vanish.
/// Applied once at start-up by SettingsManager, for the normal arrow and for the hand shown over buttons.
/// Phones have no mouse pointer, so nothing is set there.
/// </summary>
public static class GameCursor
{
    private const int Size = 32;
    private const float Radius = 1.6f;   // rounds the arrow's corners
    private const float Edge = 1.4f;     // width of the white outline
    private static readonly Vector2 Hotspot = new(4, 4); // the arrow's tip

    // The arrow's corners: tip, right point, inner notch, bottom point (pixels of the 32x32 image).
    private static readonly Vector2[] Shape =
    {
        new(5.0f, 5.0f), new(26.0f, 16.3f), new(14.9f, 17.0f), new(8.0f, 28.0f),
    };

    private static Texture2D? _texture;

    public static void Apply()
    {
        if (OS.HasFeature("mobile")) return;
        _texture ??= Build();
        Input.SetCustomMouseCursor(_texture, Input.CursorShape.Arrow, Hotspot);
        Input.SetCustomMouseCursor(_texture, Input.CursorShape.PointingHand, Hotspot);
    }

    private static Texture2D Build()
    {
        Image image = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float d = SignedDistance(new Vector2(x + 0.5f, y + 0.5f));
                float fill = Mathf.Clamp(Radius - d + 0.5f, 0.0f, 1.0f);        // the black arrow
                float whole = Mathf.Clamp(Radius + Edge - d + 0.5f, 0.0f, 1.0f); // arrow plus its white edge
                if (whole <= 0.0f) { image.SetPixel(x, y, new Color(0, 0, 0, 0)); continue; }
                float white = (whole - fill) / whole; // how much of this pixel is the white edge
                image.SetPixel(x, y, new Color(white, white, white, whole));
            }
        }
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>Distance from a point to the arrow's outline: negative inside, positive outside.</summary>
    private static float SignedDistance(Vector2 p)
    {
        float best = float.MaxValue;
        bool inside = false;
        for (int i = 0, j = Shape.Length - 1; i < Shape.Length; j = i++)
        {
            Vector2 a = Shape[j];
            Vector2 b = Shape[i];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            Vector2 ab = b - a;
            float t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0.0f, 1.0f);
            best = Mathf.Min(best, p.DistanceTo(a + ab * t));
        }
        return inside ? -best : best;
    }
}
