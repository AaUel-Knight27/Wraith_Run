using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// On-screen control overlay for touch/Android - virtual joystick, ADS/fire/jump/crouch/sprint/
/// reload/weapon-switch buttons, and (optionally) gyroscope look. Built entirely in code, the
/// same convention PlayerHud and LanMenu already use, so there is no separate .tscn to keep in
/// sync with this script.
///
/// Only ever built for the local, authoritative player, and only when SettingsManager says touch
/// controls should be showing (see SettingsManager.ShouldShowTouchControls) - a remote peer's
/// copy of this node does nothing, same as PlayerHud.
///
/// Everything here drives the SAME Input actions the keyboard already uses (move_forward/back/
/// left/right, jump, crouch, sprint, aim, fire, reload, weapon_N). Nothing in PlayerMovement or
/// WeaponSwitcher needed to change to accept it - as far as those scripts are concerned this is
/// just another input device. The one exception is look: mouse motion bypasses the action system
/// entirely, so touch-drag look is wired directly into PlayerMovement instead of going through an
/// action (see ApplyLookDelta / ApplyGyroLookDelta there). There is deliberately no button here
/// for Interact or a world-item Auto Pickup - neither has a gameplay system behind it yet in this
/// build, so a button for either would just do nothing. Wire those in here once they exist.
/// </summary>
public partial class TouchControls : CanvasLayer
{
    private const float EdgeMargin = 20f;
    private const float ClusterGap = 12f;

    private CharacterBody3D _player = null!;
    private PlayerMovement _movement = null!;
    private Control _root = null!;
    private bool _sprintOn;
    private bool _adsOn;
    private int _weaponCursor;

    public override void _Ready()
    {
        _player = (CharacterBody3D)GetParent();
        if (!_player.IsMultiplayerAuthority())
        {
            SetProcess(false);
            return;
        }

        _movement = (PlayerMovement)_player;
        if (!SettingsManager.Instance.ShouldShowTouchControls())
        {
            SetProcess(false);
            return;
        }

        Build();
    }

    public override void _Process(double delta)
    {
        SettingsManager settings = SettingsManager.Instance;
        if (settings.Gyroscope == SettingsManager.GyroMode.Off) return;
        if (settings.Gyroscope == SettingsManager.GyroMode.AdsOnly && !_movement.IsAiming) return;

        // A phone's gyroscope reports rotation *rate* in rad/s per axis, not a per-event screen
        // delta the way mouse motion / screen drags do - so this is scaled by delta directly
        // instead of going through _UnhandledInput's event-based Relative.
        Vector3 gyro = Input.GetGyroscope();
        float step = (float)delta * settings.GyroSensitivity;
        _movement.ApplyGyroLookDelta(gyro.Y * step, -gyro.X * step);
    }

    private void Build()
    {
        SettingsManager settings = SettingsManager.Instance;
        float scale = settings.ButtonScale;
        // Right-handed (default): joystick bottom-left, fire cluster bottom-right. Mirrored for
        // left-handed play. The utility stack (sprint/weapon-switch/melee) lives on the joystick
        // side, since the movement thumb has more idle moments mid-firefight than the fire thumb.
        bool joystickRight = settings.LeftHandedLayout;
        bool clusterRight = !joystickRight;

        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(_root);
        _root.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(settings.ButtonOpacity, 0.1f, 1.0f));
        AddChild(_root);

        BuildJoystick(settings, joystickRight, scale);
        BuildFireCluster(clusterRight, scale);
        BuildUtilityStack(settings, joystickRight, scale);
    }

    private void BuildJoystick(SettingsManager settings, bool right, float scale)
    {
        float baseRadius = 92f * scale;
        var joystick = new VirtualJoystick
        {
            BaseRadius = baseRadius,
            KnobRadius = 40f * scale,
            Deadzone = settings.JoystickDeadzone,
            Floating = settings.JoystickFloating,
        };
        _root.AddChild(joystick);
        PlaceCorner(joystick, right, bottom: true, baseRadius * 2, baseRadius * 2, EdgeMargin, EdgeMargin);
    }

    /// <summary>Fire, ADS, Jump, Crouch and Reload - the buttons the firing thumb owns.</summary>
    private void BuildFireCluster(bool right, float scale)
    {
        SettingsManager settings = SettingsManager.Instance;
        float fireSize = 108f * scale;
        float adsSize = 86f * scale;
        float smallSize = 72f * scale;

        var fire = NewButton("FIRE", "crosshair", fireSize);
        PlaceCorner(fire, right, bottom: true, fireSize, fireSize, EdgeMargin, EdgeMargin);
        WireHoldOrTap(fire, "fire", () => settings.HoldToFire);

        var ads = NewButton("ADS", null, adsSize);
        PlaceCorner(ads, right, bottom: true, adsSize, adsSize,
            EdgeMargin + fireSize + ClusterGap, EdgeMargin + (fireSize - adsSize) * 0.5f);
        ads.ButtonDown += () => OnAdsDown(settings);
        ads.ButtonUp += () => { if (settings.HoldToAds) Input.ActionRelease("aim"); };

        float row2Y = EdgeMargin + fireSize + ClusterGap;
        var jump = NewButton("JUMP", null, smallSize);
        PlaceCorner(jump, right, bottom: true, smallSize, smallSize, EdgeMargin, row2Y);
        jump.ButtonDown += () => Input.ActionPress("jump");
        jump.ButtonUp += () => Input.ActionRelease("jump");

        var crouch = NewButton("CROUCH", null, smallSize);
        PlaceCorner(crouch, right, bottom: true, smallSize, smallSize,
            EdgeMargin + fireSize + ClusterGap, row2Y);
        crouch.ButtonDown += () => Input.ActionPress("crouch");
        crouch.ButtonUp += () => Input.ActionRelease("crouch");

        var reload = NewButton("RELOAD", null, smallSize);
        PlaceCorner(reload, right, bottom: true, smallSize, smallSize, EdgeMargin, row2Y + smallSize + ClusterGap);
        reload.ButtonDown += () => Pulse("reload");
    }

    /// <summary>Sprint (hidden if Auto Sprint is on), weapon-switch and quick-melee, stacked at
    /// the vertical middle of the joystick side.</summary>
    private void BuildUtilityStack(SettingsManager settings, bool right, float scale)
    {
        float size = 64f * scale;
        var slots = new List<Button>();

        if (!settings.AutoSprint)
        {
            var sprint = NewButton("SPRINT", null, size);
            sprint.ButtonDown += () => OnSprintTap(sprint);
            slots.Add(sprint);
        }

        var swap = NewButton("SWAP", "rifle", size);
        swap.ButtonDown += () => Pulse(NextLoadoutAction());
        slots.Add(swap);

        var melee = NewButton("MELEE", null, size);
        melee.ButtonDown += () => Pulse(MeleeLoadoutAction());
        slots.Add(melee);

        float totalHeight = slots.Count * size + (slots.Count - 1) * ClusterGap;
        float startOffset = -totalHeight * 0.5f;
        for (int i = 0; i < slots.Count; i++)
        {
            float top = startOffset + i * (size + ClusterGap);
            MenuStyle.Place(slots[i], right ? 1f : 0f, 0.5f, right ? 1f : 0f, 0.5f,
                right ? -(EdgeMargin + size) : EdgeMargin, top,
                right ? -EdgeMargin : EdgeMargin + size, top + size);
        }
    }

    // ---- action wiring -------------------------------------------------------------------------

    private void OnAdsDown(SettingsManager settings)
    {
        if (settings.HoldToAds) { Input.ActionPress("aim"); return; }
        _adsOn = !_adsOn;
        if (_adsOn) Input.ActionPress("aim"); else Input.ActionRelease("aim");
    }

    private void OnSprintTap(Button button)
    {
        _sprintOn = !_sprintOn;
        if (_sprintOn) Input.ActionPress("sprint"); else Input.ActionRelease("sprint");
        button.Modulate = _sprintOn ? MenuStyle.BorderHot : Colors.White;
    }

    private void WireHoldOrTap(Button button, string action, Func<bool> holdMode)
    {
        button.ButtonDown += () => { if (holdMode()) Input.ActionPress(action); else Pulse(action); };
        button.ButtonUp += () => { if (holdMode()) Input.ActionRelease(action); };
    }

    /// <summary>weapon_N action for the next slot in WeaponSwitcher's loadout, cycling back to 0
    /// at the end. Reads WeaponSwitcher.Loadout directly (made internal for this) instead of
    /// hardcoding a count, so this keeps working if the loadout is ever reordered or extended.</summary>
    private string NextLoadoutAction()
    {
        _weaponCursor = (_weaponCursor + 1) % WeaponSwitcher.Loadout.Length;
        return $"weapon_{_weaponCursor + 1}";
    }

    /// <summary>Jumps straight to whichever loadout slot is melee (the Karambit) rather than
    /// cycling to it - a dedicated "quick melee" button is only useful if it is actually quick.</summary>
    private string MeleeLoadoutAction()
    {
        int index = Array.IndexOf(WeaponSwitcher.Loadout, "Karambit");
        if (index < 0) index = 0; // Loadout changed and no longer has a melee entry - stay safe.
        _weaponCursor = index;
        return $"weapon_{index + 1}";
    }

    /// <summary>One-frame press so Input.IsActionJustPressed picks it up exactly like a key tap,
    /// for actions read that way (reload, weapon switching, tap-fire, tap-ADS).</summary>
    private void Pulse(string action)
    {
        Input.ActionPress(action);
        CallDeferred(nameof(ReleaseAction), action);
    }

    private void ReleaseAction(string action) => Input.ActionRelease(action);

    // ---- building blocks -----------------------------------------------------------------------

    private Button NewButton(string label, string icon, float size)
    {
        int radius = (int)(size * 0.5f);
        var button = new Button
        {
            CustomMinimumSize = new Vector2(size, size),
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        button.AddThemeStyleboxOverride("normal", MenuStyle.Box(MenuStyle.Ink, MenuStyle.Border, 2, radius));
        button.AddThemeStyleboxOverride("hover", MenuStyle.Box(MenuStyle.HoverFill, MenuStyle.BorderHot, 2, radius));
        button.AddThemeStyleboxOverride("pressed", MenuStyle.Box(MenuStyle.PressFill, MenuStyle.BorderHot, 2, radius, 10));
        button.AddThemeStyleboxOverride("hover_pressed", MenuStyle.Box(MenuStyle.PressFill, MenuStyle.BorderHot, 2, radius, 10));
        button.AddThemeStyleboxOverride("disabled", MenuStyle.Box(MenuStyle.Ink, MenuStyle.Hairline, 2, radius));

        if (!string.IsNullOrEmpty(icon))
        {
            var iconRect = MenuStyle.Icon(icon, (int)(size * 0.46f));
            button.AddChild(iconRect);
            MenuStyle.Place(iconRect, 0.5f, 0.5f, 0.5f, 0.5f,
                -iconRect.CustomMinimumSize.X * 0.5f, -iconRect.CustomMinimumSize.Y * 0.5f,
                iconRect.CustomMinimumSize.X * 0.5f, iconRect.CustomMinimumSize.Y * 0.5f);
        }
        else
        {
            var text = MenuStyle.Text(label, (int)(size * 0.22f), MenuStyle.TextMain, MenuStyle.Bold,
                HorizontalAlignment.Center);
            text.VerticalAlignment = VerticalAlignment.Center;
            button.AddChild(text);
            MenuStyle.Fill(text);
        }

        _root.AddChild(button);
        return button;
    }

    /// <summary>Anchors a control to one screen corner and sizes it, in one call. `right`/`bottom`
    /// pick the corner; margins are measured inward from that corner's two edges.</summary>
    private static void PlaceCorner(Control c, bool right, bool bottom, float w, float h,
        float marginX, float marginY)
    {
        float anchorX = right ? 1f : 0f;
        float anchorY = bottom ? 1f : 0f;
        float left = right ? -(marginX + w) : marginX;
        float top = bottom ? -(marginY + h) : marginY;
        MenuStyle.Place(c, anchorX, anchorY, anchorX, anchorY, left, top, left + w, top + h);
    }
}
