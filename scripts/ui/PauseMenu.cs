using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// In-game tablet: Esc (keyboard), Start (gamepad), Android's Back button/gesture, or the MENU
/// button TouchControls draws. It displays the existing main menu inside the supplied 3D tablet,
/// so players can use the same operator, loadout, settings, and play flows from the warehouse.
///
/// It does NOT pause the game. This is a LAN multiplayer shooter: the other players keep playing, the
/// clock keeps running and you can still be shot while you read the map - the same as every online
/// shooter's menu. What it does do is stop YOUR gameplay input while it is open (GameUiState) and
/// release the mouse; closing it hands both back.
///
/// Built entirely in code like the rest of the UI, and added by PlayerHud as a child of the local
/// player's HUD - never for remote players' copies of the player. Screens that show live numbers
/// (objectives, ammo) register small refresh callbacks in _live, run four times a second instead of
/// rebuilding the screen, so scroll position and focus survive.
/// </summary>
public partial class PauseMenu : CanvasLayer
{
    private enum Section { Objectives, Players, Map, WeaponSet, Items, Settings }

    /// <summary>Set by PlayerHud before the node enters the tree.</summary>
    public CharacterBody3D Player { get; set; } = null!;

    private const float LiveRefreshSeconds = 0.25f;

    private WeaponSwitcher? _weapons;
    private PlayerScore? _score;
    private Health? _health;
    private CharacterLoadout? _loadout;

    private Control _root = null!;
    // Retained for the legacy in-match detail builders below. The visible tablet now hosts the
    // actual main menu scene inside its screen viewport.
    private Control _nav = null!;
    private Control _content = null!;
    private Node3D? _tabletModel;
    private Control? _tabletMenu;
    private Control? _connectionNotice;
    private Label _subtitle = null!;
    private Control? _modal;
    // Set while a player's stat card is open: re-checks once a moment whether a fresher copy of that
    // card has arrived over the network, and redraws it if so.
    private Action? _cardRefresh;

    private bool _open;
    private bool _inHub;
    private bool _connectionLost;
    private Section _section = Section.Objectives;
    private SettingsPanel? _settingsPanel;
    private readonly List<Action> _live = new();
    private float _liveTimer;

    public bool IsOpen => _open;

    // -------------------------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------------------------

    public override void _Ready()
    {
        Layer = 80;
        GameUiState.EnsureInputActions();
        // The hub has its own tablet (HubTablet). Building this one as well would instantiate a whole
        // hidden main menu per player, and that menu reacts to "match starting" by changing scenes too.
        if (HubState.Active)
        {
            _inHub = true;
            SetProcess(false);
            SetProcessInput(false);
            return;
        }
        _weapons = Player.GetNodeOrNull<WeaponSwitcher>("WeaponSwitcher");
        _score = Player.GetNodeOrNull<PlayerScore>("Score");
        _health = Player.GetNodeOrNull<Health>("Health");
        _loadout = Player.GetNodeOrNull<CharacterLoadout>("CharacterLoadout");
        AttachTabletModel();

        BuildShell();
        _root.Visible = false;
        SetProcess(false);
        Multiplayer.ServerDisconnected += OnServerDisconnected;
    }

    public override void _ExitTree()
    {
        if (_inHub) return;
        if (Multiplayer != null) Multiplayer.ServerDisconnected -= OnServerDisconnected;
        // Leaving the scene with the menu open (scene change, player freed) must not leave the rest
        // of the game believing a menu is still up.
        if (_open) GameUiState.Reset();
    }

    public void Toggle()
    {
        // In the hub the tablet is the menu (HubTablet); this match menu stays out of the way.
        if (_inHub) { HubTablet.Instance?.Toggle(); return; }
        if (_connectionLost) return;
        if (_open) Close(); else Open();
    }

    /// <summary>Put the supplied tablet in the local first-person view while its tactical menu is
    /// open. The menu controls remain screen-space so they stay readable at every resolution.</summary>
    private void AttachTabletModel()
    {
		var camera = Player.GetNodeOrNull<Node3D>("Head/CameraShake/FirstPersonCamera");
        var tabletScene = ResourceLoader.Load<PackedScene>("res://art/ui/tablet/rugged_tactical_tablet.glb");
        if (camera == null || tabletScene == null) return;
        _tabletModel = tabletScene.Instantiate<Node3D>();
        _tabletModel.Name = "TacticalTabletViewModel";
        _tabletModel.Position = new Vector3(0.0f, -0.02f, -0.82f);
        _tabletModel.Rotation = new Vector3(0.0f, Mathf.Pi, Mathf.Pi * 0.5f);
        _tabletModel.Scale = new Vector3(2.8f, 2.8f, 2.8f);
        _tabletModel.Visible = false;
        camera.AddChild(_tabletModel);
    }

    public override void _Input(InputEvent @event)
    {
        if (_inHub) return;
        if (_connectionLost) return;

        // A key row in SETTINGS > CONTROLS is waiting for a key: Escape then means "cancel the
        // rebind", and SettingsPanel handles it in _UnhandledInput. Stay out of its way.
        if (_open && _settingsPanel != null && GodotObject.IsInstanceValid(_settingsPanel)
            && _settingsPanel.IsCapturingKey) return;

        bool pausePressed = @event.IsActionPressed(GameUiState.PauseAction);
        bool backPressed = _open && @event.IsActionPressed("ui_cancel");
        if (!pausePressed && !backPressed) return;

        // The match-results screen owns the whole screen; the pause menu must not open under it.
        if (GameUiState.ResultsOpen) return;

        // With the quit question showing, Escape / Back answers NO rather than closing everything.
        if (_open && _modal != null && GodotObject.IsInstanceValid(_modal))
        {
            GetViewport().SetInputAsHandled();
            CancelQuit();
            return;
        }

        GetViewport().SetInputAsHandled();
        Toggle();
    }

    public override void _Notification(int what)
    {
        // Android Back button / gesture. Needs application/config/quit_on_go_back=false in
        // project.godot, otherwise the engine quits the app before a script ever sees it.
        if (what == (int)NotificationWMGoBackRequest && !GameUiState.ResultsOpen)
        {
            if (_open && _modal != null && GodotObject.IsInstanceValid(_modal)) CancelQuit();
            else Toggle();
        }
    }

    public override void _Process(double delta)
    {
        _liveTimer += (float)delta;
        if (_liveTimer < LiveRefreshSeconds) return;
        _liveTimer = 0.0f;
        RunLive();
    }

    private void RunLive()
    {
        UpdateSubtitle();
        foreach (Action refresh in _live) refresh();
        _cardRefresh?.Invoke();
    }

    /// <summary>"TEAM DEATHMATCH  -  03:07" under the title, so the mode and clock are visible from
    /// every screen, not just OBJECTIVES.</summary>
    private void UpdateSubtitle()
    {
        MatchManager? match = MatchManager.Instance;
        if (match == null) { _subtitle.Text = ""; return; }
        string mode = match.ModeRules.DisplayName.ToUpperInvariant();
        float seconds = match.HasTimeLimit ? match.TimeRemainingSeconds : match.ElapsedSeconds;
        _subtitle.Text = $"{mode}  -  {GameUiState.FormatClock(seconds)}{(match.HasTimeLimit ? " left" : "")}";
    }

    // -------------------------------------------------------------------------------------------
    // Open / close
    // -------------------------------------------------------------------------------------------

    private void Open()
    {
        if (_open || GameUiState.ResultsOpen) return;
        _open = true;
        GameUiState.SetMenuOpen(true);
        if (_tabletModel != null) _tabletModel.Visible = true;
        if (!OS.HasFeature("mobile")) Input.MouseMode = Input.MouseModeEnum.Visible;
        // A finger that was on the joystick or FIRE when MENU was tapped would otherwise leave that
        // action pressed for good, because its release lands on the menu overlay instead.
        Player.GetNodeOrNull<TouchControls>("TouchControls")?.ReleaseAllHeld();
        Player.GetNodeOrNull<Scoreboard>("Hud/Scoreboard")?.HideNow();

        _root.Visible = true;
        if (_tabletMenu != null) _tabletMenu.ProcessMode = ProcessModeEnum.Inherit;
        SetProcess(false);
    }

    private void Close()
    {
        if (!_open) return;
        _open = false;
        if (!_connectionLost) CloseModal();
        GameUiState.SetMenuOpen(false);
        if (_tabletModel != null && IsInstanceValid(_tabletModel)) _tabletModel.Visible = false;
        _root.Visible = false;
        if (_tabletMenu != null && IsInstanceValid(_tabletMenu)) _tabletMenu.ProcessMode = ProcessModeEnum.Disabled;
        SetProcess(false);
        if (!OS.HasFeature("mobile")) Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    private void OnServerDisconnected()
    {
        // The host closed the match (or the Wi-Fi dropped). There is nothing left to play, so lock
        // the menu open on a single way out instead of letting the player wander a frozen arena.
        if (_connectionLost) return;
        // After the final whistle the host going back to its menu is normal; the results screen has
        // its own way out, so do not bury it under a CONNECTION LOST popup.
        if (MatchManager.Instance != null && MatchManager.Instance.IsMatchEnded) return;
        _connectionLost = true;
        if (!_open)
        {
            _open = true;
            GameUiState.SetMenuOpen(true);
            if (_tabletModel != null) _tabletModel.Visible = true;
            if (!OS.HasFeature("mobile")) Input.MouseMode = Input.MouseModeEnum.Visible;
            Player.GetNodeOrNull<Scoreboard>("Hud/Scoreboard")?.HideNow();
            _root.Visible = true;
            if (_tabletMenu != null) _tabletMenu.ProcessMode = ProcessModeEnum.Inherit;
        }
        if (_connectionNotice != null) _connectionNotice.Visible = true;
        SetProcess(false);
    }

    // -------------------------------------------------------------------------------------------
    // Tablet shell and its live main-menu screen
    // -------------------------------------------------------------------------------------------

    private void BuildShell()
    {
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        MenuStyle.Fill(_root);
        AddChild(_root);

        var dim = new ColorRect { Color = new Color(0.01f, 0.02f, 0.025f, 0.16f), MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(dim);
        _root.AddChild(dim);
        var frame = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Place(frame, 0.17f, 0.13f, 0.83f, 0.87f, 0, 0, 0, 0);
        var frameStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.025f, 0.04f, 0.045f, 0.96f),
            BorderColor = new Color(0.25f, 0.46f, 0.42f, 0.96f),
            BorderWidthLeft = 5, BorderWidthTop = 5, BorderWidthRight = 5, BorderWidthBottom = 5,
            CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12,
        };
        frame.AddThemeStyleboxOverride("panel", frameStyle);
        _root.AddChild(frame);

        var screen = new SubViewportContainer { Stretch = true, MouseFilter = Control.MouseFilterEnum.Stop };
        MenuStyle.Place(screen, 0.5f, 0.5f, 0.5f, 0.5f, -360, -220, 360, 220);
        frame.AddChild(screen);

        var viewport = new SubViewport
        {
            Size = new Vector2I(1280, 720),
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.WhenVisible,
        };
        screen.AddChild(viewport);

        // This is the game's actual main menu, hosted as the tablet's display so operator, weapon,
        // settings, profile and play actions keep their existing behavior inside the device.
        var mainMenuScene = ResourceLoader.Load<PackedScene>("res://scenes/MainMenu.tscn");
        if (mainMenuScene != null)
        {
            _tabletMenu = mainMenuScene.Instantiate<Control>();
            _tabletMenu.ProcessMode = ProcessModeEnum.Disabled;
            MenuStyle.Fill(_tabletMenu);
            viewport.AddChild(_tabletMenu);
        }

        _connectionNotice = MenuStyle.Text("HOST LINK LOST", 18,
            new Color(1.0f, 0.34f, 0.24f), MenuStyle.Bold);
        MenuStyle.Place(_connectionNotice, 0.5f, 0.13f, 0.5f, 0.13f, -100, -26, 100, 4);
        _connectionNotice.Visible = false;
        _root.AddChild(_connectionNotice);
    }

    private void BuildNav()
    {
        ClearChildren(_nav);
        Button? selected = null;

        var resume = MenuStyle.Card("play-solid", "RESUME", null, new Vector2(0, 62), Close,
            titleSize: 28, iconSize: 26);
        _nav.AddChild(resume);
        _nav.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4), MouseFilter = Control.MouseFilterEnum.Ignore });

        void Item(string icon, string label, Section section)
        {
            Button button = MenuStyle.SelectRow(icon, label, _section == section, () => ShowSection(section));
            _nav.AddChild(button);
            if (_section == section) selected = button;
        }
        Item("check", "OBJECTIVES", Section.Objectives);
        Item("users", "PLAYERS", Section.Players);
        Item("map", "MAP", Section.Map);
        Item("rifle", "WEAPON SET", Section.WeaponSet);
        Item("helmet-gear", "ITEMS", Section.Items);
        Item("settings", "SETTINGS", Section.Settings);
        // One button. It does not open a page of choices: it asks a single YES / NO question.
        _nav.AddChild(MenuStyle.SelectRow("power", "QUIT GAME", false, ShowQuitConfirm));

        (selected ?? resume).GrabFocus();
    }

    private void ShowSection(Section section)
    {
        if (!_open) return;
        bool changed = section != _section;
        _section = section;
        _live.Clear();
        _settingsPanel = null;
        ClearChildren(_content);
        if (changed) BuildNav();

        switch (section)
        {
            case Section.Objectives: BuildScrolled(BuildObjectives); break;
            case Section.Players: BuildScrolled(BuildPlayers); break;
            case Section.Map: BuildMap(); break;
            case Section.WeaponSet: BuildScrolled(BuildWeaponSet); break;
            case Section.Items: BuildScrolled(BuildItems); break;
            case Section.Settings: BuildSettings(); break;
        }
        RunLive();
    }

    /// <summary>Most screens are a stack of panels that may be taller than a phone's landscape
    /// screen, so they scroll. Map and Settings size themselves and skip this.</summary>
    private void BuildScrolled(Action<VBoxContainer> build)
    {
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        MenuStyle.Fill(scroll);
        _content.AddChild(scroll);
        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 14);
        scroll.AddChild(column);
        build(column);
    }

    /// <summary>Removes immediately (so layout never sees the old children next to the new ones) and
    /// frees later (so it is safe to do from inside one of the removed buttons' own click handlers).</summary>
    private static void ClearChildren(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            parent.RemoveChild(child);
            child.QueueFree();
        }
    }

    // -------------------------------------------------------------------------------------------
    // Shared bits
    // -------------------------------------------------------------------------------------------

    private long MyPeerId => Player.GetMultiplayerAuthority();

    private MatchStats.Stats MyStats => MatchStats.Instance?.Get(MyPeerId) ?? new MatchStats.Stats();

    /// <summary>A label whose text is recomputed by the live refresh instead of being fixed.</summary>
    private Label LiveLabel(Func<string> text, int size, Color color, Font? font = null,
        HorizontalAlignment align = HorizontalAlignment.Left)
    {
        Label label = MenuStyle.Text(text(), size, color, font ?? MenuStyle.SemiBold, align);
        _live.Add(() => { if (GodotObject.IsInstanceValid(label)) label.Text = text(); });
        return label;
    }

    private static Label Wrapped(string text, int size, Color color)
    {
        Label label = MenuStyle.Text(text, size, color, MenuStyle.Medium);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return label;
    }

    private static ProgressBar Bar(float fraction, Color fill, float height = 12.0f)
    {
        var bar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, Value = Mathf.Clamp(fraction, 0.0f, 1.0f),
            ShowPercentage = false, CustomMinimumSize = new Vector2(0, height),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        bar.AddThemeStyleboxOverride("background", MenuStyle.Box(MenuStyle.RowFill, MenuStyle.Hairline, 1, 2));
        bar.AddThemeStyleboxOverride("fill", MenuStyle.Box(fill, fill, 0, 2));
        return bar;
    }

    private static void SetBar(ProgressBar bar, float fraction, Color fill)
    {
        bar.Value = Mathf.Clamp(fraction, 0.0f, 1.0f);
        bar.AddThemeStyleboxOverride("fill", MenuStyle.Box(fill, fill, 0, 2));
    }

    /// <summary>Big number over a small caption - the tiles on the objectives screen.</summary>
    private Control StatTile(string caption, Func<string> value)
    {
        var tile = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = MenuStyle.Box(MenuStyle.RowFill, MenuStyle.Hairline);
        style.SetContentMarginAll(10);
        tile.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", -2);
        box.AddChild(MenuStyle.Text(caption, 14, MenuStyle.TextMuted, MenuStyle.SemiBold, HorizontalAlignment.Center));
        var number = LiveLabel(value, 30, MenuStyle.TextMain, MenuStyle.Bold, HorizontalAlignment.Center);
        box.AddChild(number);
        tile.AddChild(box);
        return tile;
    }

    // -------------------------------------------------------------------------------------------
    // OBJECTIVES
    // -------------------------------------------------------------------------------------------

    private void BuildObjectives(VBoxContainer column)
    {
        MatchManager match = MatchManager.Instance;
        bool teamMode = match != null && match.IsTeamMode;

        var panel = MenuStyle.Panel("OBJECTIVES", "check", out var body);
        body.AddChild(MenuStyle.Text(teamMode ? "TEAM DEATHMATCH" : "DEATHMATCH", 30, MenuStyle.TextMain, MenuStyle.Bold));

        if (teamMode)
        {
            body.AddChild(Wrapped(
                $"Eliminate the other team. The first team to {match!.ScoreLimit} kills wins the match" +
                (match.HasTimeLimit ? $", or whoever leads when the {match.TimeLimitMinutes}-minute clock runs out." : "."),
                19, MenuStyle.TextDim));
            body.AddChild(LiveLabel(() =>
            {
                int team = match.GetTeam(MyPeerId);
                return team >= 0 ? $"YOU ARE ON TEAM {MatchManager.TeamName(team)}" : "WAITING FOR A TEAM ASSIGNMENT";
            }, 20, MenuStyle.Accent, MenuStyle.Bold));

            for (int team = 0; team < 2; team++)
            {
                int captured = team;
                Color color = MatchManager.TeamColor(captured);
                var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                line.AddChild(MenuStyle.Text($"TEAM {MatchManager.TeamName(captured)}", 18, color, MenuStyle.Bold));
                var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
                line.AddChild(spacer);
                line.AddChild(LiveLabel(() => $"{match.GetTeamScore(captured)} / {match.ScoreLimit} kills", 18,
                    MenuStyle.TextMain, MenuStyle.SemiBold, HorizontalAlignment.Right));
                body.AddChild(line);
                ProgressBar bar = Bar(0, color, 14);
                body.AddChild(bar);
                _live.Add(() =>
                {
                    if (GodotObject.IsInstanceValid(bar))
                        SetBar(bar, match.ScoreLimit > 0 ? match.GetTeamScore(captured) / (float)match.ScoreLimit : 0, color);
                });
            }
        }
        else
        {
            body.AddChild(Wrapped(
                "Free for all. Eliminate as many players as you can - headshots, hip-fire and spin " +
                "kills score bonus points. The match has no time or kill limit; you can leave any time from QUIT GAME.",
                19, MenuStyle.TextDim));
        }

        body.AddChild(LiveLabel(() =>
        {
            if (match == null) return "";
            if (match.IsMatchEnded)
                return match.WinningTeam < 0 ? "MATCH OVER - DRAW" : $"MATCH OVER - TEAM {MatchManager.TeamName(match.WinningTeam)} WINS";
            return match.HasTimeLimit
                ? $"TIME LEFT  {GameUiState.FormatClock(match.TimeRemainingSeconds)}"
                : $"MATCH TIME  {GameUiState.FormatClock(match.ElapsedSeconds)}   -   no time limit";
        }, 22, MenuStyle.TextMain, MenuStyle.Bold));

        if (teamMode)
        {
            body.AddChild(MenuStyle.Text(
                $"Friendly fire {(match!.FriendlyFire ? "ON" : "OFF")}   -   team indicators {(match.TeamIndicators ? "ON" : "OFF")}",
                16, MenuStyle.TextMuted, MenuStyle.Medium));
        }
        column.AddChild(panel);

        var mine = MenuStyle.Panel("YOUR MATCH", "user", out var mineBody);
        var tiles = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        tiles.AddThemeConstantOverride("separation", 8);
        tiles.AddChild(StatTile("SCORE", () => (_score?.TotalPoints ?? 0).ToString()));
        tiles.AddChild(StatTile("KILLS", () => MyStats.Kills.ToString()));
        tiles.AddChild(StatTile("DEATHS", () => MyStats.Deaths.ToString()));
        tiles.AddChild(StatTile("ASSISTS", () => MyStats.Assists.ToString()));
        tiles.AddChild(StatTile("K/D", () => GameUiState.FormatKd(MyStats.Kills, MyStats.Deaths)));
        mineBody.AddChild(tiles);
        column.AddChild(mine);
    }

    // -------------------------------------------------------------------------------------------
    // PLAYERS - everyone in the match; tap one to open their stat card
    // -------------------------------------------------------------------------------------------

    private void BuildPlayers(VBoxContainer column)
    {
        var panel = MenuStyle.Panel("PLAYERS", "users", out var body);
        body.AddChild(Wrapped("Tap a player to see their career: main stats, best weapons and medals.", 17, MenuStyle.TextMuted));

        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 8);
        body.AddChild(list);
        Label botNote = Wrapped(string.Empty, 16, MenuStyle.TextMuted);
        body.AddChild(botNote);

        // The list is rebuilt only when who is here (or what we know about them) changes, so a
        // button you are about to press does not vanish from under your finger every quarter second.
        string signature = string.Empty;
        void Refresh()
        {
            long me = PlayerIdentity.IdOf(Player);
            List<ScoreboardData.Row> rows = ScoreboardData.Collect(GetTree());
            List<ScoreboardData.Row> humans = rows.FindAll(r => !BotRoster.IsBot(r.PeerId));
            humans.Sort((a, b) => a.PeerId == me ? -1 : (b.PeerId == me ? 1 : a.PeerId.CompareTo(b.PeerId)));
            int bots = rows.Count - humans.Count;

            string now = $"{PlayerDirectory.Version}|{bots}|{string.Join(",", humans.ConvertAll(r => r.PeerId))}";
            if (now == signature) return;
            signature = now;

            ClearChildren(list);
            foreach (ScoreboardData.Row row in humans) list.AddChild(PlayerListRow(row, me));
            botNote.Text = bots > 0
                ? $"{bots} bot{(bots == 1 ? string.Empty : "s")} in this match. Bots have no career record."
                : string.Empty;
        }
        Refresh();
        _live.Add(() => { if (GodotObject.IsInstanceValid(list)) Refresh(); });
        column.AddChild(panel);
    }

    private Control PlayerListRow(ScoreboardData.Row row, long me)
    {
        long id = row.PeerId;
        bool known = PlayerDirectory.TryGet(id, out PlayerCard card);
        Control avatar = known ? AvatarView.Create(card, 46) : AvatarView.CreateUnknown(46);
        string detail = known
            ? $"K/D {CareerStats.FormatKd(card.Stats.KdRatio)}  \u00b7  {CareerStats.FormatCount(card.Stats.Kills)} kills  \u00b7  {CareerStats.FormatCount(card.Stats.MatchesPlayed)} matches"
            : "Stats not received yet";
        return MenuStyle.PlayerRow(avatar, ScoreboardData.DisplayName(row, me), detail,
            () => ShowPlayerCard(id), highlight: id == me);
    }

    /// <summary>Full-screen stat card for one player. Opens at once with what this machine already
    /// has, asks that player for a fresh copy, and redraws when it arrives. Uses the same modal slot
    /// as the quit question, so Esc / Back / BACK all close it the same way.</summary>
    private void ShowPlayerCard(long peerId)
    {
        if (_connectionLost || !_open) return;
        CloseModal();

        long me = PlayerIdentity.IdOf(Player);
        if (peerId != me) CareerTracker.Instance?.RequestFreshCard(peerId);

        _modal = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        MenuStyle.Fill(_modal);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.88f), MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(dim);
        _modal.AddChild(dim);

        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 10);
        MenuStyle.Place(column, 0.5f, 0, 0.5f, 1, -410, 20, 410, -20);
        _modal.AddChild(column);

        Button back = MenuStyle.Card("arrow-left", "BACK", null, new Vector2(180, 54), CancelQuit,
            titleSize: 24, iconSize: 24);
        back.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        column.AddChild(back);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        column.AddChild(scroll);
        var host = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        scroll.AddChild(host);

        PlayerCard? shown = null;
        bool drawn = false;
        void Fill()
        {
            bool known = PlayerDirectory.TryGet(peerId, out PlayerCard card);
            if (drawn && (known ? ReferenceEquals(card, shown) : shown == null)) return; // nothing new
            drawn = true;
            shown = known ? card : null;
            ClearChildren(host);
            host.AddChild(known
                ? StatCard.Build(card, isYou: peerId == me, wide: true)
                : StatCard.BuildUnavailable(BotRoster.DisplayName(peerId),
                    "Waiting for their stats to arrive over the network..."));
        }
        Fill();
        _cardRefresh = Fill;

        _root.AddChild(_modal);
        back.GrabFocus();
    }

    // -------------------------------------------------------------------------------------------
    // MAP
    // -------------------------------------------------------------------------------------------

    private void BuildMap()
    {
        string mapName = "BATTLEFIELD";
        Node? map = GetTree().CurrentScene?.GetNodeOrNull("Map");
        if (map != null && !string.IsNullOrEmpty(map.SceneFilePath))
            mapName = System.IO.Path.GetFileNameWithoutExtension(map.SceneFilePath).ToUpperInvariant();

        var panel = MenuStyle.Panel($"MAP  -  {mapName}", "map", out var body);
        MenuStyle.Fill(panel);
        _content.AddChild(panel);

        var view = new MapView { LocalPlayer = Player, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddChild(view);

        MatchManager match = MatchManager.Instance;
        bool teamMode = match != null && match.IsTeamMode;
        var legend = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        legend.AddThemeConstantOverride("separation", 24);
        legend.AddChild(LegendChip(Colors.White, "YOU"));
        if (teamMode) legend.AddChild(LegendChip(MatchManager.TeamColor(match!.GetTeam(MyPeerId)), "TEAMMATES"));
        legend.AddChild(MenuStyle.Text("Enemies are never shown.   Grid: 10 m.   Up = north.", 15, MenuStyle.TextMuted, MenuStyle.Medium));
        body.AddChild(legend);
    }

    private static Control LegendChip(Color color, string text)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(new ColorRect { Color = color, CustomMinimumSize = new Vector2(14, 14), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore });
        row.AddChild(MenuStyle.Text(text, 16, MenuStyle.TextDim, MenuStyle.SemiBold));
        return row;
    }

    // -------------------------------------------------------------------------------------------
    // WEAPON SET
    // -------------------------------------------------------------------------------------------

    private void BuildWeaponSet(VBoxContainer column)
    {
        var panel = MenuStyle.Panel("WEAPON SET", "rifle", out var body);
        body.AddChild(Wrapped("Choose a weapon to equip it - the same as pressing its number key.", 17, MenuStyle.TextMuted));
        if (_weapons == null)
        {
            body.AddChild(Wrapped("No weapon system on this player.", 20, MenuStyle.TextDim));
            column.AddChild(panel);
            return;
        }
        for (int i = 0; i < WeaponSwitcher.LoadoutCount; i++)
            if (_weapons.CanCarryIndex(i)) body.AddChild(WeaponRow(i));
        column.AddChild(panel);
    }

    private static StyleBoxFlat FocusRing()
    {
        var ring = new StyleBoxFlat { DrawCenter = false, BorderColor = MenuStyle.BorderHot };
        ring.SetBorderWidthAll(2);
        ring.SetCornerRadiusAll(2);
        return ring;
    }

    private Control StatColumn(string caption, string value, float width)
    {
        var box = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(width, 0),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        box.AddThemeConstantOverride("separation", -3);
        box.AddChild(MenuStyle.Text(caption, 13, MenuStyle.TextMuted, MenuStyle.SemiBold));
        box.AddChild(MenuStyle.Text(value, 20, MenuStyle.TextMain, MenuStyle.Bold));
        return box;
    }

    private Button WeaponRow(int index)
    {
        WeaponData? data = _weapons!.GetWeaponData(index);
        bool equipped = index == _weapons.EquippedIndex;
        string name = data?.WeaponName ?? WeaponSwitcher.Loadout[index];

        var button = new Button
        {
            CustomMinimumSize = new Vector2(0, 70),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.All,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        button.AddThemeStyleboxOverride("normal", MenuStyle.Box(equipped ? MenuStyle.SelectedFill : MenuStyle.RowFill, equipped ? MenuStyle.SelectedBorder : MenuStyle.Hairline));
        button.AddThemeStyleboxOverride("hover", MenuStyle.Box(equipped ? MenuStyle.SelectedFill : MenuStyle.HoverFill, MenuStyle.Border));
        button.AddThemeStyleboxOverride("pressed", MenuStyle.Box(MenuStyle.PressFill, MenuStyle.BorderHot));
        button.AddThemeStyleboxOverride("hover_pressed", MenuStyle.Box(MenuStyle.PressFill, MenuStyle.BorderHot));
        button.AddThemeStyleboxOverride("focus", FocusRing());
        button.Pressed += () =>
        {
            if (_weapons!.TryEquip(index)) ShowSection(Section.WeaponSet);
        };

        var pad = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (string side in new[] { "margin_left", "margin_right" }) pad.AddThemeConstantOverride(side, 14);
        foreach (string side in new[] { "margin_top", "margin_bottom" }) pad.AddThemeConstantOverride(side, 6);
        MenuStyle.Fill(pad);
        button.AddChild(pad);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 14);
        pad.AddChild(row);

        var slot = MenuStyle.Text((index + 1).ToString(), 30, equipped ? MenuStyle.Accent : MenuStyle.TextMuted, MenuStyle.Bold, HorizontalAlignment.Center);
        slot.CustomMinimumSize = new Vector2(30, 0);
        slot.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(slot);

        var names = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
        names.AddThemeConstantOverride("separation", -3);
        var nameLabel = MenuStyle.Text(name, 24, MenuStyle.TextMain, MenuStyle.Bold);
        nameLabel.ClipText = true;
        names.AddChild(nameLabel);
        names.AddChild(MenuStyle.Text(data?.WeaponClass ?? "", 15, MenuStyle.TextDim, MenuStyle.Medium));
        row.AddChild(names);

        if (data != null)
        {
            bool melee = data.MagazineSize <= 0;
            string damage = melee || Mathf.IsEqualApprox(data.BodyDamage, data.HeadshotDamage)
                ? $"{data.BodyDamage:0}"
                : $"{data.BodyDamage:0} / {data.HeadshotDamage:0}";
            if (data.PelletsPerShot > 1) damage = $"{data.BodyDamage:0} x{data.PelletsPerShot}";
            row.AddChild(StatColumn("DAMAGE", damage, 92));
            row.AddChild(StatColumn("RPM", $"{data.FireRateRPM:0}", 56));
            row.AddChild(StatColumn("AMMO", melee ? "-" : $"{data.MagazineSize} + {data.MaxReserveAmmo}", 78));
        }

        var status = MenuStyle.Text(equipped ? "EQUIPPED" : "EQUIP", 18, equipped ? MenuStyle.Accent : MenuStyle.TextDim,
            MenuStyle.Bold, HorizontalAlignment.Right);
        status.CustomMinimumSize = new Vector2(84, 0);
        status.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(status);
        return button;
    }

    // -------------------------------------------------------------------------------------------
    // ITEMS
    // -------------------------------------------------------------------------------------------

    private static readonly (string Title, string[] Classes)[] ItemGroups =
    {
        ("PRIMARY WEAPONS", new[] { "Assault Rifle", "SMG", "Shotgun" }),
        ("SIDEARMS", new[] { "Pistol" }),
        ("HEAVY", new[] { "Launcher" }),
        ("MELEE", new[] { "Melee" }),
    };

    private void BuildItems(VBoxContainer column)
    {
        if (_health != null)
        {
            var condition = MenuStyle.Panel("CONDITION", "user", out var conditionBody);
            conditionBody.AddChild(LiveLabel(() =>
                $"HEALTH  {Mathf.CeilToInt(_health.CurrentHealth)} / {Mathf.CeilToInt(_health.MaxHealth)}",
                22, MenuStyle.TextMain, MenuStyle.Bold));
            ProgressBar hp = Bar(1, new Color(0.25f, 0.75f, 0.30f), 14);
            conditionBody.AddChild(hp);
            _live.Add(() =>
            {
                if (!GodotObject.IsInstanceValid(hp)) return;
                float fraction = _health.MaxHealth > 0 ? _health.CurrentHealth / _health.MaxHealth : 0;
                SetBar(hp, fraction, fraction < 0.3f ? new Color(0.85f, 0.20f, 0.20f) : new Color(0.25f, 0.75f, 0.30f));
            });
            column.AddChild(condition);
        }

        var carried = MenuStyle.Panel("WEAPONS & AMMO", "rifle", out var carriedBody);
        if (_weapons == null)
        {
            carriedBody.AddChild(Wrapped("No weapon system on this player.", 20, MenuStyle.TextDim));
        }
        else
        {
            var used = new HashSet<int>();
            foreach ((string title, string[] classes) in ItemGroups)
            {
                var indices = new List<int>();
                for (int i = 0; i < WeaponSwitcher.LoadoutCount; i++)
                {
                    WeaponData? data = _weapons.GetWeaponData(i);
                    if (data != null && _weapons.CanCarryIndex(i) && Array.IndexOf(classes, data.WeaponClass) >= 0) indices.Add(i);
                }
                if (indices.Count == 0) continue;
                carriedBody.AddChild(MenuStyle.Text(title, 16, MenuStyle.TextMuted, MenuStyle.Bold));
                foreach (int i in indices) { used.Add(i); carriedBody.AddChild(AmmoRow(i)); }
            }
            // Anything in the loadout that does not match a group above still gets listed, so a new
            // weapon class can never silently vanish from this screen.
            var other = new List<int>();
            for (int i = 0; i < WeaponSwitcher.LoadoutCount; i++) if (!used.Contains(i) && _weapons.CanCarryIndex(i)) other.Add(i);
            if (other.Count > 0)
            {
                carriedBody.AddChild(MenuStyle.Text("OTHER", 16, MenuStyle.TextMuted, MenuStyle.Bold));
                foreach (int i in other) carriedBody.AddChild(AmmoRow(i));
            }
        }
        column.AddChild(carried);

        var kits = MenuStyle.Panel("KITS", "helmet-gear", out var kitsBody);
        CharacterData? operatorData = _loadout?.Data;
        if (operatorData != null)
        {
            string name = string.IsNullOrEmpty(operatorData.DisplayName) ? operatorData.CharacterId : operatorData.DisplayName;
            kitsBody.AddChild(MenuStyle.Text($"{name}  -  {operatorData.Class}", 22, MenuStyle.TextMain, MenuStyle.Bold));
            if (!string.IsNullOrEmpty(operatorData.SkillName))
                kitsBody.AddChild(MenuStyle.Text($"Skill: {operatorData.SkillName}", 18, MenuStyle.Accent, MenuStyle.SemiBold));
            foreach (StatModifier buff in operatorData.Buffs)
                kitsBody.AddChild(Wrapped($"+  {ModifierText(buff)}", 17, new Color(0.45f, 0.85f, 0.50f)));
            foreach (StatModifier drawback in operatorData.Drawbacks)
                kitsBody.AddChild(Wrapped($"-  {ModifierText(drawback)}", 17, new Color(0.95f, 0.45f, 0.40f)));
        }
        else
        {
            kitsBody.AddChild(Wrapped("No operator kit equipped.", 20, MenuStyle.TextDim));
        }
        kitsBody.AddChild(Wrapped("No medkits, grenades or gadgets in this build yet.", 16, MenuStyle.TextMuted));
        column.AddChild(kits);
    }

    private static string ModifierText(StatModifier modifier) =>
        !string.IsNullOrEmpty(modifier.Note) ? modifier.Note : modifier.EffectId;

    /// <summary>One weapon: name, magazine, reserve and a bar for total rounds left. The equipped one
    /// is marked. Melee has no ammo, so it just says so.</summary>
    private Control AmmoRow(int index)
    {
        WeaponData data = _weapons!.GetWeaponData(index)!;
        bool melee = data.MagazineSize <= 0;

        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = MenuStyle.Box(MenuStyle.RowFill, MenuStyle.Hairline);
        style.SetContentMarginAll(10);
        panel.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);

        var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        top.AddThemeConstantOverride("separation", 10);
        Label name = LiveLabel(() => (_weapons.EquippedIndex == index ? "> " : "") + data.WeaponName, 21,
            MenuStyle.TextMain, MenuStyle.Bold);
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        top.AddChild(name);

        if (melee)
        {
            top.AddChild(MenuStyle.Text("MELEE  -  no ammo needed", 18, MenuStyle.TextDim, MenuStyle.SemiBold, HorizontalAlignment.Right));
            box.AddChild(top);
            return panel;
        }

        Label numbers = LiveLabel(() =>
        {
            _weapons.TryGetAmmo(index, out int mag, out int reserve);
            return $"MAG {mag} / {data.MagazineSize}     RESERVE {reserve} / {data.MaxReserveAmmo}";
        }, 18, MenuStyle.TextDim, MenuStyle.SemiBold, HorizontalAlignment.Right);
        top.AddChild(numbers);
        box.AddChild(top);

        ProgressBar bar = Bar(1, MenuStyle.Accent, 10);
        box.AddChild(bar);
        _live.Add(() =>
        {
            if (!GodotObject.IsInstanceValid(bar)) return;
            _weapons.TryGetAmmo(index, out int mag, out int reserve);
            int capacity = data.MagazineSize + data.MaxReserveAmmo;
            float fraction = capacity > 0 ? (mag + reserve) / (float)capacity : 0;
            SetBar(bar, fraction, mag + reserve <= 0 ? new Color(0.85f, 0.20f, 0.20f) : MenuStyle.Accent);
        });
        return panel;
    }

    // -------------------------------------------------------------------------------------------
    // SETTINGS
    // -------------------------------------------------------------------------------------------

    private void BuildSettings()
    {
        _settingsPanel = new SettingsPanel();
        MenuStyle.Fill(_settingsPanel);
        _content.AddChild(_settingsPanel);
    }

    // -------------------------------------------------------------------------------------------
    // QUIT GAME - one button, one YES / NO question
    // -------------------------------------------------------------------------------------------

    private void ShowQuitConfirm()
    {
        if (_connectionLost || !_open) return;
        CloseModal();

        bool hosting = GetNodeOrNull<LanSession>("/root/LanSession")?.IsHosting ?? false;
        _modal = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        MenuStyle.Fill(_modal);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.82f), MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(dim);
        _modal.AddChild(dim);
        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(center);
        _modal.AddChild(center);

        var panel = MenuStyle.Panel("QUIT GAME?", "power", out var body);
        panel.CustomMinimumSize = new Vector2(520, 0);
        var text = MenuStyle.Text(
            hosting
                ? "You are hosting this match. Quitting ends it for every player connected to you."
                : "Leave this match and go back to the main menu? Your score for this match will be lost.",
            22, hosting ? MenuStyle.Accent : MenuStyle.TextDim, MenuStyle.Medium);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(text);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 14);
        var yes = MenuStyle.Card("check", "YES", null, new Vector2(0, 64),
            () => GameSession.LeaveToMainMenu(GetTree()), titleSize: 28, iconSize: 28);
        yes.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var no = MenuStyle.Card("arrow-left", "NO", null, new Vector2(0, 64), CancelQuit,
            titleSize: 28, iconSize: 28);
        no.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        buttons.AddChild(yes);
        buttons.AddChild(no);
        body.AddChild(buttons);

        center.AddChild(panel);
        _root.AddChild(_modal);
        no.GrabFocus(); // the safe answer is the default one
    }

    private void CancelQuit()
    {
        CloseModal();
        if (_open) BuildNav();
    }

    private void CloseModal()
    {
        if (_modal != null && GodotObject.IsInstanceValid(_modal)) _modal.QueueFree();
        _modal = null;
        _cardRefresh = null;
    }
}
