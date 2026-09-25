using Godot;

/// <summary>
/// Persists player-facing settings across sessions (user://settings.cfg) and is the single
/// source of truth every gameplay/UI script reads from at runtime, rather than each script
/// keeping its own copy that can drift out of sync with what was last saved.
///
/// Scope, for now: only the settings that actually have something behind them in this build -
/// look sensitivity and the Touch/Android control scheme (see TouchControls.cs). Categories from
/// the settings spec with no implementation yet (Graphics quality, Audio mixing, HUD toggles...)
/// are deliberately left out rather than stored as dead fields nothing reads; add them here
/// alongside whatever system starts consuming them.
///
/// Reachable in-game via the gear icon on any main-menu screen -> LanMenu.ShowSettings, which
/// reads/writes these fields directly and calls Save() on every change.
/// </summary>
public partial class SettingsManager : Node
{
    public enum TouchMode { Auto, ForceOn, ForceOff }
    public enum GyroMode { Off, On, AdsOnly }

    private const string SettingsPath = "user://settings.cfg";
    private const string Section = "settings";

    public static SettingsManager Instance { get; private set; } = null!;

    // ---- Look ----------------------------------------------------------------------------
    public float MouseSensitivity = 0.0025f;
    public bool InvertLookY;

    // ---- Touch / Android -------------------------------------------------------------------
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

    // ---- Gameplay --------------------------------------------------------------------------
    /// <summary>Skips the Sprint button/key entirely - moving forward sprints automatically.
    /// Mainly useful for touch, where holding a second button just to run is awkward, but nothing
    /// about the flag itself is touch-specific so it is exposed for every input device.</summary>
    public bool AutoSprint;

    public override void _Ready()
    {
        Instance = this;
        Load();
    }

    /// <summary>What TouchControls and PlayerHud both check to decide whether the mobile layout
    /// should be showing right now.</summary>
    public bool ShouldShowTouchControls() => TouchControlsMode switch
    {
        TouchMode.ForceOn => true,
        TouchMode.ForceOff => false,
        _ => OS.HasFeature("mobile"),
    };

    public void Load()
    {
        var file = new ConfigFile();
        if (file.Load(SettingsPath) != Error.Ok) return; // First run - the defaults above stand.

        MouseSensitivity = (float)file.GetValue(Section, "mouse_sensitivity", MouseSensitivity);
        InvertLookY = (bool)file.GetValue(Section, "invert_look_y", InvertLookY);
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
    }

    public void Save()
    {
        var file = new ConfigFile();
        file.SetValue(Section, "mouse_sensitivity", MouseSensitivity);
        file.SetValue(Section, "invert_look_y", InvertLookY);
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
        file.Save(SettingsPath);
    }
}
