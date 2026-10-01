using Godot;

/// <summary>
/// Input actions the training center adds at runtime.
///
/// The project has no "interact" (or ability/callout/map) action yet - the touch-controls notes say so
/// outright. Registering them here, only when missing, means project.godot is untouched; the day you add
/// the same action names in Project Settings > Input Map, yours win and nothing here needs to change.
/// </summary>
public static class TrainingInput
{
    public const string Interact = "interact";               // E   - enter/exit car, seat swap
    public const string AbilityPrimary = "ability_primary";  // Q   - HOLD: heal / repair / plant / deploy
    public const string AbilitySecondary = "ability_secondary"; // F - smoke / detonate / plating
    public const string CalloutQuick = "callout_quick";      // G   - the context callout for this drill
    public const string CalloutMenu = "callout_menu";        // V   - full callout list
    public const string MapToggle = "map_toggle";            // M   - map overlay
    public const string TrainingMenu = "training_menu";      // Tab - pause / drill menu
    public const string RestartDrill = "training_restart";   // Backspace

    public static readonly string[] AllActions =
    {
        Interact, AbilityPrimary, AbilitySecondary, CalloutQuick, CalloutMenu, MapToggle, TrainingMenu, RestartDrill,
    };

    public static void EnsureRegistered()
    {
        Register(Interact, Key.E);
        Register(AbilityPrimary, Key.Q);
        Register(AbilitySecondary, Key.F);
        Register(CalloutQuick, Key.G);
        Register(CalloutMenu, Key.V);
        Register(MapToggle, Key.M);
        Register(TrainingMenu, Key.Tab);
        Register(RestartDrill, Key.Backspace);
    }

    private static void Register(string action, Key key)
    {
        if (InputMap.HasAction(action)) return;
        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = key });
    }

    /// <summary>Human-readable key for prompts ("E", "Q", ...).</summary>
    public static string KeyText(string action)
    {
        if (!InputMap.HasAction(action)) return "?";
        foreach (InputEvent evt in InputMap.ActionGetEvents(action))
            if (evt is InputEventKey keyEvent)
                return new InputEventKey { PhysicalKeycode = keyEvent.PhysicalKeycode }.AsTextPhysicalKeycode();
        return "?";
    }
}
