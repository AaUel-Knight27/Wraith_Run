using Godot;
using System;

/// <summary>
/// The settings screen (Look &amp; Aim, Controls, Gameplay, Video, Audio, Controller, Mobile), as one
/// self-contained control. It used to live inside LanMenu; it was moved here, unchanged, so the
/// SAME screen can be shown from the main menu AND from the in-game pause menu instead of keeping
/// two copies that drift apart. Add a new setting here once and both places get it.
///
/// It owns its tab list, the content of the selected tab, and the "press a key to rebind" capture.
/// Every change is written straight into SettingsManager and saved, exactly as before. Whoever hosts
/// the panel only positions it (give it a rect - it fills whatever it is given) and provides its own
/// header/back button.
///
/// Rebinding note: while a keybind row is waiting for a key, Escape cancels the capture. The panel
/// handles that in _UnhandledInput and marks the event handled, and Godot delivers _UnhandledInput to
/// deeper nodes first, so the host's own "Escape = go back" handler never sees that Escape.
/// </summary>
public partial class SettingsPanel : Control
{
    private enum SettingsCategory { Look, Controls, Gameplay, Video, Audio, Controller, Mobile }

    private SettingsCategory _settingsCategory = SettingsCategory.Look;
    /// <summary>Non-null while the CONTROLS screen is waiting for the player to press the key they
    /// want to rebind an action to - see _UnhandledInput.</summary>
    private string? _rebindingAction;
    private ScrollContainer? _scroll;

    /// <summary>True while a keybind row is waiting for a key press.</summary>
    public bool IsCapturingKey => _rebindingAction != null;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Build();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_rebindingAction == null) return;
        // Swallow everything while listening, not just keys - a stray click elsewhere on the
        // screen should not also trigger navigation underneath the capture.
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            GetViewport().SetInputAsHandled();
            // Escape cancels the rebind rather than becoming the new binding - the usual
            // convention, and the only way to back out once listening has started.
            if (keyEvent.PhysicalKeycode != Key.Escape)
                SettingsManager.Instance.RebindAction(_rebindingAction, keyEvent.PhysicalKeycode);
            _rebindingAction = null;
            Refresh();
        }
    }

    /// <summary>Rebuilds the current tab in place after a change, keeping the scroll position so
    /// toggling something far down a long list (Video) does not jump back to the top.</summary>
    private void Refresh()
    {
        int scrollValue = _scroll != null && IsInstanceValid(_scroll) ? _scroll.ScrollVertical : 0;
        Build();
        if (scrollValue > 0 && _scroll != null) RestoreScroll(_scroll, scrollValue);
    }

    /// <summary>A freshly built ScrollContainer has no content size until the next layout pass, so a
    /// scroll position set right away would be clamped to zero. Wait one frame, then set it.</summary>
    private async void RestoreScroll(ScrollContainer scroll, int value)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (IsInstanceValid(this) && IsInstanceValid(scroll)) scroll.ScrollVertical = value;
    }

    private void Build()
    {
        foreach (Node child in GetChildren())
            if (IsInstanceValid(child) && !child.IsQueuedForDeletion()) child.QueueFree();

        SettingsManager settings = SettingsManager.Instance;
        void Rebuild() { settings.Save(); Refresh(); }
        if (!IsCategoryAvailable(_settingsCategory, settings)) _settingsCategory = SettingsCategory.Look;

        var layout = new HBoxContainer();
        layout.AddThemeConstantOverride("separation", 20);
        MenuStyle.Fill(layout);
        AddChild(layout);

        var tabs = new VBoxContainer { CustomMinimumSize = new Vector2(230, 0) };
        tabs.AddThemeConstantOverride("separation", 8);
        layout.AddChild(tabs);

        void Tab(string icon, string label, SettingsCategory category) =>
            tabs.AddChild(MenuStyle.SelectRow(icon, label, _settingsCategory == category,
                () => { _settingsCategory = category; _rebindingAction = null; Build(); }));
        // Only the tabs that mean something on this device: a phone has no keyboard to rebind and a
        // PC has no touch layout or gyroscope to tune. (A PC with a touchscreen, or anyone who forced
        // touch controls on, still gets the TOUCH tab; a phone shows CONTROLLER once one is plugged in.)
        Tab("crosshair", "LOOK & AIM", SettingsCategory.Look);
        if (IsCategoryAvailable(SettingsCategory.Controls, settings)) Tab("settings", "KEYBOARD", SettingsCategory.Controls);
        Tab("bot", "GAMEPLAY", SettingsCategory.Gameplay);
        Tab("map", "VIDEO", SettingsCategory.Video);
        Tab("circle", "AUDIO", SettingsCategory.Audio);
        if (IsCategoryAvailable(SettingsCategory.Controller, settings)) Tab("wifi", "CONTROLLER", SettingsCategory.Controller);
        if (IsCategoryAvailable(SettingsCategory.Mobile, settings)) Tab("helmet-gear", "TOUCH", SettingsCategory.Mobile);

        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        layout.AddChild(_scroll);
        var content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        content.AddThemeConstantOverride("separation", 14);
        _scroll.AddChild(content);

        switch (_settingsCategory)
        {
            case SettingsCategory.Look: BuildLookSettings(content, settings, Rebuild); break;
            case SettingsCategory.Controls: BuildControlsSettings(content, settings, Refresh); break;
            case SettingsCategory.Gameplay: BuildGameplaySettings(content, settings, Rebuild); break;
            case SettingsCategory.Video: BuildVideoSettings(content, settings, Rebuild); break;
            case SettingsCategory.Audio: BuildAudioSettings(content, settings, Rebuild); break;
            case SettingsCategory.Controller: BuildControllerSettings(content, settings); break;
            case SettingsCategory.Mobile: BuildMobileSettings(content, settings, Rebuild); break;
        }
    }

    /// <summary>Is this settings category meaningful on the device the game is running on?</summary>
    private static bool IsCategoryAvailable(SettingsCategory category, SettingsManager settings)
    {
        bool phone = OS.HasFeature("mobile");
        switch (category)
        {
            case SettingsCategory.Controls: // key bindings
                return !phone;
            case SettingsCategory.Controller: // always on PC; on a phone only with a pad connected
                return !phone || Input.GetConnectedJoypads().Count > 0;
            case SettingsCategory.Mobile: // touch layout + gyroscope
                return phone || DisplayServer.IsTouchscreenAvailable()
                    || settings.TouchControlsMode != SettingsManager.TouchMode.Auto;
            default:
                return true;
        }
    }

    private static void BuildLookSettings(Container content, SettingsManager settings, Action rebuild)
    {
        var look = MenuStyle.Panel("LOOK & AIM", "crosshair", out var body);
        body.AddChild(MenuStyle.ToggleRow(null, "Invert Look Y", settings.InvertLookY,
            v => { settings.InvertLookY = v; rebuild(); }));
        if (!OS.HasFeature("mobile"))
            body.AddChild(MenuStyle.SliderRow("Mouse Sensitivity", settings.MouseSensitivity,
                0.0008f, 0.006f, 0.0001f, "{0:0.0000}",
                v => settings.MouseSensitivity = v, () => settings.Save()));
        body.AddChild(MenuStyle.SliderRow("Field of View", settings.FieldOfView,
            60f, 110f, 1f, "{0:0}\u00B0",
            v => settings.FieldOfView = v, () => settings.Save()));
        content.AddChild(look);
    }

    private void BuildControlsSettings(Container content, SettingsManager settings, Action refresh)
    {
        var panel = MenuStyle.Panel("KEY BINDINGS", "settings", out var body);
        foreach (string action in SettingsManager.RebindableActions)
        {
            string capturedAction = action;
            bool listening = _rebindingAction == capturedAction;
            string keyText = SettingsManager.KeyDisplayName(settings.GetKeybind(capturedAction));
            body.AddChild(MenuStyle.KeybindRow(SettingsManager.DisplayName(capturedAction), keyText, listening,
                () => { _rebindingAction = listening ? null : capturedAction; refresh(); }));
        }
        var note = MenuStyle.Text(
            "Fire and Aim stay on the mouse and are not rebindable here.",
            16, MenuStyle.TextMuted, MenuStyle.Medium);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(note);
        body.AddChild(MenuStyle.Row("RESET TO DEFAULTS", "Restore every key above",
            () => { settings.ResetKeybindsToDefault(); refresh(); }));
        content.AddChild(panel);
    }

    private static void BuildGameplaySettings(Container content, SettingsManager settings, Action rebuild)
    {
        var gameplay = MenuStyle.Panel("GAMEPLAY", "bot", out var body);
        body.AddChild(MenuStyle.ToggleRow(null, "Hold to Fire", settings.HoldToFire,
            v => { settings.HoldToFire = v; rebuild(); }));
        body.AddChild(MenuStyle.ToggleRow(null, "Hold to ADS", settings.HoldToAds,
            v => { settings.HoldToAds = v; rebuild(); }));
        body.AddChild(MenuStyle.ToggleRow(null, "Auto Sprint", settings.AutoSprint,
            v => { settings.AutoSprint = v; rebuild(); }));
        content.AddChild(gameplay);
    }

    private static void BuildVideoSettings(Container content, SettingsManager settings, Action rebuild)
    {
        // Window mode/resolution are a desktop concept - see SettingsManager.ApplyWindowMode's own
        // OS.HasFeature("mobile") guard, mirrored here so the rows never show up on a build where
        // clicking them could not do anything.
        if (!OS.HasFeature("mobile"))
        {
            var window = MenuStyle.Panel("WINDOW", "map", out var windowBody);
            void SetWindowMode(SettingsManager.WindowModeOption mode)
            {
                settings.WindowMode = mode;
                settings.ApplyWindowMode();
                rebuild();
            }
            windowBody.AddChild(MenuStyle.SelectRow("map", "Windowed",
                settings.WindowMode == SettingsManager.WindowModeOption.Windowed,
                () => SetWindowMode(SettingsManager.WindowModeOption.Windowed)));
            windowBody.AddChild(MenuStyle.SelectRow("map", "Borderless",
                settings.WindowMode == SettingsManager.WindowModeOption.Borderless,
                () => SetWindowMode(SettingsManager.WindowModeOption.Borderless)));
            windowBody.AddChild(MenuStyle.SelectRow("map", "Fullscreen",
                settings.WindowMode == SettingsManager.WindowModeOption.Fullscreen,
                () => SetWindowMode(SettingsManager.WindowModeOption.Fullscreen)));
            content.AddChild(window);

            var resolution = MenuStyle.Panel("WINDOWED RESOLUTION", "map", out var resBody);
            foreach (Vector2I preset in SettingsManager.ResolutionPresets)
            {
                Vector2I captured = preset;
                resBody.AddChild(MenuStyle.SelectRow("map", $"{captured.X} x {captured.Y}",
                    settings.WindowedResolution == captured,
                    () => { settings.WindowedResolution = captured; settings.ApplyResolution(); rebuild(); }));
            }
            content.AddChild(resolution);
        }

        var display = MenuStyle.Panel("DISPLAY", "circle", out var displayBody);
        void SetVSync(SettingsManager.VSyncOption mode)
        {
            settings.VSync = mode;
            settings.ApplyVSync();
            rebuild();
        }
        displayBody.AddChild(MenuStyle.SelectRow("circle", "VSync Off",
            settings.VSync == SettingsManager.VSyncOption.Off, () => SetVSync(SettingsManager.VSyncOption.Off)));
        displayBody.AddChild(MenuStyle.SelectRow("circle", "VSync On",
            settings.VSync == SettingsManager.VSyncOption.On, () => SetVSync(SettingsManager.VSyncOption.On)));
        displayBody.AddChild(MenuStyle.SelectRow("circle", "VSync Adaptive",
            settings.VSync == SettingsManager.VSyncOption.Adaptive,
            () => SetVSync(SettingsManager.VSyncOption.Adaptive)));
        content.AddChild(display);

        var perf = MenuStyle.Panel("PERFORMANCE", "circle", out var perfBody);
        foreach (int fps in SettingsManager.MaxFpsPresets)
        {
            int captured = fps;
            perfBody.AddChild(MenuStyle.SelectRow("circle", fps == 0 ? "Unlimited" : $"{fps} FPS",
                settings.MaxFps == captured,
                () => { settings.MaxFps = captured; settings.ApplyMaxFps(); rebuild(); }));
        }
        perfBody.AddChild(MenuStyle.SliderRow("Render Scale", settings.RenderScale,
            0.5f, 1.0f, 0.05f, "{0:0%}",
            v => { settings.RenderScale = v; settings.ApplyRenderScale(); },
            () => settings.Save()));
        content.AddChild(perf);
    }

    private static void BuildAudioSettings(Container content, SettingsManager settings, Action rebuild)
    {
        var audio = MenuStyle.Panel("AUDIO", "circle", out var body);
        body.AddChild(MenuStyle.ToggleRow(null, "Mute All", settings.MasterMuted,
            v => { settings.MasterMuted = v; settings.ApplyAudioSettings(); rebuild(); }));
        body.AddChild(MenuStyle.SliderRow("Master Volume", settings.MasterVolume,
            0f, 1f, 0.05f, "{0:0%}",
            v => { settings.MasterVolume = v; settings.ApplyAudioSettings(); }, () => settings.Save()));
        body.AddChild(MenuStyle.SliderRow("Weapons Volume", settings.WeaponsVolume,
            0f, 1f, 0.05f, "{0:0%}",
            v => { settings.WeaponsVolume = v; settings.ApplyAudioSettings(); }, () => settings.Save()));
        body.AddChild(MenuStyle.SliderRow("Movement Volume", settings.MovementVolume,
            0f, 1f, 0.05f, "{0:0%}",
            v => { settings.MovementVolume = v; settings.ApplyAudioSettings(); }, () => settings.Save()));
        body.AddChild(MenuStyle.SliderRow("Voice Volume", settings.VoiceVolume,
            0f, 1f, 0.05f, "{0:0%}",
            v => { settings.VoiceVolume = v; settings.ApplyAudioSettings(); }, () => settings.Save()));
        body.AddChild(MenuStyle.SliderRow("Ambience Volume", settings.AmbienceVolume,
            0f, 1f, 0.05f, "{0:0%}",
            v => { settings.AmbienceVolume = v; settings.ApplyAudioSettings(); }, () => settings.Save()));
        body.AddChild(MenuStyle.SliderRow("UI Volume", settings.UiVolume,
            0f, 1f, 0.05f, "{0:0%}",
            v => { settings.UiVolume = v; settings.ApplyAudioSettings(); }, () => settings.Save()));
        body.AddChild(MenuStyle.SliderRow("Music Volume", settings.MusicVolume,
            0f, 1f, 0.05f, "{0:0%}",
            v => { settings.MusicVolume = v; settings.ApplyAudioSettings(); }, () => settings.Save()));
        content.AddChild(audio);
    }

    private static void BuildControllerSettings(Container content, SettingsManager settings)
    {
        var controller = MenuStyle.Panel("CONTROLLER", "wifi", out var body);
        body.AddChild(MenuStyle.SliderRow("Look Sensitivity", settings.ControllerLookSensitivity,
            1.0f, 8.0f, 0.5f, "{0:0.0}",
            v => settings.ControllerLookSensitivity = v, () => settings.Save()));
        var note = MenuStyle.Text(
            "Move with the left stick, look with the right. Jump = A, Crouch = B, Reload = X, " +
            "Fire = Right Trigger, Aim = Left Trigger, Sprint = click Left Stick, " +
            "Swap Weapon = Left/Right Bumper. These are fixed for now - no remapping screen yet.",
            16, MenuStyle.TextMuted, MenuStyle.Medium);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(note);
        content.AddChild(controller);
    }

    private static void BuildMobileSettings(Container content, SettingsManager settings, Action rebuild)
    {
        var mode = MenuStyle.Panel("TOUCH CONTROLS", "helmet-gear", out var modeBody);
        void SetTouchMode(SettingsManager.TouchMode value)
        {
            settings.TouchControlsMode = value;
            rebuild();
        }
        modeBody.AddChild(MenuStyle.SelectRow("helmet-gear", "Auto (device decides)",
            settings.TouchControlsMode == SettingsManager.TouchMode.Auto,
            () => SetTouchMode(SettingsManager.TouchMode.Auto)));
        modeBody.AddChild(MenuStyle.SelectRow("helmet-gear", "Force On",
            settings.TouchControlsMode == SettingsManager.TouchMode.ForceOn,
            () => SetTouchMode(SettingsManager.TouchMode.ForceOn)));
        modeBody.AddChild(MenuStyle.SelectRow("helmet-gear", "Force Off",
            settings.TouchControlsMode == SettingsManager.TouchMode.ForceOff,
            () => SetTouchMode(SettingsManager.TouchMode.ForceOff)));
        content.AddChild(mode);

        if (!settings.ShouldShowTouchControls())
        {
            var off = MenuStyle.Panel("LAYOUT", "lock", out var offBody);
            var offText = MenuStyle.Text(
                "Touch controls are off on this device. Set the mode above to Force On to preview " +
                "and tune the on-screen layout here.", 18, MenuStyle.TextDim, MenuStyle.Medium);
            offText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            offBody.AddChild(offText);
            content.AddChild(off);
            return;
        }

        var touch = MenuStyle.Panel("LAYOUT", "settings", out var touchBody);
        touchBody.AddChild(MenuStyle.ToggleRow(null, "Left-Handed Layout", settings.LeftHandedLayout,
            v => { settings.LeftHandedLayout = v; rebuild(); }));
        touchBody.AddChild(MenuStyle.ToggleRow(null, "Floating Joystick", settings.JoystickFloating,
            v => { settings.JoystickFloating = v; rebuild(); }));
        touchBody.AddChild(MenuStyle.SliderRow("Touch Look Sensitivity", settings.TouchLookSensitivity,
            0.0012f, 0.008f, 0.0001f, "{0:0.0000}",
            v => settings.TouchLookSensitivity = v, () => settings.Save()));
        touchBody.AddChild(MenuStyle.SliderRow("Joystick Deadzone", settings.JoystickDeadzone,
            0.0f, 0.6f, 0.01f, "{0:0%}", v => settings.JoystickDeadzone = v, () => settings.Save()));
        touchBody.AddChild(MenuStyle.SliderRow("Button Scale", settings.ButtonScale,
            0.7f, 1.5f, 0.05f, "{0:0.00}x", v => settings.ButtonScale = v, () => settings.Save()));
        touchBody.AddChild(MenuStyle.SliderRow("Button Opacity", settings.ButtonOpacity,
            0.3f, 1.0f, 0.05f, "{0:0%}", v => settings.ButtonOpacity = v, () => settings.Save()));
        content.AddChild(touch);

        var gyro = MenuStyle.Panel("GYROSCOPE LOOK", "circle", out var gyroBody);
        void SetGyro(SettingsManager.GyroMode value)
        {
            settings.Gyroscope = value;
            rebuild();
        }
        gyroBody.AddChild(MenuStyle.SelectRow("circle", "Off",
            settings.Gyroscope == SettingsManager.GyroMode.Off, () => SetGyro(SettingsManager.GyroMode.Off)));
        gyroBody.AddChild(MenuStyle.SelectRow("circle", "On",
            settings.Gyroscope == SettingsManager.GyroMode.On, () => SetGyro(SettingsManager.GyroMode.On)));
        gyroBody.AddChild(MenuStyle.SelectRow("circle", "ADS Only",
            settings.Gyroscope == SettingsManager.GyroMode.AdsOnly, () => SetGyro(SettingsManager.GyroMode.AdsOnly)));
        if (settings.Gyroscope != SettingsManager.GyroMode.Off)
            gyroBody.AddChild(MenuStyle.SliderRow("Gyro Sensitivity", settings.GyroSensitivity,
                0.2f, 3.0f, 0.1f, "{0:0.0}x", v => settings.GyroSensitivity = v, () => settings.Save()));
        content.AddChild(gyro);
    }
}
