using Godot;
using System;
using System.Collections.Generic;
using Row = ScoreboardData.Row;

/// <summary>
/// The scoreboard: hold Tab (or gamepad Back/Select) to show it, release to hide it. On a phone the
/// SCORE button TouchControls draws toggles it instead, since there is nothing to hold.
///
/// Shows, for the current match: the mode, the match clock (time left in Team Deathmatch), your own
/// K/D ratio with kills / deaths / assists / score, and one row per player with kills, deaths,
/// assists, K/D and score. In Team Deathmatch the rows are split into YOUR TEAM (friends) and the
/// ENEMY TEAM; in Deathmatch everyone is one list, ranked. You are highlighted and dead players are
/// dimmed.
///
/// Where the numbers come from: kills / deaths / assists from MatchStats (identical on every peer -
/// see its doc for why PlayerScore.Kills could not be used), score points from each player's
/// replicated PlayerScore.TotalPoints, teams from MatchManager, alive/dead from each player's
/// replicated Health. Players are found through the "players" group PlayerMovement already joins,
/// so a player who leaves disappears from the board on their own.
///
/// It never takes mouse input, so a touch that lands on it still turns the camera. Added by
/// PlayerHud, local player only.
/// </summary>
public partial class Scoreboard : CanvasLayer
{
    /// <summary>Set by PlayerHud before the node enters the tree.</summary>
    public CharacterBody3D Player { get; set; } = null!;

    private const float RefreshSeconds = 0.2f;

    private static readonly Color DeadTint = new(1, 1, 1, 0.45f);

    private Control _root = null!;
    private VBoxContainer _column = null!;
    private bool _touchShown;
    private float _refreshTimer;

    public override void _Ready()
    {
        Layer = 70;
        GameUiState.EnsureInputActions();
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        MenuStyle.Fill(_root);
        AddChild(_root);

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(dim);
        _root.AddChild(dim);

        // Centred, fixed-width column. Top-anchored (not vertically centred) so a board that grows
        // with more players grows downward instead of jumping around.
        _column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _column.AddThemeConstantOverride("separation", 10);
        MenuStyle.Place(_column, 0.5f, 0, 0.5f, 0, -400, 26, 400, 26);
        _root.AddChild(_column);
    }

    /// <summary>The touch SCORE button: show it, tap again to hide it.</summary>
    public void ToggleTouch() => _touchShown = !_touchShown;

    /// <summary>Hides it right now (the pause menu calls this as it opens).</summary>
    public void HideNow()
    {
        _touchShown = false;
        _root.Visible = false;
    }

    public override void _Process(double delta)
    {
        bool want = !GameUiState.MenuOpen && (_touchShown || Input.IsActionPressed(GameUiState.ScoreboardAction));
        if (!want)
        {
            if (_root.Visible) _root.Visible = false;
            _refreshTimer = RefreshSeconds; // so the next show builds immediately
            return;
        }

        _refreshTimer += (float)delta;
        if (_root.Visible && _refreshTimer < RefreshSeconds) return;
        _refreshTimer = 0.0f;
        Rebuild();
        _root.Visible = true;
    }

    // -------------------------------------------------------------------------------------------
    // Layout
    // -------------------------------------------------------------------------------------------

    private void Rebuild()
    {
        // Immediate removal + Free, not QueueFree: this runs several times a second and the old rows
        // must be gone before the new ones are laid out, or the board flickers for a frame.
        foreach (Node child in _column.GetChildren())
        {
            _column.RemoveChild(child);
            child.Free();
        }

        MatchManager match = MatchManager.Instance;
        bool teamMode = match != null && match.IsTeamMode;
        long me = PlayerIdentity.IdOf(Player);
        List<Row> rows = ScoreboardData.Collect(GetTree());
        MatchStats.Stats mine = MatchStats.Instance?.Get(me) ?? new MatchStats.Stats();
        int myPoints = Player.GetNodeOrNull<PlayerScore>("Score")?.TotalPoints ?? 0;

        _column.AddChild(BuildHeader(match, teamMode));
        _column.AddChild(BuildSummary(mine, myPoints));

        if (!teamMode)
        {
            _column.AddChild(BuildTable("PLAYERS", null, MenuStyle.Accent, rows, me, null, showScoreOfTeam: false));
            return;
        }

        int myTeam = match!.GetTeam(me);
        int first = myTeam >= 0 ? myTeam : 0;
        int second = 1 - first;
        AddTeamTable(rows, first, myTeam >= 0 ? "YOUR TEAM" : null, me, match);
        AddTeamTable(rows, second, myTeam >= 0 ? "ENEMY TEAM" : null, me, match);
        var unassigned = rows.FindAll(r => r.Team < 0);
        if (unassigned.Count > 0)
            _column.AddChild(BuildTable("JOINING", null, MenuStyle.TextDim, unassigned, me, null, showScoreOfTeam: false));
    }

    private void AddTeamTable(List<Row> all, int team, string? role, long me, MatchManager match)
    {
        var members = all.FindAll(r => r.Team == team);
        Color color = MatchManager.TeamColor(team);
        string title = role != null ? $"{role}  -  {MatchManager.TeamName(team)}" : $"TEAM {MatchManager.TeamName(team)}";
        _column.AddChild(BuildTable(title, match.GetTeamScore(team).ToString(), color, members, me, team, showScoreOfTeam: true));
    }

    private Control BuildHeader(MatchManager match, bool teamMode)
    {
        var bar = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("separation", 16);

        var left = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        left.AddThemeConstantOverride("separation", -4);
        left.AddChild(MenuStyle.Text("SCOREBOARD", 34, MenuStyle.TextMain, MenuStyle.Spaced(MenuStyle.Bold, 2)));
        string mode = teamMode ? "TEAM DEATHMATCH" : "DEATHMATCH";
        if (teamMode) mode += $"   -   first to {match.ScoreLimit} kills";
        left.AddChild(MenuStyle.Text(mode, 17, MenuStyle.Accent, MenuStyle.SemiBold));
        bar.AddChild(left);

        string timeText;
        string timeCaption;
        if (match == null) { timeText = "--:--"; timeCaption = "TIME"; }
        else if (match.IsMatchEnded)
        {
            timeText = match.WinningTeam < 0 ? "DRAW" : $"TEAM {MatchManager.TeamName(match.WinningTeam)} WINS";
            timeCaption = "MATCH OVER";
        }
        else if (match.HasTimeLimit)
        {
            timeText = GameUiState.FormatClock(match.TimeRemainingSeconds);
            timeCaption = "TIME LEFT";
        }
        else
        {
            timeText = GameUiState.FormatClock(match.ElapsedSeconds);
            timeCaption = "MATCH TIME";
        }
        var right = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        right.AddThemeConstantOverride("separation", -6);
        right.AddChild(MenuStyle.Text(timeCaption, 14, MenuStyle.TextMuted, MenuStyle.SemiBold, HorizontalAlignment.Right));
        right.AddChild(MenuStyle.Text(timeText, 36, MenuStyle.TextMain, MenuStyle.Bold, HorizontalAlignment.Right));
        bar.AddChild(right);
        return bar;
    }

    /// <summary>Your own numbers, big: K/D first because it is what the scoreboard is opened for.</summary>
    private Control BuildSummary(MatchStats.Stats mine, int points)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = MenuStyle.Box(MenuStyle.PanelFill, MenuStyle.SelectedBorder);
        style.SetContentMarginAll(10);
        panel.AddThemeStyleboxOverride("panel", style);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);

        void Tile(string caption, string value, Color? color = null)
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
            box.AddThemeConstantOverride("separation", -4);
            box.AddChild(MenuStyle.Text(caption, 14, MenuStyle.TextMuted, MenuStyle.SemiBold, HorizontalAlignment.Center));
            box.AddChild(MenuStyle.Text(value, 32, color ?? MenuStyle.TextMain, MenuStyle.Bold, HorizontalAlignment.Center));
            row.AddChild(box);
        }
        Tile("YOUR K/D", GameUiState.FormatKd(mine.Kills, mine.Deaths), MenuStyle.Accent);
        Tile("KILLS", mine.Kills.ToString());
        Tile("DEATHS", mine.Deaths.ToString());
        Tile("ASSISTS", mine.Assists.ToString());
        Tile("SCORE", points.ToString());
        return panel;
    }

    private Control BuildTable(string title, string? teamScore, Color accent, List<Row> rows, long me,
        int? team, bool showScoreOfTeam)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = MenuStyle.Box(MenuStyle.PanelFill, MenuStyle.Hairline);
        style.SetContentMarginAll(12);
        panel.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 4);
        panel.AddChild(box);

        var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var titleLabel = MenuStyle.Text(title, 22, accent, MenuStyle.Bold);
        titleLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(titleLabel);
        if (showScoreOfTeam && teamScore != null)
            head.AddChild(MenuStyle.Text($"{teamScore} kills", 22, accent, MenuStyle.Bold, HorizontalAlignment.Right));
        box.AddChild(head);

        box.AddChild(ColumnsRow("#", "PLAYER", "SCORE", "K", "D", "A", "K/D", MenuStyle.TextMuted, 14, null, false));
        if (rows.Count == 0)
        {
            box.AddChild(MenuStyle.Text("No players", 17, MenuStyle.TextMuted, MenuStyle.Medium));
            return panel;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            Row r = rows[i];
            bool isMe = r.PeerId == me;
            string name = ScoreboardData.DisplayName(r, me);
            if (r.Dead) name += "  - dead";
            Color textColor = isMe ? MenuStyle.TextMain : MenuStyle.TextDim;
            if (r.Dead) textColor = new Color(textColor, DeadTint.A);
            box.AddChild(ColumnsRow((i + 1).ToString(), name, r.Points.ToString(), r.Kills.ToString(),
                r.Deaths.ToString(), r.Assists.ToString(), GameUiState.FormatKd(r.Kills, r.Deaths),
                textColor, 19, isMe ? MenuStyle.SelectedFill : null, true));
        }
        return panel;
    }

    internal static Control ColumnsRow(string rank, string name, string score, string kills, string deaths,
        string assists, string kd, Color color, int size, Color? highlight, bool body, Color? teamStripe = null)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 6);
        Font font = body ? MenuStyle.SemiBold : MenuStyle.Bold;

        void Cell(string text, float width, HorizontalAlignment align, bool expand = false)
        {
            Label label = MenuStyle.Text(text, size, color, font, align);
            label.CustomMinimumSize = new Vector2(width, 0);
            label.ClipText = true;
            if (expand) label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(label);
        }
        if (teamStripe != null)
            row.AddChild(new ColorRect { Color = teamStripe.Value, CustomMinimumSize = new Vector2(5, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        Cell(rank, 30, HorizontalAlignment.Center);
        Cell(name, 0, HorizontalAlignment.Left, expand: true);
        Cell(score, 74, HorizontalAlignment.Right);
        Cell(kills, 52, HorizontalAlignment.Right);
        Cell(deaths, 52, HorizontalAlignment.Right);
        Cell(assists, 52, HorizontalAlignment.Right);
        Cell(kd, 68, HorizontalAlignment.Right);

        // Every row gets the same 6 px side padding - filled for the highlighted (your) row, empty
        // for the rest - so the columns line up exactly between rows.
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        StyleBox style;
        if (highlight != null)
        {
            // Thick, bright border so your own row is findable at a glance in a long list.
            var filled = MenuStyle.Box(highlight.Value, MenuStyle.TextMain, 3, 2);
            filled.ContentMarginLeft = 6;
            filled.ContentMarginRight = 6;
            style = filled;
        }
        else
        {
            var empty = new StyleBoxEmpty();
            empty.ContentMarginLeft = 6;
            empty.ContentMarginRight = 6;
            style = empty;
        }
        panel.AddThemeStyleboxOverride("panel", style);
        panel.AddChild(row);
        return panel;
    }
}
