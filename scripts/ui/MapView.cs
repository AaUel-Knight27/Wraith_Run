using Godot;
using System.Collections.Generic;

/// <summary>
/// A top-down map of whatever arena is loaded, for the pause menu's MAP screen.
///
/// There is no hand-made map image in the project, so this draws the map from the map itself: every
/// StaticBody3D collision shape in the running scene is flattened onto the ground plane and drawn as
/// a filled outline. That means it is always correct, needs no art, and works unchanged for Depot,
/// Ashfields and any future map - the only requirement is that walls and cover are StaticBody3D
/// with box / cylinder / capsule / sphere / convex shapes, which is how every map here is built.
///
/// Orientation: -Z (Godot's "forward") is UP on the map, +X is right, so the arrow of a player who is
/// facing straight ahead in world space points up.
///
/// Players: you are always shown. Teammates are shown in Team Deathmatch. Enemies are deliberately
/// NEVER drawn - a live enemy radar in a pause menu would be a wall-hack.
/// </summary>
public partial class MapView : Control
{
    /// <summary>Shapes shorter than this (metres) are floor slabs, not cover: the arena's ground is a
    /// 0.4 m box, its walls and props are all taller.</summary>
    private const float SolidMinHeight = 0.8f;

    private const int MaxShapes = 4000;
    private const float Padding = 26.0f;

    private static readonly Color BackgroundColor = new(0.03f, 0.03f, 0.04f, 1.0f);
    private static readonly Color FloorColor = new(0.11f, 0.10f, 0.10f, 1.0f);
    private static readonly Color SolidFill = new(0.36f, 0.27f, 0.25f, 1.0f);
    private static readonly Color SolidEdge = new(0.62f, 0.42f, 0.36f, 1.0f);
    private static readonly Color GridColor = new(1.0f, 1.0f, 1.0f, 0.045f);

    private readonly List<Vector2[]> _floors = new();
    private readonly List<Vector2[]> _solids = new();
    private Rect2 _bounds;
    private bool _hasGeometry;
    private double _redrawTimer;

    /// <summary>The local player - drawn as "you" and the reference for who counts as a teammate.</summary>
    public CharacterBody3D? LocalPlayer { get; set; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        CustomMinimumSize = new Vector2(0, 260);
        Collect(GetTree().CurrentScene);
    }

    public override void _Process(double delta)
    {
        // Markers move, so redraw ~10x a second while the map is actually on screen. Retained-mode
        // canvas: not calling QueueRedraw costs nothing.
        if (!IsVisibleInTree()) return;
        _redrawTimer += delta;
        if (_redrawTimer < 0.1) return;
        _redrawTimer = 0.0;
        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }

    // -------------------------------------------------------------------------------------------
    // Geometry
    // -------------------------------------------------------------------------------------------

    private void Collect(Node? root)
    {
        _floors.Clear();
        _solids.Clear();
        _hasGeometry = false;
        if (root == null) return;

        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        int seen = 0;
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0 && seen < MaxShapes)
        {
            Node node = stack.Pop();
            foreach (Node child in node.GetChildren()) stack.Push(child);

            if (node is not CollisionShape3D shapeNode || shapeNode.Disabled || shapeNode.Shape == null) continue;
            if (shapeNode.GetParent() is not StaticBody3D) continue;
            if (!shapeNode.IsInsideTree()) continue;
            seen++;

            Vector3[] corners = ShapePoints(shapeNode.Shape);
            if (corners.Length == 0) continue;

            Transform3D world = shapeNode.GlobalTransform;
            var flat = new Vector2[corners.Length];
            float lowY = float.MaxValue, highY = float.MinValue;
            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 p = world * corners[i];
                flat[i] = new Vector2(p.X, p.Z);
                lowY = Mathf.Min(lowY, p.Y);
                highY = Mathf.Max(highY, p.Y);
            }

            Vector2[] hull = Hull(flat);
            if (hull.Length < 3 || Mathf.Abs(Area(hull)) < 0.0001f) continue;

            foreach (Vector2 p in hull)
            {
                minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X);
                minZ = Mathf.Min(minZ, p.Y); maxZ = Mathf.Max(maxZ, p.Y);
            }
            if (highY - lowY >= SolidMinHeight) _solids.Add(hull);
            else _floors.Add(hull);
        }

        if (minX > maxX || minZ > maxZ) return;
        _bounds = new Rect2(minX, minZ, Mathf.Max(maxX - minX, 1.0f), Mathf.Max(maxZ - minZ, 1.0f)).Grow(1.0f);
        _hasGeometry = true;
    }

    /// <summary>Local-space points that outline the shape (its top and bottom rings for round shapes,
    /// its 8 corners for a box), so the hull of their world-space XZ projection is the footprint even
    /// when the shape is rotated. Empty for shapes that should not appear on a map.</summary>
    private static Vector3[] ShapePoints(Shape3D shape)
    {
        switch (shape)
        {
            case BoxShape3D box:
                return BoxCorners(-box.Size * 0.5f, box.Size * 0.5f);
            case CylinderShape3D cylinder:
                return Rings(cylinder.Radius, cylinder.Height * 0.5f);
            case CapsuleShape3D capsule:
                return Rings(capsule.Radius, capsule.Height * 0.5f);
            case SphereShape3D sphere:
                return Rings(sphere.Radius, sphere.Radius);
            case ConvexPolygonShape3D or ConcavePolygonShape3D:
                Mesh? debug = shape.GetDebugMesh();
                if (debug == null) return System.Array.Empty<Vector3>();
                Aabb aabb = debug.GetAabb();
                return BoxCorners(aabb.Position, aabb.End);
            default:
                // Height maps, world-boundary planes and the like are not "cover".
                return System.Array.Empty<Vector3>();
        }
    }

    private static Vector3[] BoxCorners(Vector3 low, Vector3 high) => new[]
    {
        new Vector3(low.X, low.Y, low.Z), new Vector3(high.X, low.Y, low.Z),
        new Vector3(low.X, high.Y, low.Z), new Vector3(high.X, high.Y, low.Z),
        new Vector3(low.X, low.Y, high.Z), new Vector3(high.X, low.Y, high.Z),
        new Vector3(low.X, high.Y, high.Z), new Vector3(high.X, high.Y, high.Z),
    };

    private static Vector3[] Rings(float radius, float halfHeight)
    {
        const int Steps = 16;
        var points = new Vector3[Steps * 2];
        for (int i = 0; i < Steps; i++)
        {
            float angle = Mathf.Tau * i / Steps;
            float x = Mathf.Cos(angle) * radius, z = Mathf.Sin(angle) * radius;
            points[i] = new Vector3(x, -halfHeight, z);
            points[Steps + i] = new Vector3(x, halfHeight, z);
        }
        return points;
    }

    /// <summary>Convex hull with the closing duplicate point Godot appends removed - a polygon with a
    /// repeated first/last vertex is rejected by the triangulator when it is filled.</summary>
    private static Vector2[] Hull(Vector2[] points)
    {
        Vector2[] hull = Geometry2D.ConvexHull(points);
        if (hull.Length > 1 && hull[0].DistanceSquaredTo(hull[^1]) < 0.000001f)
        {
            var trimmed = new Vector2[hull.Length - 1];
            System.Array.Copy(hull, trimmed, trimmed.Length);
            return trimmed;
        }
        return hull;
    }

    private static float Area(Vector2[] polygon)
    {
        float sum = 0.0f;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Length];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum * 0.5f;
    }

    // -------------------------------------------------------------------------------------------
    // Drawing
    // -------------------------------------------------------------------------------------------

    private float _scale = 1.0f;
    private Vector2 _origin;

    private Vector2 ToScreen(Vector2 world) => _origin + (world - _bounds.Position) * _scale;

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), BackgroundColor);
        DrawRect(new Rect2(Vector2.Zero, Size), MenuStyle.Hairline, filled: false, width: 2.0f);

        if (!_hasGeometry)
        {
            DrawCenteredText("No map geometry to draw.", Size * 0.5f, MenuStyle.TextMuted, 20);
            return;
        }

        float availableX = Mathf.Max(Size.X - Padding * 2.0f, 1.0f);
        float availableY = Mathf.Max(Size.Y - Padding * 2.0f, 1.0f);
        _scale = Mathf.Min(availableX / _bounds.Size.X, availableY / _bounds.Size.Y);
        Vector2 drawn = _bounds.Size * _scale;
        _origin = (Size - drawn) * 0.5f;

        DrawRect(new Rect2(_origin, drawn), FloorColor);
        DrawGrid(drawn);
        foreach (Vector2[] floor in _floors) DrawShape(floor, new Color(0.15f, 0.14f, 0.13f, 1.0f), null);
        foreach (Vector2[] solid in _solids) DrawShape(solid, SolidFill, SolidEdge);
        DrawRect(new Rect2(_origin, drawn), MenuStyle.Border, filled: false, width: 2.0f);

        DrawPlayers();
        DrawCenteredText("N", new Vector2(Size.X * 0.5f, 14.0f), MenuStyle.TextDim, 16);
    }

    private void DrawGrid(Vector2 drawn)
    {
        // A line every 10 metres, anchored to world zero so the grid means something.
        const float Spacing = 10.0f;
        for (float x = Mathf.Ceil(_bounds.Position.X / Spacing) * Spacing; x < _bounds.End.X; x += Spacing)
        {
            Vector2 a = ToScreen(new Vector2(x, _bounds.Position.Y));
            DrawLine(a, new Vector2(a.X, a.Y + drawn.Y), GridColor, 1.0f);
        }
        for (float z = Mathf.Ceil(_bounds.Position.Y / Spacing) * Spacing; z < _bounds.End.Y; z += Spacing)
        {
            Vector2 a = ToScreen(new Vector2(_bounds.Position.X, z));
            DrawLine(a, new Vector2(a.X + drawn.X, a.Y), GridColor, 1.0f);
        }
    }

    private void DrawShape(Vector2[] worldPolygon, Color fill, Color? edge)
    {
        var screen = new Vector2[worldPolygon.Length];
        for (int i = 0; i < screen.Length; i++) screen[i] = ToScreen(worldPolygon[i]);
        DrawColoredPolygon(screen, fill);
        if (edge == null) return;
        var loop = new Vector2[screen.Length + 1];
        screen.CopyTo(loop, 0);
        loop[^1] = screen[0];
        DrawPolyline(loop, edge.Value, 1.0f);
    }

    private void DrawPlayers()
    {
        if (LocalPlayer == null || !GodotObject.IsInstanceValid(LocalPlayer)) return;
        MatchManager? match = MatchManager.Instance;
        bool teamMode = match != null && match.GameMode == MatchManager.Mode.TeamDeathmatch;
        int myPeer = LocalPlayer.GetMultiplayerAuthority();
        int myTeam = teamMode ? match!.GetTeam(myPeer) : -1;

        foreach (Node node in GetTree().GetNodesInGroup("players"))
        {
            if (node is not CharacterBody3D player || player == LocalPlayer) continue;
            if (!teamMode || myTeam < 0) continue;
            int peer = player.GetMultiplayerAuthority();
            if (match!.GetTeam(peer) != myTeam) continue;
            Health? health = player.GetNodeOrNull<Health>("Health");
            if (health != null && health.IsDead) continue;
            DrawMarker(player, MatchManager.TeamColor(myTeam), 6.0f, null);
        }

        Health? own = LocalPlayer.GetNodeOrNull<Health>("Health");
        bool dead = own != null && own.IsDead;
        DrawMarker(LocalPlayer, dead ? new Color(1, 1, 1, 0.35f) : Colors.White, 8.0f, MenuStyle.Accent);
    }

    private void DrawMarker(CharacterBody3D player, Color fill, float size, Color? outline)
    {
        Vector3 position = player.GlobalPosition;
        Vector3 forward = -player.GlobalTransform.Basis.Z;
        Vector2 direction = new Vector2(forward.X, forward.Z);
        direction = direction.LengthSquared() > 0.0001f ? direction.Normalized() : new Vector2(0, -1);
        Vector2 side = new(-direction.Y, direction.X);

        Vector2 center = ToScreen(new Vector2(position.X, position.Z));
        var triangle = new[]
        {
            center + direction * size * 1.5f,
            center - direction * size + side * size * 0.9f,
            center - direction * size * 0.4f,
            center - direction * size - side * size * 0.9f,
        };
        DrawColoredPolygon(triangle, fill);
        if (outline != null)
            DrawPolyline(new[] { triangle[0], triangle[1], triangle[2], triangle[3], triangle[0] }, outline.Value, 2.0f);
    }

    private void DrawCenteredText(string text, Vector2 center, Color color, int fontSize)
    {
        Font font = MenuStyle.SemiBold;
        Vector2 measured = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize);
        DrawString(font, new Vector2(center.X - measured.X * 0.5f, center.Y + measured.Y * 0.25f), text,
            HorizontalAlignment.Left, -1, fontSize, color);
    }
}
