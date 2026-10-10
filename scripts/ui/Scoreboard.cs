using Godot;
using System;
using System.Collections.Generic;
using Row = ScoreboardData.Row;

/// <summary>
/// The scoreboard: hold Tab (or gamepad Back/Select) to show it, release to hide it. On a phone the
/// SCORE button TouchControls draws toggles it instead, since there is nothing to hold.
///
/// Shows, for the current match, one thin line (mode | map on the left, match time on the right) and
/// then compact tables: one per team (your team first) with the team's kills over the score limit,
/// or a single ranked list in modes without teams. Each row is a name plus Score / Kills / Deaths /
/// K/D. You are the gold row and dead players are dimmed. There is no separate "your stats" box -
/// your own row is already in the table.
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

	private static readonly Color Gold = new(1.0f, 0.82f, 0.32f);
	private const float DeadAlpha = 0.45f;

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

		// One translucent band across the screen with the board centred in it, so the game stays
		// visible above and below. Anchored by its top edge: a board that grows with more players
		// grows downward instead of jumping around.
		var band = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		var bandStyle = new StyleBoxFlat { BgColor = new Color(0.02f, 0.02f, 0.025f, 0.66f) };
		bandStyle.ContentMarginTop = 12;
		bandStyle.ContentMarginBottom = 14;
		band.AddThemeStyleboxOverride("panel", bandStyle);
		MenuStyle.Place(band, 0, 0.2f, 1, 0.2f, 0, 0, 0, 0);
		_root.AddChild(band);

		var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		band.AddChild(center);
		_column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		_column.AddThemeConstantOverride("separation", 8);
		center.AddChild(_column);
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
		// RemoveChild() is what prevents the flicker (it happens synchronously either way) -
		// QueueFree() only defers the memory cleanup, so it's safe here and doesn't crash
		// when a deferred layout/sort callback still has a pending reference to the old row.
		foreach (Node child in _column.GetChildren())
		{
			_column.RemoveChild(child);
			child.QueueFree();
		}

		MatchManager? match = MatchManager.Instance;
		bool teamMode = match != null && match.IsTeamMode;
		long me = PlayerIdentity.IdOf(Player);
		List<Row> rows = ScoreboardData.Collect(GetTree());

		// Three quarters of the screen, like the reference, but never absurdly narrow or wide.
		float width = Mathf.Clamp(GetViewport().GetVisibleRect().Size.X * 0.75f, 480.0f, 1100.0f);
		_column.CustomMinimumSize = new Vector2(width, 0);

		_column.AddChild(BuildInfoLine(match));

		if (!teamMode || match == null)
		{
			_column.AddChild(BuildBoard("PLAYERS", null, null, MenuStyle.TextDim, new Color(0.05f, 0.07f, 0.10f, 0.62f), rows, me));
			return;
		}

		int myTeam = match.GetTeam(me);
		int first = myTeam >= 0 ? myTeam : 0;
		AddTeamBoard(rows, first, me, match);
		AddTeamBoard(rows, 1 - first, me, match);
		var unassigned = rows.FindAll(r => r.Team < 0);
		if (unassigned.Count > 0)
			_column.AddChild(BuildBoard("JOINING", null, null, MenuStyle.TextDim, new Color(0.05f, 0.07f, 0.10f, 0.62f), unassigned, me));
	}

	private void AddTeamBoard(List<Row> all, int team, long me, MatchManager match)
	{
		Color color = MatchManager.TeamColor(team);
		var tint = new Color(color.R * 0.30f, color.G * 0.30f, color.B * 0.30f, 0.62f);
		_column.AddChild(BuildBoard(MatchManager.TeamName(team), match.GetTeamScore(team).ToString(),
			match.ScoreLimit.ToString(), color, tint, all.FindAll(r => r.Team == team), me));
	}

	/// <summary>The one thin line above the tables: mode | map on the left, the match time on the right.</summary>
	private static Control BuildInfoLine(MatchManager? match)
	{
		var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		box.AddThemeConstantOverride("separation", 4);

		var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		string left = match == null ? string.Empty : $"{match.ModeRules.DisplayName} | {match.MapDisplayName}";
		Label mode = MenuStyle.Text(left, 17, MenuStyle.TextDim, MenuStyle.Medium);
		mode.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		line.AddChild(mode);
		line.AddChild(MenuStyle.Text(TimeText(match), 17, MenuStyle.TextDim, MenuStyle.Medium, HorizontalAlignment.Right));
		box.AddChild(line);

		box.AddChild(new ColorRect { Color = new Color(1, 1, 1, 0.55f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = Control.MouseFilterEnum.Ignore });
		return box;
	}

	private static string TimeText(MatchManager? match)
	{
		if (match == null) return string.Empty;
		if (match.IsMatchEnded)
			return match.WinningTeam < 0 ? "Match over - draw" : $"Match over - Team {MatchManager.TeamName(match.WinningTeam)} wins";
		return match.HasTimeLimit
			? $"Time Left - {GameUiState.FormatClock(match.TimeRemainingSeconds)}"
			: $"Match Time - {GameUiState.FormatClock(match.ElapsedSeconds)}";
	}

	/// <summary>One tinted block: a header (team kills / limit and the name, with the column titles on the
	/// right) and one slim row per player.</summary>
	private static Control BuildBoard(string name, string? score, string? limit, Color accent, Color tint,
		List<Row> rows, long me)
	{
		var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		var style = new StyleBoxFlat { BgColor = tint };
		style.ContentMarginLeft = 10;
		style.ContentMarginRight = 10;
		style.ContentMarginTop = 6;
		style.ContentMarginBottom = 8;
		panel.AddThemeStyleboxOverride("panel", style);
		var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		box.AddThemeConstantOverride("separation", 2);
		panel.AddChild(box);

		var head = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		head.AddThemeConstantOverride("separation", 6);
		if (score != null)
		{
			head.AddChild(MenuStyle.Text(score, 30, accent, MenuStyle.SemiBold));
			if (limit != null)
			{
				Label of = MenuStyle.Text("/" + limit, 22, MenuStyle.TextMuted, MenuStyle.Medium);
				of.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
				head.AddChild(of);
			}
		}
		Label title = MenuStyle.Text(name, 28, MenuStyle.TextMain, MenuStyle.SemiBold);
		title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		title.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
		title.ClipText = true;
		head.AddChild(title);
		head.AddChild(StatCells("Score", "Kills", "Deaths", "K/D", MenuStyle.TextDim, 15));
		box.AddChild(head);

		box.AddChild(new ColorRect { Color = accent, CustomMinimumSize = new Vector2(0, 2), MouseFilter = Control.MouseFilterEnum.Ignore });

		if (rows.Count == 0)
		{
			box.AddChild(MenuStyle.Text("No players", 17, MenuStyle.TextMuted, MenuStyle.Medium));
			return panel;
		}
		foreach (Row r in rows) box.AddChild(BuildPlayerRow(r, me));
		return panel;
	}

	private static Control BuildPlayerRow(Row r, long me)
	{
		bool isMe = r.PeerId == me;
		Color color = isMe ? Gold : MenuStyle.TextMain;
		if (r.Dead) color = new Color(color, DeadAlpha);

		var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		var style = new StyleBoxFlat { BgColor = isMe ? new Color(Gold, 0.14f) : new Color(0, 0, 0, 0.28f) };
		style.ContentMarginLeft = 10;
		style.ContentMarginRight = 10;
		style.ContentMarginTop = 2;
		style.ContentMarginBottom = 2;
		panel.AddThemeStyleboxOverride("panel", style);

		var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		Label name = MenuStyle.Text(BotRoster.DisplayName(r.PeerId), 19, color, MenuStyle.SemiBold);
		name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		name.ClipText = true;
		row.AddChild(name);
		row.AddChild(StatCells(r.Points.ToString(), r.Kills.ToString(), r.Deaths.ToString(),
			GameUiState.FormatKd(r.Kills, r.Deaths), color, 19));
		panel.AddChild(row);
		return panel;
	}

	/// <summary>The four number columns, used by the header and every row so they line up exactly.</summary>
	private static Control StatCells(string score, string kills, string deaths, string kd, Color color, int size)
	{
		var cells = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		cells.AddThemeConstantOverride("separation", 0);
		void Cell(string text, float width)
		{
			Label label = MenuStyle.Text(text, size, color, MenuStyle.SemiBold, HorizontalAlignment.Center);
			label.CustomMinimumSize = new Vector2(width, 0);
			label.ClipText = true;
			cells.AddChild(label);
		}
		Cell(score, 96);
		Cell(kills, 84);
		Cell(deaths, 84);
		Cell(kd, 84);
		return cells;
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