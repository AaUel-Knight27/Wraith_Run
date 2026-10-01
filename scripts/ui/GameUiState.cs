using Godot;

/// <summary>
/// The few things the in-game pause menu, the Tab scoreboard and the gameplay scripts all need to
/// agree on, in one place so none of them has to reach into another one's internals:
///
///  - the two input actions the new menus use (registered in code, see EnsureInputActions, so
///    project.godot's own [input] block does not have to change);
///  - whether a menu is currently open and therefore whether PlayerMovement / WeaponSwitcher should
///    ignore gameplay input (movement, look, fire, reload, weapon keys). The game itself does NOT
///    pause behind the menu - it is a LAN multiplayer game, so everyone else keeps playing, exactly
///    like every other online shooter's pause menu;
///  - small formatting helpers (match clock, player display name) shared by both screens.
///
/// It is static and holds only two plain values, so there is nothing to keep in sync with a scene.
/// PauseMenu.Close()/_ExitTree and GameSession.LeaveToMainMenu both call Reset(), so a stale "menu
/// is open" can never survive a scene change.
/// </summary>
public static class GameUiState
{
    /// <summary>Esc on keyboard, Start on a gamepad. Android's Back button/gesture is handled
    /// separately by PauseMenu (it arrives as a window notification, not an input action).</summary>
    public const string PauseAction = "pause_menu";

    /// <summary>Hold Tab on keyboard, or Back/Select on a gamepad. Touch uses an on-screen button.</summary>
    public const string ScoreboardAction = "scoreboard";

    /// <summary>True while the pause menu (or one of its modals) is on screen.</summary>
    public static bool MenuOpen { get; private set; }

    /// <summary>True while the end-of-match results screen is up (the pause menu stays shut).</summary>
    public static bool ResultsOpen { get; set; }

    /// <summary>What PlayerMovement and WeaponSwitcher check before reading gameplay input.</summary>
    public static bool BlocksGameplayInput => MenuOpen;

    public static void SetMenuOpen(bool open) => MenuOpen = open;

    public static void Reset() { MenuOpen = false; ResultsOpen = false; }

    /// <summary>Idempotent - safe to call from every player's HUD. Adds the two actions and their
    /// default keys if they are not in the InputMap yet, and never touches an existing binding.</summary>
    public static void EnsureInputActions()
    {
        AddAction(PauseAction,
            new InputEventKey { PhysicalKeycode = Key.Escape },
            new InputEventJoypadButton { ButtonIndex = JoyButton.Start });
        AddAction(ScoreboardAction,
            new InputEventKey { PhysicalKeycode = Key.Tab },
            new InputEventJoypadButton { ButtonIndex = JoyButton.Back });
    }

    private static void AddAction(string action, params InputEvent[] events)
    {
        if (!InputMap.HasAction(action)) InputMap.AddAction(action);
        foreach (InputEvent evt in events)
            if (!InputMap.ActionHasEvent(action, evt)) InputMap.ActionAddEvent(action, evt);
    }

    /// <summary>"03:07" - whole minutes and seconds, never negative.</summary>
    public static string FormatClock(float seconds)
    {
        int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
        return $"{total / 60:00}:{total % 60:00}";
    }

    /// <summary>Same wording the kill feed already uses, so a name on the scoreboard is
    /// recognisable in the feed: a bot's name, or a person's account name (see PlayerDirectory).</summary>
    public static string PlayerName(long peerId) => BotRoster.DisplayName(peerId);

    /// <summary>Kills per death. With zero deaths it is simply the kill count (a 5-0 game reads
    /// 5.00, not infinity), the usual scoreboard convention.</summary>
    public static float KdRatio(int kills, int deaths) => deaths <= 0 ? kills : kills / (float)deaths;

    public static string FormatKd(int kills, int deaths) => KdRatio(kills, deaths).ToString("0.00");
}
