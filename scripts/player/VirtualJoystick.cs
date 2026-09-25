using Godot;

/// <summary>
/// A drag-anywhere-in-its-rect movement stick. Reports its offset by driving the same
/// move_forward/back/left/right actions a keyboard would, so PlayerMovement's
/// Input.GetVector call sees it as an ordinary (if unusually smooth) input device and needs
/// no touch-specific branch of its own.
///
/// Movement in PlayerMovement is direction-only (moveDirection is Normalized() before use), so
/// there is no analog "push it halfway for half speed" - once past the deadzone, an axis is
/// simply pressed at full strength like a key would be. That is what keeps this file simple:
/// no need to fight Input.GetVector's own deadzone shaping over an analog value nothing reads.
/// </summary>
public partial class VirtualJoystick : Control
{
    public float BaseRadius = 76f;
    public float KnobRadius = 34f;
    public float Deadzone = 0.2f;
    public bool Floating = true;
    public (string Negative, string Positive) MoveAxisX = ("move_left", "move_right");
    public (string Negative, string Positive) MoveAxisY = ("move_forward", "move_back");

    private int _touchIndex = -1;
    private Vector2 _origin;
    private Panel _base = null!;
    private Panel _knob = null!;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        // The hit region is exactly 2*BaseRadius square. Read from the field rather than Size:
        // whoever instantiates this (TouchControls) sets Position/Size via anchors and offsets
        // right after AddChild, which is not guaranteed to be resolved yet on this same frame.
        CustomMinimumSize = new Vector2(BaseRadius, BaseRadius) * 2;

        _base = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        _base.AddThemeStyleboxOverride("panel", MenuStyle.Box(MenuStyle.Ink, MenuStyle.Border, 2, (int)BaseRadius));
        AddChild(_base);

        _knob = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        _knob.AddThemeStyleboxOverride("panel", MenuStyle.Box(MenuStyle.Accent, MenuStyle.BorderHot, 2, (int)KnobRadius, 6));
        AddChild(_knob);

        _origin = new Vector2(BaseRadius, BaseRadius);
        DrawAt(_origin, _origin);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventScreenTouch touch)
        {
            if (touch.Pressed && _touchIndex == -1)
            {
                _touchIndex = touch.Index;
                if (Floating) _origin = touch.Position;
                UpdateKnob(touch.Position);
            }
            else if (!touch.Pressed && touch.Index == _touchIndex)
            {
                _touchIndex = -1;
                if (Floating) _origin = new Vector2(BaseRadius, BaseRadius);
                DrawAt(_origin, _origin);
                Release();
            }
        }
        else if (@event is InputEventScreenDrag drag && drag.Index == _touchIndex)
        {
            UpdateKnob(drag.Position);
        }
    }

    private void UpdateKnob(Vector2 touchPosition)
    {
        Vector2 offset = touchPosition - _origin;
        float length = Mathf.Min(offset.Length(), BaseRadius);
        Vector2 clamped = length > 0.001f ? offset.Normalized() * length : Vector2.Zero;
        DrawAt(_origin, _origin + clamped);

        float magnitude = clamped.Length() / BaseRadius; // 0..1
        if (magnitude < Deadzone) { Release(); return; }
        Vector2 direction = clamped.Normalized();
        PressAxis(MoveAxisX, direction.X);
        PressAxis(MoveAxisY, direction.Y);
    }

    /// <summary>Positions the base at `origin` and the knob at `knob`, both in this control's
    /// own local space - Godot passes _gui_input positions already local to the control, so no
    /// further transform is needed.</summary>
    private void DrawAt(Vector2 origin, Vector2 knob)
    {
        _base.Position = origin - new Vector2(BaseRadius, BaseRadius);
        _base.Size = new Vector2(BaseRadius, BaseRadius) * 2;
        _knob.Position = knob - new Vector2(KnobRadius, KnobRadius);
        _knob.Size = new Vector2(KnobRadius, KnobRadius) * 2;
    }

    private void PressAxis((string Negative, string Positive) axis, float value)
    {
        if (value < -0.001f) { Input.ActionPress(axis.Negative); Input.ActionRelease(axis.Positive); }
        else if (value > 0.001f) { Input.ActionPress(axis.Positive); Input.ActionRelease(axis.Negative); }
        else { Input.ActionRelease(axis.Negative); Input.ActionRelease(axis.Positive); }
    }

    private void Release()
    {
        Input.ActionRelease(MoveAxisX.Negative);
        Input.ActionRelease(MoveAxisX.Positive);
        Input.ActionRelease(MoveAxisY.Negative);
        Input.ActionRelease(MoveAxisY.Positive);
    }
}
