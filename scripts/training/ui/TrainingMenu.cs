using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Every full-screen training menu: the hub (pick a class track, then a drill), the drill briefing, pause,
/// the results/coaching screen, and the callout picker. All of them pause the game while open. Built from
/// MenuStyle so it matches the rest of the game.
/// </summary>
public partial class TrainingMenu : CanvasLayer
{
    private enum ScreenId { None, Hub, Detail, Pause, Results, Callouts }

    private Control _root = null!;
    private Control _content = null!;
    private Control _hubBackdrop = null!;
    private ScreenId _screen = ScreenId.None;
    private TrainingTrack _track = TrainingTrack.Fundamentals;
    private DrillDefinition? _detail;
    private ulong _openedFrame;

    public bool IsOpen => _screen != ScreenId.None;

    public override void _Ready()
    {
        Layer = 20;
        ProcessMode = ProcessModeEnum.Always;

        _root = new Control { Name = "Root", Visible = false };
        MenuStyle.Fill(_root);
        AddChild(_root);

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.8f), MouseFilter = Control.MouseFilterEnum.Stop };
        MenuStyle.Fill(dim);
        _root.AddChild(dim);

        _hubBackdrop = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(_hubBackdrop);
        _root.AddChild(_hubBackdrop);
        MenuStyle.AddBackdrop(_hubBackdrop);

        _content = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(_content);
        _root.AddChild(_content);
    }

    public override void _Process(double delta)
    {
        if (!IsOpen || Engine.GetProcessFrames() == _openedFrame) return;

        bool back = Input.IsActionJustPressed(TrainingInput.TrainingMenu) || Input.IsActionJustPressed("ui_cancel");
        if (_screen == ScreenId.Callouts && Input.IsActionJustPressed(TrainingInput.CalloutMenu)) back = true;
        if (!back) return;

        switch (_screen)
        {
            case ScreenId.Detail: ShowHub(); break;
            case ScreenId.Results: ShowHub(); break;
            default: CloseAll(); break;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Open / close
    // ---------------------------------------------------------------------------------------------
    private void Open(ScreenId screen)
    {
        if (!IsOpen) _openedFrame = Engine.GetProcessFrames();
        _screen = screen;
        _root.Visible = true;
        _hubBackdrop.Visible = screen == ScreenId.Hub;
        TrainingSession.Instance?.SetPausedForMenu(true);
        foreach (Node child in _content.GetChildren())
        {
            _content.RemoveChild(child);
            child.QueueFree();
        }
    }

    public void CloseAll()
    {
        _screen = ScreenId.None;
        _root.Visible = false;
        TrainingSession.Instance?.SetPausedForMenu(false);
    }

    private static TrainingSession Session => TrainingSession.Instance!;

    // ---------------------------------------------------------------------------------------------
    // Hub
    // ---------------------------------------------------------------------------------------------
    public void ShowHub()
    {
        Open(ScreenId.Hub);

        var title = MenuStyle.Text("TRAINING CENTER", 46, MenuStyle.TextMain, MenuStyle.Bold);
        MenuStyle.Place(title, 0, 0, 1, 0, 48, 24, -48, 84);
        _content.AddChild(title);
        var sub = MenuStyle.Text("Trainee: MANNY   |   every drill runs on the baseline class kit", 18, MenuStyle.TextDim, MenuStyle.Medium);
        MenuStyle.Place(sub, 0, 0, 1, 0, 50, 82, -48, 112);
        _content.AddChild(sub);

        BuildTrackColumn();
        BuildDrillColumn();
        BuildHubFooter();
    }

    private void BuildTrackColumn()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        MenuStyle.Place(column, 0, 0, 0, 1, 48, 126, 388, -96);
        _content.AddChild(column);

        foreach (TrainingTrack track in Enum.GetValues<TrainingTrack>())
        {
            TrainingTrack captured = track;
            column.AddChild(MenuStyle.SelectRow(TrainingTrackInfo.Icon(track), TrainingTrackInfo.Title(track), track == _track, () =>
            {
                _track = captured;
                ShowHub();
            }));
        }

        PanelContainer panel = MenuStyle.Panel("YOUR PROGRESS", "check", out VBoxContainer body);
        panel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        column.AddChild(panel);

        TrainingSkill? weakest = TrainingProgress.WeakestSkill();
        if (weakest.HasValue)
        {
            float average = TrainingProgress.SkillAverage(weakest.Value) ?? 0.0f;
            body.AddChild(WrapText($"Focus area: {weakest.Value} (avg {average:0}).", 18, MenuStyle.TextMain));
        }
        else
        {
            body.AddChild(WrapText("No runs yet. Start with the Movement Course, then work through your class.", 18, MenuStyle.TextMain));
        }

        DrillDefinition? next = TrainingProgress.Recommend(DrillRegistry.All);
        if (next != null)
        {
            body.AddChild(WrapText($"Recommended: {next.Title}", 18, MenuStyle.TextDim));
            body.AddChild(MenuStyle.Row("Go", next.Title, () => ShowDetail(next)));
        }
        else
        {
            body.AddChild(WrapText("Every drill is a B or better. Chase the S grades.", 18, MenuStyle.TextDim));
        }
    }

    private void BuildDrillColumn()
    {
        PanelContainer panel = MenuStyle.Panel(TrainingTrackInfo.Title(_track), TrainingTrackInfo.Icon(_track), out VBoxContainer body);
        MenuStyle.Place(panel, 0, 0, 1, 1, 412, 126, -48, -96);
        _content.AddChild(panel);

        body.AddChild(WrapText(TrainingTrackInfo.Blurb(_track), 18, MenuStyle.TextDim));

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(list);

        foreach (DrillDefinition definition in DrillRegistry.All)
        {
            if (definition.Track != _track) continue;
            DrillDefinition captured = definition;
            list.AddChild(MenuStyle.Row(definition.Title, GradeText(definition), () => ShowDetail(captured)));
        }
    }

    private void BuildHubFooter()
    {
        var footer = new HBoxContainer();
        footer.AddThemeConstantOverride("separation", 16);
        MenuStyle.Place(footer, 0, 1, 1, 1, 48, -78, -48, -24);
        _content.AddChild(footer);

        footer.AddChild(MenuStyle.Primary("FREE ROAM", new Vector2(240, 54), CloseAll));
        Control ammo = MenuStyle.ToggleRow("rifle", "Infinite reserve ammo", Session.InfiniteReserveAmmo, on => Session.InfiniteReserveAmmo = on);
        ammo.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        footer.AddChild(ammo);
        footer.AddChild(MenuStyle.Primary("MAIN MENU", new Vector2(240, 54), ExitToMainMenu));
    }

    private static string GradeText(DrillDefinition definition)
    {
        int runs = TrainingProgress.Runs(definition.Id);
        if (runs == 0) return "NEW";
        float best = TrainingProgress.BestRating(definition.Id);
        return $"{TrainingGrade.Letter(best)}  {best:0}%   |   {runs} run{(runs == 1 ? "" : "s")}";
    }

    // ---------------------------------------------------------------------------------------------
    // Drill briefing
    // ---------------------------------------------------------------------------------------------
    private void ShowDetail(DrillDefinition definition)
    {
        Open(ScreenId.Detail);
        _detail = definition;

        PanelContainer panel = MenuStyle.Panel(definition.Title.ToUpperInvariant(), TrainingTrackInfo.Icon(definition.Track), out VBoxContainer body);
        MenuStyle.Place(panel, 0.5f, 0.5f, 0.5f, 0.5f, -430, -300, 430, 300);
        _content.AddChild(panel);

        body.AddChild(WrapText(definition.Description, 20, MenuStyle.TextMain));
        body.AddChild(WrapText("GOAL:  " + definition.Goal, 20, new Color(1.0f, 0.85f, 0.5f)));
        if (!string.IsNullOrEmpty(definition.Controls)) body.AddChild(WrapText("CONTROLS:  " + definition.Controls, 18, MenuStyle.TextDim));

        var skills = new List<string>();
        foreach (TrainingSkill skill in definition.Skills) skills.Add(skill.ToString());
        body.AddChild(WrapText("TRAINS:  " + string.Join(", ", skills) + $"     PAR:  {TrainingUtil.Clock(definition.ParSeconds)}", 18, MenuStyle.TextDim));
        body.AddChild(WrapText("YOUR BEST:  " + GradeText(definition), 18, MenuStyle.TextMain));

        var buttons = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkEnd };
        buttons.AddThemeConstantOverride("separation", 16);
        buttons.AddChild(MenuStyle.Primary("START", new Vector2(260, 56), () => Session.StartDrill(definition)));
        buttons.AddChild(MenuStyle.Primary("BACK", new Vector2(200, 56), ShowHub));
        body.AddChild(buttons);
    }

    // ---------------------------------------------------------------------------------------------
    // Pause
    // ---------------------------------------------------------------------------------------------
    public void ShowPause()
    {
        Open(ScreenId.Pause);

        PanelContainer panel = MenuStyle.Panel("PAUSED", "settings", out VBoxContainer body);
        MenuStyle.Place(panel, 0.5f, 0.5f, 0.5f, 0.5f, -260, -230, 260, 230);
        _content.AddChild(panel);

        body.AddChild(MenuStyle.Row("Resume", "", CloseAll));
        if (Session.ActiveDefinition != null)
            body.AddChild(MenuStyle.Row("Restart drill", Session.ActiveDefinition.Title, () => Session.StartDrill(Session.ActiveDefinition)));
        body.AddChild(MenuStyle.Row("Training Center", "pick another drill", () =>
        {
            Session.ReturnToHub(false);
            ShowHub();
        }));
        body.AddChild(MenuStyle.Row("Main menu", "leave training", ExitToMainMenu));
    }

    // ---------------------------------------------------------------------------------------------
    // Results
    // ---------------------------------------------------------------------------------------------
    public void ShowResults(TrainingDrill drill)
    {
        Open(ScreenId.Results);
        bool success = drill.Outcome == DrillOutcome.Succeeded;
        string letter = TrainingGrade.Letter(drill.Rating);

        PanelContainer panel = MenuStyle.Panel(success ? "DRILL COMPLETE" : "DRILL FAILED", TrainingTrackInfo.Icon(drill.Definition.Track), out VBoxContainer body);
        MenuStyle.Place(panel, 0.5f, 0.5f, 0.5f, 0.5f, -470, -330, 470, 330);
        _content.AddChild(panel);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 24);
        head.AddChild(MenuStyle.Text(letter, 110, TrainingGrade.GradeColor(letter), MenuStyle.Bold));
        var headText = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        headText.AddChild(MenuStyle.Text(drill.Definition.Title, 28, MenuStyle.TextMain, MenuStyle.Bold));
        headText.AddChild(MenuStyle.Text($"{drill.Rating:0}%   |   {TrainingUtil.Clock(drill.Elapsed)}   |   best {TrainingProgress.BestRating(drill.Definition.Id):0}%", 18, MenuStyle.TextDim, MenuStyle.Medium));
        head.AddChild(headText);
        body.AddChild(head);

        if (!string.IsNullOrEmpty(drill.ResultMessage)) body.AddChild(WrapText(drill.ResultMessage, 20, MenuStyle.TextMain));

        foreach ((string label, string value) in drill.Results)
        {
            var row = new HBoxContainer();
            var name = MenuStyle.Text(label, 18, MenuStyle.TextDim, MenuStyle.Medium);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(name);
            row.AddChild(MenuStyle.Text(value, 18, MenuStyle.TextMain, MenuStyle.SemiBold, HorizontalAlignment.Right));
            body.AddChild(row);
        }

        string[] tips = drill.CoachingTips();
        if (tips.Length > 0)
        {
            body.AddChild(MenuStyle.Text("COACH", 18, MenuStyle.Accent, MenuStyle.Bold));
            foreach (string tip in tips) body.AddChild(WrapText("- " + tip, 17, MenuStyle.TextMain));
        }

        var buttons = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkEnd };
        buttons.AddThemeConstantOverride("separation", 14);
        buttons.AddChild(MenuStyle.Primary("RETRY", new Vector2(200, 54), () => Session.StartDrill(drill.Definition)));
        DrillDefinition? next = TrainingProgress.Recommend(DrillRegistry.All);
        if (next != null && next.Id != drill.Definition.Id)
            buttons.AddChild(MenuStyle.Primary("NEXT", new Vector2(200, 54), () => ShowDetail(next)));
        buttons.AddChild(MenuStyle.Primary("HUB", new Vector2(200, 54), () =>
        {
            Session.ReturnToHub(false);
            ShowHub();
        }));
        body.AddChild(buttons);
    }

    // ---------------------------------------------------------------------------------------------
    // Callout picker
    // ---------------------------------------------------------------------------------------------
    public void ShowCalloutPicker()
    {
        Open(ScreenId.Callouts);

        PanelContainer panel = MenuStyle.Panel("CALLOUTS", "users", out VBoxContainer body);
        MenuStyle.Place(panel, 0.5f, 0.5f, 0.5f, 0.5f, -430, -320, 430, 320);
        _content.AddChild(panel);

        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 8);
        body.AddChild(grid);

        foreach (CalloutType type in CalloutInfo.MenuOrder)
        {
            CalloutType captured = type;
            grid.AddChild(MenuStyle.Row(CalloutInfo.Text(type), "", () =>
            {
                CloseAll();
                Session.PlayerCallout(captured);
            }));
        }
        body.AddChild(MenuStyle.Row("Cancel", "", CloseAll));
    }

    // ---------------------------------------------------------------------------------------------
    private static Label WrapText(string text, int size, Color color)
    {
        Label label = MenuStyle.Text(text, size, color, MenuStyle.Medium);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.CustomMinimumSize = new Vector2(200, 0);
        return label;
    }

    private void ExitToMainMenu()
    {
        GetTree().Paused = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        GetTree().ChangeSceneToFile("res://scenes/MainMenu.tscn");
    }
}
