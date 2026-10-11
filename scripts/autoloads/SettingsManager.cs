using System.Collections.Generic;
using Godot;

/// <summary>
/// Persists player-facing settings across sessions (user://settings.cfg) and is the single
/// source of truth every gameplay/UI script reads from at runtime, rather than each script
/// keeping its own copy that can drift out of sync with what was last saved.
///
/// Implemented categories: Look &amp; Aim (mouse sensitivity, invert Y, field of view), Touch/
/// Android (see TouchControls.cs), Gameplay, Video (window mode, windowed resolution, VSync, FPS
/// cap, render scale), Audio (per-bus volume - see default_bus_layout.tres for the Master/
/// Weapons/WeaponsQuiet/Movement/Voice/Ambience/UI buses the sound system built, plus a Music bus
/// added here for whenever a music system exists to use it), Controls (keyboard rebinding for
/// movement/jump/crouch/sprint/reload/weapon slots - fire and aim stay mouse-only), and Controller
/// (right-stick look sensitivity; the stick/button/trigger bindings themselves are fixed, set up
/// once by EnsureControllerBindings so a controller works out of the box on both PC and mobile per
/// the design docs).
///
/// Deliberately still out of scope, the same "don't store a dead field nothing reads" way the
/// Touch/Android section always has been: graphics quality beyond render scale (MSAA, shadow
/// quality), keybind conflict detection, mouse-button rebinding, controller vibration, HUD/
/// crosshair customization, colorblind modes. Add a field here alongside whatever system starts
/// consuming it, same as everything above did.
///
/// Reachable in-game via the gear icon on any main-menu screen -> LanMenu.ShowSettings, which
/// reads/writes these fields directly and calls Save() on every change.
/// </summary>
public partial class SettingsManager : Node
{
    public enum TouchMode { Auto, ForceOn, ForceOff }
    public enum GyroMode { Off, On, AdsOnly }
    public enum WindowModeOption { Windowed, Borderless, Fullscreen }
    public enum VSyncOption { Off, On, Adaptive }

    private const string SettingsPath = "user://settings.cfg";
    private const string Section = "settings";

    /// <summary>Audio buses this settings screen drives. Master (index 0) always exists. The rest
    /// - Weapons/WeaponsQuiet/Movement/Voice/Ambience/UI - come from the project's own
    /// default_bus_layout.tres (Weapons fire, WeaponsQuiet is the Karambit's non-ducking parallel
    /// bus, Movement is footsteps, UI is interface/feedback cues); Music is new here, added for
    /// whenever a music system exists to route into it. ApplyBusVolume skips a bus that is
    /// missing rather than erroring, so an out-of-date bus layout degrades gracefully.</summary>
    private static readonly string[] MixedBuses =
        { "Master", "Weapons", "WeaponsQuiet", "Movement", "Voice", "Ambience", "UI", "Music" };

    /// <summary>Actions the CONTROLS screen can rebind. Fire/aim stay mouse-only by design - see
    /// the class doc. Order here is display order in the settings list.</summary>
    public static readonly string[] RebindableActions =
    {
        "move_forward", "move_back", "move_left", "move_right",
        "jump", "crouch", "sprint", "reload",
        "weapon_1", "weapon_2", "weapon_3", "weapon_4", "weapon_5", "weapon_6",
        "weapon_7", "weapon_8", "weapon_9",
    };

    public static readonly Vector2I[] ResolutionPresets =
    {
        new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440),
    };

    /// <summary>0 means uncapped (Engine.MaxFps's own "unlimited" value).</summary>
    public static readonly int[] MaxFpsPresets = { 30, 60, 120, 144, 0 };

    public static SettingsManager Instance { get; private set; } = null!;

    // ---- Look ---------------------------------------------------------------------------------
    public float MouseSensitivity = 0.0025f;
    public bool InvertLookY;
    public float FieldOfView = 75.0f;

    // ---- Touch / Android ------------------------------------------------------------------------
    /// <summary>Auto shows the overlay only on an actual mobile export (OS.HasFeature("mobile")).
    /// ForceOn/ForceOff are for testing the layout on a desktop build.</summary>
    public TouchMode TouchControlsMode = TouchMode.Auto;
    public float TouchLookSensitivity = 0.0035f;
    /// <summary>Fraction of the joystick's radius that counts as centred (no movement input).</summary>
    public float JoystickDeadzone = 0.2f;
    /// <summary>Floating: the base recentres on wherever the thumb first touches down, like most
    /// mobile shooters. Fixed: the base stays put in its screen corner.</summary>
    public bool JoystickFloating = true;
    /// <summary>Mirrors the whole layout left/right - movement joystick and the fire/ADS cluster
    /// swap sides.</summary>
    public bool LeftHandedLayout;
    public float ButtonScale = 1.0f;
    public float ButtonOpacity = 0.85f;
    /// <summary>True = press-and-hold fires/aims for as long as the button is held (matches a
    /// full-auto trigger). False = each tap fires a single pulse / toggles aim on-off.</summary>
    public bool HoldToFire = true;
    public bool HoldToAds = true;
    public GyroMode Gyroscope = GyroMode.Off;
    public float GyroSensitivity = 1.0f;

    // ---- Gameplay -----------------------------------------------------------------------------
    /// <summary>Skips the Sprint button/key entirely - moving forward sprints automatically.
    /// Mainly useful for touch, where holding a second button just to run is awkward, but nothing
    /// about the flag itself is touch-specific so it is exposed for every input device.</summary>
    public bool AutoSprint;

    // ---- Video --------------------------------------------------------------------------------
    public WindowModeOption WindowMode = WindowModeOption.Windowed;
    /// <summary>Vector2I.Zero means "leave the window exactly as Godot opened it" - the player has
    /// never chosen a size. Set the instant they pick one of ResolutionPresets.</summary>
    public Vector2I WindowedResolution = Vector2I.Zero;
    public VSyncOption VSync = VSyncOption.On;
    public int MaxFps;
    public float RenderScale = 1.0f;

    // ---- Audio --------------------------------------------------------------------------------
    public float MasterVolume = 1.0f;
    public bool MasterMuted;
    /// <summary>Drives both the Weapons and WeaponsQuiet buses together - the split between them
    /// is about whether a shot ducks the Movement bus (see default_bus_layout.tres's sidechain
    /// compressor), not about the player wanting to hear weapons at two different volumes.</summary>
    public float WeaponsVolume = 1.0f;
    public float MovementVolume = 1.0f;
    public float VoiceVolume = 1.0f;
    public float AmbienceVolume = 1.0f;
    public float UiVolume = 1.0f;
    public float MusicVolume = 1.0f;

    // ---- Controller -----------------------------------------------------------------------------
    public float ControllerLookSensitivity = 3.0f;

    /// <summary>Live physical_keycode per rebindable action, seeded from project.godot's own
    /// defaults by SnapshotDefaultKeybinds before Load() ever runs.</summary>
    private readonly Dictionary<string, Key> _keybinds = new();
    /// <summary>project.godot's original bindings, captured once so ResetKeybindsToDefault has
    /// something to go back to even after several rebinds.</summary>
    private readonly Dictionary<string, Key> _defaultKeybinds = new();
    /// <summary>Each mixed bus's authored volume_db at startup - whatever default_bus_layout.tres
    /// (and whatever mixing work has been done on top of it) actually specifies, which is not
    /// necessarily 0 dB for every bus. A slider's 100% means "no change from that baseline", not
    /// "0 dB", so turning settings on for the first time never silently changes existing levels.</summary>
    private readonly Dictionary<string, float> _busBaseDb = new();

    public override void _Ready()
    {
        Instance = this;
        SnapshotDefaultKeybinds();
        EnsureControllerBindings();
        CaptureBusBaselines();
        Load();
        GameCursor.Apply();
    }

    /// <summary>What TouchControls and PlayerHud both check to decide whether the mobile layout
    /// should be showing right now.</summary>
    public bool ShouldShowTouchControls() => TouchControlsMode switch
    {
        TouchMode.ForceOn => true,
        TouchMode.ForceOff => false,
        _ => OS.HasFeature("mobile"),
    };

    /// <summary>Forces the touch overlay on and saves it, for testing the mobile layout on a desktop
    /// build without a touchscreen. Harmless to leave in: it is just TouchControlsMode = ForceOn.</summary>
    public void DebugForceTouchOn()
    {
        TouchControlsMode = TouchMode.ForceOn;
        Save();
    }

    public void Load()
    {
        var file = new ConfigFile();
        if (file.Load(SettingsPath) == Error.Ok)
        {
            MouseSensitivity = (float)file.GetValue(Section, "mouse_sensitivity", MouseSensitivity);
            InvertLookY = (bool)file.GetValue(Section, "invert_look_y", InvertLookY);
            FieldOfView = (float)file.GetValue(Section, "field_of_view", FieldOfView);

            TouchControlsMode = (TouchMode)(int)file.GetValue(Section, "touch_controls_mode", (int)TouchControlsMode);
            TouchLookSensitivity = (float)file.GetValue(Section, "touch_look_sensitivity", TouchLookSensitivity);
            JoystickDeadzone = (float)file.GetValue(Section, "joystick_deadzone", JoystickDeadzone);
            JoystickFloating = (bool)file.GetValue(Section, "joystick_floating", JoystickFloating);
            LeftHandedLayout = (bool)file.GetValue(Section, "left_handed_layout", LeftHandedLayout);
            ButtonScale = (float)file.GetValue(Section, "button_scale", ButtonScale);
            ButtonOpacity = (float)file.GetValue(Section, "button_opacity", ButtonOpacity);
            HoldToFire = (bool)file.GetValue(Section, "hold_to_fire", HoldToFire);
            HoldToAds = (bool)file.GetValue(Section, "hold_to_ads", HoldToAds);
            Gyroscope = (GyroMode)(int)file.GetValue(Section, "gyroscope", (int)Gyroscope);
            GyroSensitivity = (float)file.GetValue(Section, "gyro_sensitivity", GyroSensitivity);

            AutoSprint = (bool)file.GetValue(Section, "auto_sprint", AutoSprint);

            WindowMode = (WindowModeOption)(int)file.GetValue(Section, "window_mode", (int)WindowMode);
            WindowedResolution = (Vector2I)file.GetValue(Section, "windowed_resolution", WindowedResolution);
            VSync = (VSyncOption)(int)file.GetValue(Section, "vsync", (int)VSync);
            MaxFps = (int)file.GetValue(Section, "max_fps", MaxFps);
            RenderScale = (float)file.GetValue(Section, "render_scale", RenderScale);

            MasterVolume = (float)file.GetValue(Section, "master_volume", MasterVolume);
            MasterMuted = (bool)file.GetValue(Section, "master_muted", MasterMuted);
            WeaponsVolume = (float)file.GetValue(Section, "weapons_volume", WeaponsVolume);
            MovementVolume = (float)file.GetValue(Section, "movement_volume", MovementVolume);
            VoiceVolume = (float)file.GetValue(Section, "voice_volume", VoiceVolume);
            AmbienceVolume = (float)file.GetValue(Section, "ambience_volume", AmbienceVolume);
            UiVolume = (float)file.GetValue(Section, "ui_volume", UiVolume);
            MusicVolume = (float)file.GetValue(Section, "music_volume", MusicVolume);

            ControllerLookSensitivity = (float)file.GetValue(
                Section, "controller_look_sensitivity", ControllerLookSensitivity);

            foreach (string action in RebindableActions)
            {
                int saved = (int)file.GetValue(Section, KeybindConfigKey(action), (int)_keybinds[action]);
                _keybinds[action] = (Key)saved;
            }
        }

        // Whether this was a fresh settings.cfg or a loaded one, push every field into the engine
        // at least once - a fresh file still needs InputMap holding the (just-snapshotted)
        // defaults and the audio buses sitting at their authored baseline, not just the in-memory
        // fields being technically correct with nothing applying them.
        ApplyAllKeybinds();
        ApplyWindowMode();
        ApplyVSync();
        ApplyMaxFps();
        ApplyRenderScale();
        ApplyAudioSettings();
    }

    public void Save()
    {
        var file = new ConfigFile();
        file.SetValue(Section, "mouse_sensitivity", MouseSensitivity);
        file.SetValue(Section, "invert_look_y", InvertLookY);
        file.SetValue(Section, "field_of_view", FieldOfView);

        file.SetValue(Section, "touch_controls_mode", (int)TouchControlsMode);
        file.SetValue(Section, "touch_look_sensitivity", TouchLookSensitivity);
        file.SetValue(Section, "joystick_deadzone", JoystickDeadzone);
        file.SetValue(Section, "joystick_floating", JoystickFloating);
        file.SetValue(Section, "left_handed_layout", LeftHandedLayout);
        file.SetValue(Section, "button_scale", ButtonScale);
        file.SetValue(Section, "button_opacity", ButtonOpacity);
        file.SetValue(Section, "hold_to_fire", HoldToFire);
        file.SetValue(Section, "hold_to_ads", HoldToAds);
        file.SetValue(Section, "gyroscope", (int)Gyroscope);
        file.SetValue(Section, "gyro_sensitivity", GyroSensitivity);

        file.SetValue(Section, "auto_sprint", AutoSprint);

        file.SetValue(Section, "window_mode", (int)WindowMode);
        file.SetValue(Section, "windowed_resolution", WindowedResolution);
        file.SetValue(Section, "vsync", (int)VSync);
        file.SetValue(Section, "max_fps", MaxFps);
        file.SetValue(Section, "render_scale", RenderScale);

        file.SetValue(Section, "master_volume", MasterVolume);
        file.SetValue(Section, "master_muted", MasterMuted);
        file.SetValue(Section, "weapons_volume", WeaponsVolume);
        file.SetValue(Section, "movement_volume", MovementVolume);
        file.SetValue(Section, "voice_volume", VoiceVolume);
        file.SetValue(Section, "ambience_volume", AmbienceVolume);
        file.SetValue(Section, "ui_volume", UiVolume);
        file.SetValue(Section, "music_volume", MusicVolume);

        file.SetValue(Section, "controller_look_sensitivity", ControllerLookSensitivity);

        foreach (string action in RebindableActions)
            file.SetValue(Section, KeybindConfigKey(action), (int)_keybinds[action]);

        file.Save(SettingsPath);
    }

    // ---------------------------------------------------------------------------------------
    // Video
    // ---------------------------------------------------------------------------------------

    /// <summary>Window mode is a desktop-only concept - skipped entirely on an actual mobile
    /// export so it never fights the OS's own fullscreen handling there.</summary>
    public void ApplyWindowMode()
    {
        if (OS.HasFeature("mobile")) return;
        switch (WindowMode)
        {
            case WindowModeOption.Windowed:
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);
                ApplyResolution();
                break;
            case WindowModeOption.Borderless:
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true);
                Vector2I screen = DisplayServer.ScreenGetSize();
                DisplayServer.WindowSetSize(screen);
                DisplayServer.WindowSetPosition(Vector2I.Zero);
                break;
            case WindowModeOption.Fullscreen:
                DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, false);
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                break;
        }
    }

    public void ApplyResolution()
    {
        if (OS.HasFeature("mobile")) return;
        if (WindowMode != WindowModeOption.Windowed || WindowedResolution == Vector2I.Zero) return;
        DisplayServer.WindowSetSize(WindowedResolution);
        Vector2I screen = DisplayServer.ScreenGetSize();
        DisplayServer.WindowSetPosition((screen - WindowedResolution) / 2);
    }

    public void ApplyVSync() => DisplayServer.WindowSetVsyncMode(VSync switch
    {
        VSyncOption.Off => DisplayServer.VSyncMode.Disabled,
        VSyncOption.Adaptive => DisplayServer.VSyncMode.Adaptive,
        _ => DisplayServer.VSyncMode.Enabled,
    });

    public void ApplyMaxFps() => Engine.MaxFps = MaxFps;

    public void ApplyRenderScale()
    {
        Viewport viewport = GetViewport();
        if (viewport != null) viewport.Scaling3DScale = Mathf.Clamp(RenderScale, 0.5f, 1.0f);
    }

    // ---------------------------------------------------------------------------------------
    // Audio
    // ---------------------------------------------------------------------------------------

    private void CaptureBusBaselines()
    {
        foreach (string bus in MixedBuses)
        {
            int idx = AudioServer.GetBusIndex(bus);
            if (idx >= 0) _busBaseDb[bus] = AudioServer.GetBusVolumeDb(idx);
        }
    }

    public void ApplyAudioSettings()
    {
        ApplyBusVolume("Master", MasterVolume, MasterMuted);
        ApplyBusVolume("Weapons", WeaponsVolume, false);
        ApplyBusVolume("WeaponsQuiet", WeaponsVolume, false);
        ApplyBusVolume("Movement", MovementVolume, false);
        ApplyBusVolume("Voice", VoiceVolume, false);
        ApplyBusVolume("Ambience", AmbienceVolume, false);
        ApplyBusVolume("UI", UiVolume, false);
        ApplyBusVolume("Music", MusicVolume, false);
    }

    /// <summary>linearVolume is a 0..1 slider fraction of the bus's own authored baseline, not an
    /// absolute dB value - see _busBaseDb's doc comment for why. 0 (or forceMute) mutes the bus
    /// outright rather than asking SetBusVolumeDb for -infinity dB.</summary>
    private void ApplyBusVolume(string busName, float linearVolume, bool forceMute)
    {
        int idx = AudioServer.GetBusIndex(busName);
        if (idx < 0) return;
        bool silent = forceMute || linearVolume <= 0.0001f;
        AudioServer.SetBusMute(idx, silent);
        if (silent) return;
        float baseDb = _busBaseDb.TryGetValue(busName, out float b) ? b : 0.0f;
        AudioServer.SetBusVolumeDb(idx, baseDb + Mathf.LinearToDb(Mathf.Clamp(linearVolume, 0.0f, 1.0f)));
    }

    // ---------------------------------------------------------------------------------------
    // Keyboard rebinding
    // ---------------------------------------------------------------------------------------

    private static string KeybindConfigKey(string action) => "keybind_" + action;

    /// <summary>Human label for a RebindableActions entry, e.g. "weapon_3" -> "Pistol". The weapon keys
    /// follow the carried weapon set: two primaries, a pistol, a knife, and a heavy weapon (Demolisher only).</summary>
    public static string DisplayName(string action) => action switch
    {
        "move_forward" => "Move Forward",
        "move_back" => "Move Back",
        "move_left" => "Move Left",
        "move_right" => "Move Right",
        "jump" => "Jump",
        "crouch" => "Crouch",
        "sprint" => "Sprint",
        "reload" => "Reload",
        "weapon_1" => "Primary Weapon 1",
        "weapon_2" => "Primary Weapon 2",
        "weapon_3" => "Pistol",
        "weapon_4" => "Knife",
        "weapon_5" => "Heavy Weapon",
        _ when action.StartsWith("weapon_") => $"Weapon Slot {action.Substring("weapon_".Length)}",
        _ => action,
    };

    /// <summary>Readable label for a physical_keycode, e.g. Key.Space -> "Space". Key.None (never
    /// bound, or a rebind was somehow cleared) reads as "Unbound" rather than an empty string.</summary>
    public static string KeyDisplayName(Key key) =>
        key == Key.None ? "Unbound" : new InputEventKey { PhysicalKeycode = key }.AsTextPhysicalKeycode();

    public Key GetKeybind(string action) => _keybinds.TryGetValue(action, out Key key) ? key : Key.None;

    /// <summary>Called once, before Load(), so _defaultKeybinds captures project.godot's own
    /// bindings before any saved override is ever applied on top of them.</summary>
    private void SnapshotDefaultKeybinds()
    {
        foreach (string action in RebindableActions)
        {
            Key key = Key.None;
            if (InputMap.HasAction(action))
                foreach (InputEvent evt in InputMap.ActionGetEvents(action))
                    if (evt is InputEventKey keyEvent) { key = keyEvent.PhysicalKeycode; break; }
            _defaultKeybinds[action] = key;
            _keybinds[action] = key;
        }
    }

    /// <summary>Replaces only the keyboard event(s) on an action, leaving any mouse or joypad
    /// event already bound to it (e.g. fire's mouse button, or a controller trigger added by
    /// EnsureControllerBindings) untouched.</summary>
    private static void ApplyKeybind(string action, Key physicalKeycode)
    {
        if (!InputMap.HasAction(action)) return;
        foreach (InputEvent evt in InputMap.ActionGetEvents(action))
            if (evt is InputEventKey) InputMap.ActionEraseEvent(action, evt);
        if (physicalKeycode != Key.None)
            InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = physicalKeycode });
    }

    private void ApplyAllKeybinds()
    {
        foreach (string action in RebindableActions) ApplyKeybind(action, _keybinds[action]);
    }

    /// <summary>Called by the CONTROLS screen once the player has picked a new key for a row.
    /// Ignored for an action this screen never listed (nothing else should be rebinding through
    /// this path).</summary>
    public void RebindAction(string action, Key physicalKeycode)
    {
        if (!_keybinds.ContainsKey(action)) return;
        _keybinds[action] = physicalKeycode;
        ApplyKeybind(action, physicalKeycode);
        Save();
    }

    public void ResetKeybindsToDefault()
    {
        foreach (string action in RebindableActions) _keybinds[action] = _defaultKeybinds[action];
        ApplyAllKeybinds();
        Save();
    }

    // ---------------------------------------------------------------------------------------
    // Controller
    // ---------------------------------------------------------------------------------------

    /// <summary>Fixed default bindings so a controller works immediately on both PC and mobile,
    /// per the design docs - there is no remapping UI for these (see the class doc's scope note).
    /// Left stick moves, right stick looks (read by PlayerMovement.ApplyControllerLook), face
    /// buttons/triggers/bumpers cover jump/crouch/reload/fire/aim/sprint/weapon-cycling. Runs once
    /// at startup, before any saved keyboard rebind is applied, and only adds an event that is not
    /// already there so a second call (should one ever happen) can't double up bindings.</summary>
    private static void EnsureControllerBindings()
    {
        BindAxis("move_left", JoyAxis.LeftX, -1.0f);
        BindAxis("move_right", JoyAxis.LeftX, 1.0f);
        BindAxis("move_forward", JoyAxis.LeftY, -1.0f);
        BindAxis("move_back", JoyAxis.LeftY, 1.0f);
        BindAxis("look_stick_left", JoyAxis.RightX, -1.0f);
        BindAxis("look_stick_right", JoyAxis.RightX, 1.0f);
        BindAxis("look_stick_up", JoyAxis.RightY, -1.0f);
        BindAxis("look_stick_down", JoyAxis.RightY, 1.0f);
        BindAxis("fire", JoyAxis.TriggerRight, 1.0f);
        BindAxis("aim", JoyAxis.TriggerLeft, 1.0f);
        BindButton("jump", JoyButton.A);
        BindButton("crouch", JoyButton.B);
        BindButton("reload", JoyButton.X);
        BindButton("sprint", JoyButton.LeftStick);
        BindButton("weapon_cycle_next", JoyButton.RightShoulder);
        BindButton("weapon_cycle_prev", JoyButton.LeftShoulder);
    }

    private static void BindButton(string action, JoyButton button)
    {
        if (!InputMap.HasAction(action)) return;
        var evt = new InputEventJoypadButton { ButtonIndex = button };
        if (!InputMap.ActionHasEvent(action, evt)) InputMap.ActionAddEvent(action, evt);
    }

    private static void BindAxis(string action, JoyAxis axis, float axisValue)
    {
        if (!InputMap.HasAction(action)) return;
        var evt = new InputEventJoypadMotion { Axis = axis, AxisValue = axisValue };
        if (!InputMap.ActionHasEvent(action, evt)) InputMap.ActionAddEvent(action, evt);
    }
}