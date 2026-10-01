using Godot;
using System.Text;

/// <summary>
/// The training overlay: objective panel, timer/countdown, contextual prompt, channel bar (heal/repair/arm/deploy),
/// vehicle readout, callout log, toasts, the [M] map, and - on touch devices - an action bar so every training
/// action (interact, ability, callouts, map, menu) is reachable without a keyboard.
///
/// It only READS TrainingSession state; the mechanics and cars write PromptText / ChannelProgress / VehicleStatus.
/// </summary>
public partial class TrainingHud : CanvasLayer
{
    private Label _title = null!;
    private Label _objective = null!;
    private Label _status = null!;
    private Label _timer = null!;
    private Label _prompt = null!;
    private Label _toast = null!;
    private Label _callouts = null!;
    private Label _vehicle = null!;
    private Label _countdown = null!;
    private Label _channelLabel = null!;
    private ProgressBar _channelBar = null!;
    private TrainingMapView _map = null!;

    private double _clock;
    private double _toastExpires;
    private double _toastFade = 0.4;
    private bool _touch;

    public override void _Ready()
    {
        Layer = 6;
        ProcessMode = ProcessModeEnum.Always;
        _touch = SettingsManager.Instance != null && SettingsManager.Instance.ShouldShowTouchControls();

        var root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(root);
        AddChild(root);

        _map = new TrainingMapView { Name = "Map", Visible = false };
        root.AddChild(_map);

        BuildObjectivePanel(root);

        _timer = MenuStyle.Text("", 34, MenuStyle.TextMain, MenuStyle.Bold, HorizontalAlignment.Center);
        MenuStyle.Place(_timer, 0.5f, 0, 0.5f, 0, -140, 12, 140, 60);
        root.AddChild(_timer);

        _countdown = MenuStyle.Text("", 120, new Color(1.0f, 0.9f, 0.5f), MenuStyle.Bold, HorizontalAlignment.Center);
        MenuStyle.Place(_countdown, 0.5f, 0.4f, 0.5f, 0.4f, -150, -90, 150, 90);
        root.AddChild(_countdown);

        _toast = MenuStyle.Text("", 34, Colors.White, MenuStyle.Bold, HorizontalAlignment.Center);
        MenuStyle.Place(_toast, 0.5f, 0.26f, 0.5f, 0.26f, -450, 0, 450, 50);
        root.AddChild(_toast);

        _callouts = MenuStyle.Text("", 20, MenuStyle.TextMain, MenuStyle.SemiBold, HorizontalAlignment.Right);
        MenuStyle.Place(_callouts, 1, 0.3f, 1, 0.3f, -420, 0, -16, 160);
        root.AddChild(_callouts);

        _vehicle = MenuStyle.Text("", 24, new Color(0.75f, 0.95f, 1.0f), MenuStyle.SemiBold, HorizontalAlignment.Center);
        MenuStyle.Place(_vehicle, 0.5f, 0.82f, 0.5f, 0.82f, -500, 0, 500, 34);
        root.AddChild(_vehicle);

        _channelLabel = MenuStyle.Text("", 24, MenuStyle.TextMain, MenuStyle.Bold, HorizontalAlignment.Center);
        MenuStyle.Place(_channelLabel, 0.5f, 0.62f, 0.5f, 0.62f, -260, 0, 260, 32);
        root.AddChild(_channelLabel);

        _channelBar = new ProgressBar { MinValue = 0, MaxValue = 1, Value = 0, ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _channelBar.AddThemeStyleboxOverride("fill", MenuStyle.Box(MenuStyle.Accent, MenuStyle.BorderHot, 1, 2, 0));
        _channelBar.AddThemeStyleboxOverride("background", MenuStyle.Box(new Color(0.05f, 0.05f, 0.06f, 0.85f), MenuStyle.Hairline, 1, 2, 0));
        MenuStyle.Place(_channelBar, 0.5f, 0.62f, 0.5f, 0.62f, -180, 34, 180, 54);
        root.AddChild(_channelBar);

        _prompt = MenuStyle.Text("", 26, new Color(1.0f, 0.95f, 0.75f), MenuStyle.SemiBold, HorizontalAlignment.Center);
        MenuStyle.Place(_prompt, 0.5f, 0.72f, 0.5f, 0.72f, -450, 0, 450, 36);
        root.AddChild(_prompt);

        if (_touch) BuildTouchBar(root);
    }

    private void BuildObjectivePanel(Control root)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        StyleBoxFlat style = MenuStyle.Box(new Color(0.02f, 0.02f, 0.03f, 0.72f), MenuStyle.Hairline, 1, 3, 0);
        style.SetContentMarginAll(12);
        panel.AddThemeStyleboxOverride("panel", style);
        MenuStyle.Place(panel, 0, 0, 0, 0, 16, _touch ? 120 : 16, 420, _touch ? 120 : 16);
        root.AddChild(panel);

        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 4);
        panel.AddChild(column);

        _title = MenuStyle.Text("", 22, MenuStyle.Accent, MenuStyle.Bold);
        column.AddChild(_title);
        _objective = MenuStyle.Text("", 18, MenuStyle.TextMain, MenuStyle.Medium);
        _objective.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _objective.CustomMinimumSize = new Vector2(390, 0);
        column.AddChild(_objective);
        _status = MenuStyle.Text("", 18, new Color(0.7f, 0.95f, 0.75f), MenuStyle.SemiBold);
        column.AddChild(_status);
    }

    private void BuildTouchBar(Control root)
    {
        var bar = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("separation", 8);
        MenuStyle.Place(bar, 0.5f, 0, 0.5f, 0, -230, 70, 230, 70);
        root.AddChild(bar);

        AddTouchButton(bar, "USE", TrainingInput.Interact);
        AddTouchButton(bar, "ABIL", TrainingInput.AbilityPrimary);
        AddTouchButton(bar, "ALT", TrainingInput.AbilitySecondary);
        AddTouchButton(bar, "CALL", TrainingInput.CalloutQuick);
        AddTouchButton(bar, "LIST", TrainingInput.CalloutMenu);
        AddTouchButton(bar, "MAP", TrainingInput.MapToggle);
        AddTouchButton(bar, "II", TrainingInput.TrainingMenu);
    }

    /// <summary>Drives a real InputMap action from a touch button: pressed while the finger is down, released when it
    /// lifts. That works for hold actions (abilities) and, because the action stays pressed across at least one frame,
    /// for tap actions read with IsActionJustPressed too.</summary>
    private static void AddTouchButton(Control parent, string text, string action)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(62, 56), FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", 18);
        button.ButtonDown += () => Input.ActionPress(action);
        button.ButtonUp += () => Input.ActionRelease(action);
        parent.AddChild(button);
    }

    public void ToggleMap() => _map.Visible = !_map.Visible;

    public void Toast(string text, Color color, float seconds)
    {
        _toast.Text = text;
        _toast.AddThemeColorOverride("font_color", color);
        _toast.Modulate = Colors.White;
        _toastExpires = _clock + seconds;
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        TrainingSession? session = TrainingSession.Instance;
        if (session == null) return;

        UpdatePanel(session);
        UpdateTimer(session);
        UpdateCallouts(session);

        _prompt.Text = session.PromptText;
        _vehicle.Text = session.VehicleStatus;

        bool channeling = session.ChannelProgress >= 0.0f;
        _channelBar.Visible = channeling;
        _channelLabel.Visible = channeling;
        if (channeling)
        {
            _channelBar.Value = session.ChannelProgress;
            _channelLabel.Text = session.ChannelLabel;
        }

        double left = _toastExpires - _clock;
        _toast.Visible = left > 0.0;
        if (left > 0.0 && left < _toastFade) _toast.Modulate = new Color(1, 1, 1, (float)(left / _toastFade));
    }

    private void UpdatePanel(TrainingSession session)
    {
        TrainingDrill? drill = session.ActiveDrill;
        if (drill == null)
        {
            _title.Text = "FREE ROAM";
            _objective.Text = $"Walk around the arena. [{TrainingInput.KeyText(TrainingInput.TrainingMenu)}] opens the Training Center menu.";
            _status.Text = string.Empty;
            return;
        }
        _title.Text = drill.Definition.Title.ToUpperInvariant();
        _objective.Text = drill.Objective;
        _status.Text = drill.StatusLine;
    }

    private void UpdateTimer(TrainingSession session)
    {
        TrainingDrill? drill = session.ActiveDrill;
        float countdown = session.CountdownRemaining;
        if (countdown > 0.0f)
        {
            _countdown.Text = Mathf.CeilToInt(countdown).ToString();
            _countdown.Visible = true;
        }
        else
        {
            _countdown.Visible = false;
        }

        if (drill == null || drill.Outcome == DrillOutcome.Aborted)
        {
            _timer.Text = string.Empty;
            return;
        }
        float par = drill.Definition.ParSeconds;
        _timer.Text = par > 0.0f ? $"{TrainingUtil.Clock(drill.Elapsed)}  /  par {TrainingUtil.Clock(par)}" : TrainingUtil.Clock(drill.Elapsed);
    }

    private void UpdateCallouts(TrainingSession session)
    {
        var text = new StringBuilder();
        int shown = 0;
        for (int i = session.Callouts.Recent.Count - 1; i >= 0 && shown < 5; i--)
        {
            CalloutMessage message = session.Callouts.Recent[i];
            if (session.Callouts.Clock - message.Time > 9.0f) break;
            if (shown > 0) text.Append('\n');
            text.Append(message.Sender).Append(": ").Append(CalloutInfo.Text(message.Type));
            shown++;
        }
        _callouts.Text = text.ToString();
    }
}
