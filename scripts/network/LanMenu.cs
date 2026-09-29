using Godot;
using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// The main menu and its screens (the scene root of MainMenu.tscn - the class keeps its old name so
/// the scene and .uid files stay valid). Look and layout follow the mockups; the visual building
/// blocks live in MenuStyle.
///
/// Flow, as in the menu-flow diagram:
///   Main  -> Choose Operator / Customize Player / Customize Gun / Train   (not built yet: styled
///            "coming soon" screens, so no button is dead)
///   Main  -> Play -> Vs Bots (not built yet) | Play Locally (LAN: Host or Connect - fully working)
///
/// The LAN logic (host, UDP discovery, join, teardown) is unchanged from the old menu.
/// </summary>
public partial class LanMenu : Control
{
    private enum LocalMode { Host, Connect }

    private const string DefaultGameName = "Wraith Run LAN";

    private readonly Dictionary<string, (string Name, string Host, int Players, ulong Seen)> _games = new();
    private PacketPeerUdp? _listener;
    private Timer _refreshTimer = null!;
    private Control _contentRoot = null!;
    private VBoxContainer? _gamesList;
    private Label? _status;
    private Action? _goBack;
    private LocalMode _localMode = LocalMode.Host;
    private string _hostName = DefaultGameName;
    private bool _leavingMenu;
    private bool _terminalCleanupDone;
    private bool _sceneChangeQueued;

    private AudioStreamPlayer? _menuMusic;
    private AudioStream[] _menuMusicTracks = Array.Empty<AudioStream>();
    private readonly RandomNumberGenerator _menuMusicRng = new();

    public override void _Ready()
    {
        _refreshTimer = new Timer { WaitTime = 0.5, OneShot = false };
        _refreshTimer.Timeout += PollDiscovery;
        AddChild(_refreshTimer);
        MenuStyle.AddBackdrop(this);
        _contentRoot = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _contentRoot.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_contentRoot);
        StartMenuMusic();
        ShowMain();
    }

    /// <summary>
    /// SettingsManager already applies MusicVolume to the "Music" bus (see SettingsManager.cs's
    /// ApplyAudioSettings) and always has, in anticipation of a music system landing at some point -
    /// this is that system. Two tracks played back to back in random order rather than either one
    /// looping alone, so the menu does not obviously repeat itself on a long lobby wait.
    /// </summary>
    private void StartMenuMusic()
    {
        _menuMusicRng.Randomize();
        _menuMusicTracks = LoadMenuMusicTracks();
        if (_menuMusicTracks.Length == 0) return;

        _menuMusic = new AudioStreamPlayer { Bus = "Music" };
        AddChild(_menuMusic);
        _menuMusic.Finished += PlayNextMenuMusicTrack;
        PlayNextMenuMusicTrack();
    }

    private void PlayNextMenuMusicTrack()
    {
        if (_menuMusic == null || _menuMusicTracks.Length == 0) return;
        _menuMusic.Stream = _menuMusicTracks[_menuMusicRng.RandiRange(0, _menuMusicTracks.Length - 1)];
        _menuMusic.Play();
    }

    private static AudioStream[] LoadMenuMusicTracks()
    {
        var streams = new System.Collections.Generic.List<AudioStream>();
        foreach (string path in new[]
        {
            "res://audio/Clips/UI and HUD/Menu Music/TacticalSilence.mp3",
            "res://audio/Clips/UI and HUD/Menu Music/TacticalSilenceV2.mp3",
        })
        {
            var stream = ResourceLoader.Load<AudioStream>(path);
            if (stream != null) streams.Add(stream);
        }
        return streams.ToArray();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Escape / gamepad B goes back one screen; on the main screen it does nothing. (While the
        // settings screen is waiting for a key to rebind, SettingsPanel handles that Escape itself
        // and marks it handled, so it never gets here.)
        if (_goBack == null || _leavingMenu || !@event.IsActionPressed("ui_cancel")) return;
        GetViewport().SetInputAsHandled();
        _goBack();
    }

    public override void _Notification(int what)
    {
        // Android Back button / gesture. project.godot now sets quit_on_go_back=false so the in-game
        // pause menu can use Back; this keeps the main menu behaving as it always did: back goes up
        // one screen, and on the main screen it quits the app.
        if (what != (int)NotificationWMGoBackRequest || _leavingMenu) return;
        if (_goBack != null) _goBack();
        else GetTree().Quit();
    }

    // -------------------------------------------------------------------------------------------
    // Screens
    // -------------------------------------------------------------------------------------------

    /// <summary>Common start of every screen: stop LAN listening, drop the old controls, remember
    /// where "back" goes.</summary>
    private void BeginScreen(Action? back)
    {
        StopLobbyBrowser();
        _leavingMenu = false;
        _goBack = back;
        _gamesList = null;
        _status = null;
        QueueFreeChildren(_contentRoot);
    }

    private void ShowMain()
    {
        if (_terminalCleanupDone) return;
        BeginScreen(null);

        CharacterData? op = OperatorInfo.Find(PlayerProfile.OperatorId);
        WeaponData? weapon = OperatorInfo.FindWeapon(PlayerProfile.WeaponName);

        // The selected operator, live in 3D, in the middle of the screen. Added first so every
        // button below draws over it; the stage itself never takes mouse input.
        var stage = new OperatorStage();
        MenuStyle.Place(stage, 0.335f, 0.27f, 0.665f, 0.985f, 0, 0, 0, 0);
        _contentRoot.AddChild(stage);
        stage.ShowOperator(op, weapon);

        var title = new VBoxContainer();
        title.AddThemeConstantOverride("separation", 2);
        title.AddChild(MenuStyle.Text("WRAITH RUN", 42, MenuStyle.TextMain,
            MenuStyle.Spaced(MenuStyle.Bold, 3)));
        var underline = new ColorRect { Color = MenuStyle.Accent, CustomMinimumSize = new Vector2(64, 3) };
        title.AddChild(underline);
        MenuStyle.Place(title, 0, 0, 0, 0, 64, 22, 64, 22);
        _contentRoot.AddChild(title);
        AddTopIcons(includeSettings: true, reopen: ShowMain);

        // Top row: everything that edits the character or its gear.
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 10);
        MenuStyle.Place(top, 0, 0, 0, 0, 64, 108, 64 + 3 * 196 + 20, 108);
        _contentRoot.AddChild(top);
        top.AddChild(MenuStyle.Card("users", "CHOOSE\nOPERATOR", null, new Vector2(196, 68),
            ShowOperators, titleSize: 19, iconSize: 36));
        top.AddChild(MenuStyle.Card("weapon", "CHOOSE\nWEAPON", null, new Vector2(196, 68),
            ShowWeapons, titleSize: 19, iconSize: 36));
        top.AddChild(MenuStyle.Card("helmet-gear", "CUSTOMIZE\nPLAYER", null, new Vector2(196, 68),
            ShowCustomize, titleSize: 19, iconSize: 36));

        // Left column, lower half: a smaller PLAY (the middle of the screen belongs to the
        // operator now) and the bot range.
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);
        MenuStyle.Place(column, 0, 0.5f, 0, 0.5f, 64, 0, 64 + 300, 0);
        _contentRoot.AddChild(column);

        var play = MenuStyle.Hero("PLAY", "Select to Start Match", new Vector2(300, 84), ShowPlay);
        column.AddChild(play);
        column.AddChild(MenuStyle.Card("crosshair", "TRAIN BUTTON", "Train With Bots",
            new Vector2(300, 72), () => ShowSoon("TRAIN",
                "A shooting range with bot targets needs bot AI, which isn't built yet.", ShowMain),
            titleSize: 20, iconSize: 40, badge: "AI"));

        // Right column: what is currently equipped. Each tile opens its own screen.
        var right = new VBoxContainer();
        right.AddThemeConstantOverride("separation", 10);
        MenuStyle.Place(right, 1, 0, 1, 0, -(64 + 300), 108, -64, 108);
        _contentRoot.AddChild(right);

        Button operatorTile = MenuStyle.Tile(new Vector2(300, 118), ShowOperators, out VBoxContainer operatorBox);
        operatorBox.AddChild(MenuStyle.Text("OPERATOR", 14, MenuStyle.Accent, MenuStyle.Bold));
        operatorBox.AddChild(MenuStyle.Text(OperatorInfo.Callsign(op), 32, MenuStyle.TextMain, MenuStyle.Bold));
        operatorBox.AddChild(MenuStyle.Text(
            op == null ? "Standard issue" : $"{OperatorInfo.ClassName(op.Class)}  |  {op.SkillName}",
            16, MenuStyle.TextDim));
        right.AddChild(operatorTile);

        Button weaponTile = MenuStyle.Tile(new Vector2(300, 118), ShowWeapons, out VBoxContainer weaponBox);
        weaponBox.AddChild(MenuStyle.Text("STARTING WEAPON", 14, MenuStyle.Accent, MenuStyle.Bold));
        weaponBox.AddChild(MenuStyle.Text(weapon == null ? PlayerProfile.WeaponName.ToUpperInvariant() : weapon.WeaponName.ToUpperInvariant(),
            32, MenuStyle.TextMain, MenuStyle.Bold));
        weaponBox.AddChild(MenuStyle.Text(
            weapon == null ? string.Empty
                : weapon.FireMode == WeaponData.FireModeType.Melee
                    ? $"{weapon.BodyDamage:0} dmg  |  melee"
                    : $"{weapon.BodyDamage:0} dmg  |  {weapon.FireRateRPM:0} rpm  |  {weapon.MagazineSize} rd",
            16, MenuStyle.TextDim));
        right.AddChild(weaponTile);

        play.GrabFocus();
    }

    private void ShowOperators()
    {
        if (_terminalCleanupDone) return;
        BeginScreen(ShowMain);
        AddHeader("CHOOSE OPERATOR", ShowMain);
        AddTopIcons(includeSettings: true, reopen: ShowOperators);
        new OperatorScreen().Build(_contentRoot);
    }

    private void ShowWeapons()
    {
        if (_terminalCleanupDone) return;
        BeginScreen(ShowMain);
        AddHeader("CHOOSE WEAPON", ShowMain);
        AddTopIcons(includeSettings: true, reopen: ShowWeapons);
        new WeaponScreen().Build(_contentRoot);
    }

    private void ShowCustomize()
    {
        if (_terminalCleanupDone) return;
        BeginScreen(ShowMain);
        AddHeader("CUSTOMIZE PLAYER", ShowMain);
        AddTopIcons(includeSettings: true, reopen: ShowCustomize);
        new CustomizeScreen().Build(_contentRoot);
    }

    private void ShowPlay()
    {
        if (_terminalCleanupDone) return;
        BeginScreen(ShowMain);
        AddHeader("PLAY", ShowMain);
        AddTopIcons(includeSettings: true, reopen: ShowPlay);

        var panel = MenuStyle.Panel("CHOOSE HOW TO PLAY", null, out var body);
        MenuStyle.Place(panel, 0.56f, 0, 1, 0, 0, 130, -64, 130);
        body.AddThemeConstantOverride("separation", 14);
        body.AddChild(MenuStyle.Card("bot", "VS BOTS", "Fight AI opponents on your own",
            new Vector2(0, 104), () => ShowSoon("VS BOTS",
                "Playing against bots needs bot AI, which isn't built yet.", ShowPlay),
            titleSize: 30, iconSize: 50));
        var local = MenuStyle.Card("wifi", "PLAY LOCALLY", "Host or join a match on your Wi-Fi",
            new Vector2(0, 104), ShowLocal, titleSize: 30, iconSize: 50);
        body.AddChild(local);
        _contentRoot.AddChild(panel);
        local.GrabFocus();
    }

    private void ShowLocal()
    {
        if (_terminalCleanupDone) return;
        BeginScreen(ShowPlay);
        AddHeader("PLAY LOCALLY", ShowPlay);
        AddTopIcons(includeSettings: true, reopen: ShowLocal);

        var layout = new HBoxContainer();
        layout.AddThemeConstantOverride("separation", 20);
        MenuStyle.Place(layout, 0, 0, 1, 1, 64, 124, -64, -60);
        _contentRoot.AddChild(layout);

        // Left: lobby setup (Host / Connect), the mode/map this build has, and - host only, Team
        // Deathmatch only - the match rules a Connect screen has no say over (only the host
        // decides them; a joining client picks them up automatically from MatchManager's
        // ReceiveHostConfig the moment it connects). Scrollable, same as the Connect pane's games
        // list below, since the Match Settings panel can push this column past a phone's
        // landscape screen height.
        var leftScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(340, 0),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        layout.AddChild(leftScroll);
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 14);
        leftScroll.AddChild(left);

        var lobby = MenuStyle.Panel("LOBBY SETUP", "wifi", out var lobbyBody);
        lobbyBody.AddChild(MenuStyle.SelectRow("user", "Host", _localMode == LocalMode.Host,
            () => SetLocalMode(LocalMode.Host)));
        lobbyBody.AddChild(MenuStyle.SelectRow("users", "Connect", _localMode == LocalMode.Connect,
            () => SetLocalMode(LocalMode.Connect)));
        left.AddChild(lobby);

        MatchManager match = MatchManager.Instance;
        var mode = MenuStyle.Panel("GAME MODE", "users", out var modeBody);
        if (_localMode == LocalMode.Host)
        {
            modeBody.AddChild(MenuStyle.SelectRow("crosshair", "Deathmatch",
                match.GameMode == MatchManager.Mode.FreeForAll,
                () => SetHostGameMode(MatchManager.Mode.FreeForAll)));
            modeBody.AddChild(MenuStyle.SelectRow("users", "Team Deathmatch",
                match.GameMode == MatchManager.Mode.TeamDeathmatch,
                () => SetHostGameMode(MatchManager.Mode.TeamDeathmatch)));
        }
        else modeBody.AddChild(MenuStyle.InfoRow("users", "Deathmatch"));
        left.AddChild(mode);

        var map = MenuStyle.Panel("MAP", "map", out var mapBody);
        mapBody.AddChild(MenuStyle.InfoRow("map", "Warzone"));
        left.AddChild(map);

        if (_localMode == LocalMode.Host && match.GameMode == MatchManager.Mode.TeamDeathmatch)
        {
            var rules = MenuStyle.Panel("MATCH SETTINGS", "crosshair", out var rulesBody);
            rulesBody.AddChild(MenuStyle.ToggleRow(null, "Friendly Fire", match.FriendlyFire,
                v => { match.FriendlyFire = v; ShowLocal(); }));
            rulesBody.AddChild(MenuStyle.ToggleRow(null, "Team Indicators", match.TeamIndicators,
                v => { match.TeamIndicators = v; ShowLocal(); }));
            rulesBody.AddChild(MenuStyle.SliderRow("Score Limit", match.ScoreLimit, 10, 100, 5,
                "{0:0} kills", v => match.ScoreLimit = (int)v));
            rulesBody.AddChild(MenuStyle.SliderRow("Time Limit", match.TimeLimitMinutes, 3, 20, 1,
                "{0:0} min", v => match.TimeLimitMinutes = (int)v));
            left.AddChild(rules);
        }

        // Right: what Host / Connect actually does.
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 14);
        layout.AddChild(right);
        if (_localMode == LocalMode.Host) BuildHostPane(right);
        else BuildConnectPane(right);
    }

    private void SetLocalMode(LocalMode mode)
    {
        if (_localMode == mode) return;
        _localMode = mode;
        ShowLocal();
    }

    private void SetHostGameMode(MatchManager.Mode mode)
    {
        if (MatchManager.Instance.GameMode == mode) return;
        MatchManager.Instance.GameMode = mode;
        ShowLocal();
    }

    private void BuildHostPane(Container right)
    {
        var panel = MenuStyle.Panel("HOST A GAME", "server", out var body);
        panel.SizeFlagsVertical = SizeFlags.ExpandFill;
        right.AddChild(panel);

        body.AddChild(MenuStyle.Text("GAME NAME", 16, MenuStyle.TextDim, MenuStyle.SemiBold));
        var name = new LineEdit
        {
            PlaceholderText = "Game name",
            Text = _hostName,
            MaxLength = 32,
        };
        MenuStyle.StyleLineEdit(name);
        name.TextChanged += text => _hostName = text;
        name.TextSubmitted += text => StartHost(text);
        body.AddChild(name);

        var hint = MenuStyle.Text(
            "Players on the same Wi-Fi will see this game under Connect and can join it.",
            18, MenuStyle.TextMuted, MenuStyle.Medium);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(hint);

        _status = MenuStyle.Text("", 18, MenuStyle.Accent, MenuStyle.SemiBold);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_status);

        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        actions.AddChild(MenuStyle.Primary("START HOSTING", new Vector2(300, 66),
            () => StartHost(name.Text)));
        right.AddChild(actions);
        name.GrabFocus();
    }

    private void BuildConnectPane(Container right)
    {
        var panel = MenuStyle.Panel("LAN GAMES", "wifi", out var body);
        panel.SizeFlagsVertical = SizeFlags.ExpandFill;
        right.AddChild(panel);

        _status = MenuStyle.Text("Listening for games…", 18, MenuStyle.TextDim, MenuStyle.Medium);
        body.AddChild(_status);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _gamesList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _gamesList.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_gamesList);
        body.AddChild(scroll);
        StartBrowsing();
    }

    private void ShowSettings(Action reopen)
    {
        if (_terminalCleanupDone) return;
        BeginScreen(reopen);
        AddHeader("SETTINGS", reopen);
        AddTopIcons(includeSettings: false, reopen: null);

        // The whole settings screen now lives in SettingsPanel so the in-game pause menu can show the
        // very same one; this screen only supplies the header/back button above and a place for it.
        var panel = new SettingsPanel();
        MenuStyle.Place(panel, 0, 0, 1, 1, 64, 124, -64, -60);
        _contentRoot.AddChild(panel);
    }

    private void ShowSoon(string title, string blurb, Action returnTo)
    {
        if (_terminalCleanupDone) return;
        BeginScreen(returnTo);
        AddHeader(title, returnTo);
        AddTopIcons(includeSettings: false, reopen: null);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        _contentRoot.AddChild(center);

        var panel = MenuStyle.Panel("NOT BUILT YET", "lock", out var body);
        panel.CustomMinimumSize = new Vector2(560, 0);
        var text = MenuStyle.Text(blurb, 22, MenuStyle.TextDim, MenuStyle.Medium);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(text);
        var back = MenuStyle.Card("arrow-left", "BACK", null, new Vector2(0, 56), returnTo,
            titleSize: 24, iconSize: 26);
        body.AddChild(back);
        center.AddChild(panel);
        back.GrabFocus();
    }

    private void AddHeader(string title, Action back)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 0);
        box.AddChild(MenuStyle.Text("WRAITH RUN", 16, MenuStyle.TextMain,
            MenuStyle.Spaced(MenuStyle.SemiBold, 2)));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(MenuStyle.IconButton("arrow-left", 26, MenuStyle.Accent, back));
        var divider = new ColorRect
        {
            Color = MenuStyle.Hairline,
            CustomMinimumSize = new Vector2(2, 30),
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        row.AddChild(divider);
        row.AddChild(MenuStyle.Text(title, 36, MenuStyle.Accent, MenuStyle.Bold));
        box.AddChild(row);
        MenuStyle.Place(box, 0, 0, 0, 0, 56, 18, 56, 18);
        _contentRoot.AddChild(box);
    }

    private void AddTopIcons(bool includeSettings, Action? reopen)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        if (includeSettings && reopen != null)
            row.AddChild(MenuStyle.IconButton("settings", 24, MenuStyle.TextDim, () => ShowSettings(reopen)));
        row.AddChild(MenuStyle.IconButton("power", 24, MenuStyle.TextDim, () => GetTree().Quit()));
        MenuStyle.Place(row, 1, 0, 1, 0, -(64 + 90), 24, -64, 24);
        _contentRoot.AddChild(row);
    }

    // -------------------------------------------------------------------------------------------
    // LAN: host / discover / join (behaviour unchanged from the previous menu)
    // -------------------------------------------------------------------------------------------

    private void StartHost(string name)
    {
        if (_leavingMenu) return;
        string gameName = string.IsNullOrWhiteSpace(name) ? DefaultGameName : name.Trim();
        MatchManager.Instance.PrepareHostMatch();
        var result = GetNode<LanSession>("/root/LanSession").StartHost(gameName);
        if (result != Error.Ok)
        {
            if (IsUsable(_status)) _status!.Text = $"Could not host on port {LanSession.GamePort}: {result}";
            return;
        }
        LeaveForGame();
    }

    private void StartBrowsing()
    {
        if (_terminalCleanupDone) return;
        StopLobbyBrowser();
        _games.Clear();
        _leavingMenu = false;
        _listener = new PacketPeerUdp();
        Error error = _listener.Bind(LanSession.DiscoveryPort, "0.0.0.0");
        if (error != Error.Ok)
        {
            if (IsUsable(_status)) _status!.Text = $"Discovery listener unavailable: {error}";
        }
        else _refreshTimer.Start();
    }

    private void PollDiscovery()
    {
        if (_leavingMenu || _listener == null || !IsUsable(_gamesList)) return;
        bool changed = false;
        while (_listener.GetAvailablePacketCount() > 0)
        {
            var parsed = Json.ParseString(Encoding.UTF8.GetString(_listener.GetPacket()));
            if (parsed.VariantType != Variant.Type.Dictionary) continue;
            var packet = parsed.AsGodotDictionary();
            string host = packet.GetValueOrDefault("host", "").AsString();
            string name = packet.GetValueOrDefault("name", DefaultGameName).AsString();
            int players = (int)packet.GetValueOrDefault("players", 1).AsInt64();
            if (!string.IsNullOrEmpty(host))
            {
                var advertised = (name, host, players, Time.GetTicksMsec());
                if (!_games.TryGetValue(host, out var prior) || prior.Name != name || prior.Players != players)
                    changed = true;
                _games[host] = advertised;
            }
        }
        foreach (string key in new List<string>(_games.Keys))
            if (Time.GetTicksMsec() - _games[key].Seen > 3000) { _games.Remove(key); changed = true; }
        if (changed) RefreshGames();
    }

    private void RefreshGames()
    {
        if (_leavingMenu || !IsUsable(_gamesList)) return;
        QueueFreeChildren(_gamesList);
        foreach (var game in _games.Values)
        {
            string host = game.Host;
            string detail = $"{host}  ·  {game.Players} player{(game.Players == 1 ? "" : "s")}";
            _gamesList!.AddChild(MenuStyle.Row(game.Name, detail, () => JoinGame(host)));
        }
        if (IsUsable(_status))
            _status!.Text = _games.Count == 0 ? "Listening for games…" : "Select a game to join.";
    }

    private void JoinGame(string host)
    {
        if (_leavingMenu) return;
        MatchManager.Instance.PrepareClientMatch();
        Error result = GetNode<LanSession>("/root/LanSession").Join(host);
        if (result != Error.Ok)
        {
            if (IsUsable(_status)) _status!.Text = $"Could not connect to {host}: {result}";
            return;
        }
        LeaveForGame();
    }

    private void LeaveForGame()
    {
        if (_sceneChangeQueued) return;
        _sceneChangeQueued = true;
        _leavingMenu = true;
        // This is the sole terminal cleanup path. It completes before the deferred scene change
        // begins to remove this Control and its Timer child.
        RunTerminalCleanup();
        LoadingScreen.Instance?.Show();
        SetProcess(false);
        CallDeferred(nameof(ChangeToGameWorld));
    }

    private void ChangeToGameWorld() => GetTree().ChangeSceneToFile("res://scenes/GameWorld.tscn");

    private void RunTerminalCleanup()
    {
        if (_terminalCleanupDone) return;
        _terminalCleanupDone = true;
        _leavingMenu = true;
        StopLobbyBrowser();
        // The signal is explicitly disconnected only during final teardown. Normal browser
        // close/reopen keeps the Timer reusable.
        if (IsUsable(_refreshTimer)) _refreshTimer.Timeout -= PollDiscovery;
        _games.Clear();
        // Sole terminal cleanup path (LeaveForGame and application shutdown both funnel through
        // here), so this is the one place the menu music needs to be stopped from.
        if (IsUsable(_menuMusic)) _menuMusic!.Stop();
    }

    private void StopLobbyBrowser()
    {
        if (IsUsable(_refreshTimer)) _refreshTimer.Stop();
        if (IsUsable(_listener)) _listener!.Close();
        _listener = null;
    }

    private static bool IsUsable(GodotObject? node) => node != null && GodotObject.IsInstanceValid(node) && !node.IsQueuedForDeletion();

    private void QueueFreeChildren(Node? node = null)
    {
        node ??= this;
        if (!IsUsable(node)) return;
        foreach (Node child in node.GetChildren())
            if (IsUsable(child)) child.QueueFree();
    }

    public override void _ExitTree()
    {
        // Usually already complete via LeaveForGame. This covers application shutdown or an
        // external scene change without ever attempting a second cleanup.
        RunTerminalCleanup();
    }
}
