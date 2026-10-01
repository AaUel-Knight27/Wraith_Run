using Godot;
using System.Collections.Generic;

/// <summary>A point a drill wants drawn on the map (a reported contact, an objective, a friend...).</summary>
public sealed class MapMarker
{
    public Vector3 Position;
    public Color Color = Colors.White;
    public string Label = "";
    public float Radius = 7.0f;
}

/// <summary>
/// The map overlay ([M]). Top-down, north (-Z) up. Draws every TrainingZone as a named, coloured rectangle, the
/// trainee as an arrow, friendly Mannys as green dots, and whatever markers the active drill registers on
/// TrainingSession.MapMarkers. With no zones in the scene it becomes a radar centred on the trainee.
/// It is also the reading surface for the map-reading drill: it never shows enemies unless the drill reports them.
/// </summary>
public partial class TrainingMapView : Control
{
    private const float RadarHalfExtent = 70.0f;

    private readonly List<Label> _labels = new();
    private Rect2 _square;
    private Vector2 _worldMin;
    private Vector2 _worldMax;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        MenuStyle.Fill(this);
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        QueueRedraw();
    }

    private Vector2 ToMap(Vector3 world)
    {
        float u = (world.X - _worldMin.X) / Mathf.Max(0.001f, _worldMax.X - _worldMin.X);
        float v = (world.Z - _worldMin.Y) / Mathf.Max(0.001f, _worldMax.Y - _worldMin.Y);
        return _square.Position + new Vector2(u * _square.Size.X, v * _square.Size.Y);
    }

    public override void _Draw()
    {
        TrainingSession? session = TrainingSession.Instance;
        if (session == null || !IsInstanceValid(session.Player)) return;

        Vector2 view = Size;
        float side = Mathf.Min(view.X, view.Y) * 0.78f;
        _square = new Rect2((view - new Vector2(side, side)) * 0.5f, new Vector2(side, side));

        DrawRect(new Rect2(Vector2.Zero, view), new Color(0, 0, 0, 0.45f));
        DrawRect(_square, new Color(0.03f, 0.04f, 0.05f, 0.92f));
        DrawRect(_square, MenuStyle.Border, false, 2.0f);

        List<TrainingZone> zones = TrainingAnchors.GetZones(session.GetTree());
        ComputeBounds(session, zones);

        int labelIndex = 0;
        foreach (TrainingZone zone in zones)
        {
            Vector3 c = zone.GlobalPosition;
            Vector2 a = ToMap(new Vector3(c.X - zone.HalfSize.X, 0, c.Z - zone.HalfSize.Y));
            Vector2 b = ToMap(new Vector3(c.X + zone.HalfSize.X, 0, c.Z + zone.HalfSize.Y));
            var rect = new Rect2(a, b - a);
            DrawRect(rect, new Color(zone.ZoneColor.R, zone.ZoneColor.G, zone.ZoneColor.B, 0.22f));
            DrawRect(rect, zone.ZoneColor, false, 2.0f);
            PlaceLabel(labelIndex++, zone.ZoneName, rect.GetCenter() - new Vector2(50, 12), zone.ZoneColor, 22);
        }

        foreach (Node node in session.GetTree().GetNodesInGroup("training_actor"))
            if (node is TrainingActor actor && !actor.IsDead && actor.Side == TrainingActor.FactionType.Friend)
                DrawCircle(ToMap(actor.GlobalPosition), 6.0f, new Color(0.35f, 1.0f, 0.5f));

        foreach (MapMarker marker in session.MapMarkers)
        {
            Vector2 p = ToMap(marker.Position);
            DrawCircle(p, marker.Radius, marker.Color);
            if (!string.IsNullOrEmpty(marker.Label)) PlaceLabel(labelIndex++, marker.Label, p + new Vector2(10, -10), marker.Color, 18);
        }

        DrawPlayer(session, ToMap(session.Player.GlobalPosition));
        PlaceLabel(labelIndex++, "N", new Vector2(_square.GetCenter().X - 6, _square.Position.Y - 34), MenuStyle.TextMain, 24);

        for (int i = labelIndex; i < _labels.Count; i++) _labels[i].Visible = false;
    }

    private void ComputeBounds(TrainingSession session, List<TrainingZone> zones)
    {
        if (zones.Count == 0)
        {
            Vector3 p = session.Player.GlobalPosition;
            _worldMin = new Vector2(p.X - RadarHalfExtent, p.Z - RadarHalfExtent);
            _worldMax = new Vector2(p.X + RadarHalfExtent, p.Z + RadarHalfExtent);
            return;
        }

        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (TrainingZone zone in zones)
        {
            Vector3 c = zone.GlobalPosition;
            min = new Vector2(Mathf.Min(min.X, c.X - zone.HalfSize.X), Mathf.Min(min.Y, c.Z - zone.HalfSize.Y));
            max = new Vector2(Mathf.Max(max.X, c.X + zone.HalfSize.X), Mathf.Max(max.Y, c.Z + zone.HalfSize.Y));
        }
        // Keep the map square so distances are not distorted, and leave a margin.
        Vector2 centre = (min + max) * 0.5f;
        float half = Mathf.Max(max.X - min.X, max.Y - min.Y) * 0.5f + 8.0f;
        _worldMin = centre - new Vector2(half, half);
        _worldMax = centre + new Vector2(half, half);
    }

    private void DrawPlayer(TrainingSession session, Vector2 at)
    {
        Vector3 f3 = -session.Player.GlobalTransform.Basis.Z;
        var forward = new Vector2(f3.X, f3.Z);
        if (forward.LengthSquared() < 0.0001f) forward = new Vector2(0, -1);
        forward = forward.Normalized();
        var side = new Vector2(-forward.Y, forward.X);
        Vector2[] triangle = { at + forward * 15.0f, at - forward * 9.0f + side * 8.0f, at - forward * 9.0f - side * 8.0f };
        DrawColoredPolygon(triangle, new Color(1.0f, 0.95f, 0.5f));
    }

    private void PlaceLabel(int index, string text, Vector2 position, Color color, int size)
    {
        while (_labels.Count <= index)
        {
            Label created = MenuStyle.Text("", size, color, MenuStyle.SemiBold);
            AddChild(created);
            _labels.Add(created);
        }
        Label label = _labels[index];
        label.Visible = true;
        label.Text = text;
        label.Position = position;
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeFontSizeOverride("font_size", size);
    }
}
