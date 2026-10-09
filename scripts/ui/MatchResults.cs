using System;
using Godot;

/// <summary>
/// The end-of-match screen. PlayerHud adds it the moment MatchManager reports the match over (score
/// limit or time limit): a full-screen results board with every player and bot ranked by score -
/// rank, name, team colour, score, kills, deaths, assists and K/D - your own row picked out with a
/// thick border. It is a snapshot taken once when the match ends, so kills the bots keep scoring
/// afterwards cannot change the final standings while you read them.
///
/// Nobody has to quit: it counts down and returns everyone to the main menu by itself, and MAIN
/// MENU skips the wait. While it is up gameplay input is blocked and the pause menu stays shut.
/// </summary>
public partial class MatchResults : CanvasLayer
{
    /// <summary>Set by PlayerHud before the node enters the tree.</summary>
    public CharacterBody3D Player { get; set; } = null!;
    public int WinningTeam { get; set; } = -1;
    public string Reason { get; set; } = "score";

    private const float AutoLeaveSeconds = 30.0f;

    private float _secondsLeft = AutoLeaveSeconds;
    private Label _countdown = null!;
    private bool _leaving;

    public override void _Ready()
    {
        Layer = 95;
        GameUiState.EnsureInputActions();

        // Whatever was open must close first: the pause menu's Close() recaptures the mouse and
        // clears the "menu open" flag, so this has to come before both are set for the results.
        PauseMenu? pause = Player.GetNodeOrNull<PauseMenu>("Hud/PauseMenu");
        if (pause != null && pause.IsOpen) pause.Toggle();
        Player.GetNodeOrNull<Scoreboard>("Hud/Scoreboard")?.HideNow();
        Player.GetNodeOrNull<TouchControls>("TouchControls")?.ReleaseAllHeld();

        GameUiState.SetMenuOpen(true);
        GameUiState.ResultsOpen = true;
        if (!OS.HasFeature("mobile")) Input.MouseMode = Input.MouseModeEnum.Visible;

        Build();
    }

    public override void _ExitTree()
    {
        GameUiState.ResultsOpen = false;
    }

    public override void _Process(double delta)
    {
        if (_leaving) return;
        _secondsLeft -= (float)delta;
        if (_secondsLeft <= 0.0f) { Leave(); return; }
        _countdown.Text = $"Returning to the main menu in {Mathf.CeilToInt(_secondsLeft)}s";
    }

    private void Leave()
    {
        if (_leaving) return;
        _leaving = true;
        GameSession.ReturnToHub(GetTree());
    }

    private void Build()
    {
        MatchManager match = MatchManager.Instance;
        bool teamMode = match != null && match.IsTeamMode;
        long me = PlayerIdentity.IdOf(Player);
        var rows = ScoreboardData.Collect(GetTree());
        int myTeam = teamMode ? match!.GetTeam(me) : -1;

        var root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        MenuStyle.Fill(root);
        AddChild(root);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.82f), MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(dim);
        root.AddChild(dim);

        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 10);
        MenuStyle.Place(column, 0.5f, 0, 0.5f, 1, -410, 22, 410, -22);
        root.AddChild(column);

        // ---- headline ---------------------------------------------------------------------
        string headline;
        Color headlineColor;
        bool won = teamMode ? myTeam == WinningTeam : PlayerIdentity.IdOf(Player) == WinningTeam;
        if (WinningTeam < 0) { headline = "DRAW"; headlineColor = MenuStyle.TextMain; }
        else if (won) { headline = "VICTORY"; headlineColor = teamMode ? MatchManager.TeamColor(WinningTeam) : MenuStyle.Accent; }
        else { headline = "DEFEAT"; headlineColor = MenuStyle.Accent; }

        column.AddChild(MenuStyle.Text(headline, 60, headlineColor, MenuStyle.Spaced(MenuStyle.Bold, 3), HorizontalAlignment.Center));

        string reasonText = Reason == "time" ? "Time limit reached" : "Score limit reached";
        string winnerName = teamMode
            ? $"TEAM {MatchManager.TeamName(WinningTeam)} WINS"
            : $"{BotRoster.DisplayName(WinningTeam)} WINS";
        string sub = $"{(WinningTeam < 0 ? "NO WINNER" : winnerName)}   -   {reasonText}";
        column.AddChild(MenuStyle.Text(sub, 20, MenuStyle.TextDim, MenuStyle.SemiBold, HorizontalAlignment.Center));
        if (teamMode && match != null)
        {
            var teams = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            teams.AddThemeConstantOverride("separation", 24);
            teams.AddChild(MenuStyle.Text($"ALPHA  {match.GetTeamScore(0)}", 26, MatchManager.TeamColor(0), MenuStyle.Bold));
            teams.AddChild(MenuStyle.Text("-", 26, MenuStyle.TextMuted, MenuStyle.Bold));
            teams.AddChild(MenuStyle.Text($"{match.GetTeamScore(1)}  BRAVO", 26, MatchManager.TeamColor(1), MenuStyle.Bold));
            column.AddChild(teams);
        }

        int myPlace = rows.FindIndex(r => r.PeerId == me) + 1;
        if (myPlace > 0)
            column.AddChild(MenuStyle.Text($"You finished #{myPlace} of {rows.Count}", 22, MenuStyle.TextMain,
                MenuStyle.SemiBold, HorizontalAlignment.Center));

        // ---- ranked table: every player and bot, best score first ---------------------------
        var panel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        var style = MenuStyle.Box(MenuStyle.PanelFill, MenuStyle.Hairline);
        style.SetContentMarginAll(12);
        panel.AddThemeStyleboxOverride("panel", style);
        column.AddChild(panel);

        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 4);
        panel.AddChild(box);
        box.AddChild(MenuStyle.Text("FINAL STANDINGS", 22, MenuStyle.Accent, MenuStyle.Bold));
        box.AddChild(Scoreboard.ColumnsRow("#", "PLAYER", "SCORE", "K", "D", "A", "K/D",
            MenuStyle.TextMuted, 14, null, false, teamMode ? new Color(0, 0, 0, 0) : null));

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        box.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(list);

        for (int i = 0; i < rows.Count; i++)
        {
            ScoreboardData.Row r = rows[i];
            bool isMe = r.PeerId == me;
            list.AddChild(Scoreboard.ColumnsRow((i + 1).ToString(), ScoreboardData.DisplayName(r, me),
                r.Points.ToString(), r.Kills.ToString(), r.Deaths.ToString(), r.Assists.ToString(),
                GameUiState.FormatKd(r.Kills, r.Deaths),
                isMe ? MenuStyle.TextMain : MenuStyle.TextDim, 19,
                isMe ? MenuStyle.SelectedFill : null, true,
                teamMode ? MatchManager.TeamColor(r.Team) : null));
        }

        // ---- footer ---------------------------------------------------------------------------
        var footer = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        footer.AddThemeConstantOverride("separation", 16);
        _countdown = MenuStyle.Text("", 18, MenuStyle.TextMuted, MenuStyle.Medium);
        _countdown.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _countdown.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        footer.AddChild(_countdown);
        Button menu = MenuStyle.Card("arrow-left", "MAIN MENU", null, new Vector2(260, 60), Leave,
            titleSize: 24, iconSize: 26);
        footer.AddChild(menu);
        column.AddChild(footer);
        menu.GrabFocus();
    }
}
