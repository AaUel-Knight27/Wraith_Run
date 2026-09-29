using Godot;

/// <summary>
/// The two ways out of a running match, kept in one place so the pause menu's "Quit Game" screen
/// and its "host closed the match" modal cannot drift apart.
///
/// Before this existed there was no way to leave a match at all (Esc only released the mouse), so
/// nothing ever had to tear a session down. Order matters here:
///   1. give the mouse back (the main menu is a normal cursor UI),
///   2. close the network peer so the other players see us leave right away,
///   3. clear per-match autoload state (clock/teams/scores) so the next match starts clean,
///   4. change scene. SceneTree.ChangeSceneToFile is deferred to the end of the frame by the
///      engine, so it is safe to call from inside a button handler on a node that is about to be freed.
/// </summary>
public static class GameSession
{
    public const string MainMenuScene = "res://scenes/MainMenu.tscn";

    public static void LeaveToMainMenu(SceneTree tree)
    {
        TearDownSession(tree);
        Error error = tree.ChangeSceneToFile(MainMenuScene);
        if (error != Error.Ok) GD.PushError($"Could not return to the main menu ({MainMenuScene}): {error}");
    }

    public static void QuitToDesktop(SceneTree tree)
    {
        TearDownSession(tree);
        tree.Quit();
    }

    private static void TearDownSession(SceneTree tree)
    {
        GameUiState.Reset();
        if (!OS.HasFeature("mobile")) Input.MouseMode = Input.MouseModeEnum.Visible;
        tree.Root.GetNodeOrNull<LanSession>("LanSession")?.Stop();
        MatchManager.Instance?.EndSession();
        MatchStats.Instance?.Reset();
    }
}
