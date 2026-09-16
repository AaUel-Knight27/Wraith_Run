using Godot;
using System;
using System.Collections.Generic;
using System.Text;

public partial class LanMenu : Control
{
    private readonly Dictionary<string, (string Name, string Host, int Players, ulong Seen)> _games = new();
    private PacketPeerUdp? _listener;
    private Timer _refreshTimer = null!;
    private Control _contentRoot = null!;
    private VBoxContainer _gamesList = null!;
    private Label _status = null!;
    private bool _leavingMenu;
    private bool _terminalCleanupDone;
    private bool _sceneChangeQueued;

    public override void _Ready()
    {
        _refreshTimer = new Timer { WaitTime = 0.5, OneShot = false };
        _refreshTimer.Timeout += PollDiscovery;
        AddChild(_refreshTimer);
        _contentRoot = new Control();
        _contentRoot.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_contentRoot);
        BuildMainMenu();
    }

    private void BuildMainMenu()
    {
        if (_terminalCleanupDone) return;
        StopLobbyBrowser();
        _leavingMenu = false;
        QueueFreeChildren(_contentRoot);
        var box = CenteredBox();
        AddTitle(box, "WRAITH RUN");
        AddButton(box, "Host LAN game", ShowHostPrompt);
        AddButton(box, "Join LAN game", ShowBrowser);
        _status = AddLabel(box, "Choose Host to start a match, or Join to discover nearby matches.");
    }

    private void ShowHostPrompt()
    {
        QueueFreeChildren(_contentRoot);
        var box = CenteredBox();
        AddTitle(box, "Host a LAN game");
        var name = new LineEdit { PlaceholderText = "Game name", Text = "Wraith Run LAN", MaxLength = 32 };
        box.AddChild(name);
        AddButton(box, "Start hosting", () => StartHost(name.Text));
        AddButton(box, "Back", BuildMainMenu);
        name.GrabFocus();
    }

    private void StartHost(string name)
    {
        var result = GetNode<LanSession>("/root/LanSession").StartHost(name);
        if (result != Error.Ok) { BuildMainMenu(); _status.Text = $"Could not host on port {LanSession.GamePort}: {result}"; return; }
        LeaveForGame();
    }

    private void ShowBrowser()
    {
        if (_terminalCleanupDone) return;
        StopLobbyBrowser();
        _leavingMenu = false;
        QueueFreeChildren(_contentRoot);
        var box = CenteredBox();
        AddTitle(box, "LAN games");
        _status = AddLabel(box, "Listening for games…");
        _gamesList = new VBoxContainer(); box.AddChild(_gamesList);
        AddButton(box, "Back", BuildMainMenu);
        _listener = new PacketPeerUdp();
        Error error = _listener.Bind(LanSession.DiscoveryPort, "0.0.0.0");
        if (error != Error.Ok) _status.Text = $"Discovery listener unavailable: {error}";
        else _refreshTimer.Start();
    }

    private void PollDiscovery()
    {
        if (_leavingMenu || _listener == null || !GodotObject.IsInstanceValid(_gamesList)) return;
        bool changed = false;
        while (_listener.GetAvailablePacketCount() > 0)
        {
            var parsed = Json.ParseString(Encoding.UTF8.GetString(_listener.GetPacket()));
            if (parsed.VariantType != Variant.Type.Dictionary) continue;
            var packet = parsed.AsGodotDictionary();
            string host = packet.GetValueOrDefault("host", "").AsString();
            string name = packet.GetValueOrDefault("name", "Wraith Run LAN").AsString();
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
        if (_leavingMenu || _gamesList == null || !GodotObject.IsInstanceValid(_gamesList) || _gamesList.IsQueuedForDeletion()) return;
        QueueFreeChildren(_gamesList);
        foreach (var game in _games.Values)
            AddButton(_gamesList, $"{game.Name}  —  {game.Host}  ({game.Players} player{(game.Players == 1 ? "" : "s")})", () => JoinGame(game.Host));
        if (_status != null && GodotObject.IsInstanceValid(_status) && !_status.IsQueuedForDeletion())
            _status.Text = _games.Count == 0 ? "Listening for games…" : "Select a game to join.";
    }

    private void JoinGame(string host)
    {
        Error result = GetNode<LanSession>("/root/LanSession").Join(host);
        if (result != Error.Ok) { _status.Text = $"Could not connect to {host}: {result}"; return; }
        LeaveForGame();
    }

    private VBoxContainer CenteredBox()
    {
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        _contentRoot.AddChild(center);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        box.AddThemeConstantOverride("separation", 14);
        center.AddChild(box); return box;
    }
    private static void AddTitle(Container box, string text) { var label = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center }; label.AddThemeFontSizeOverride("font_size", 30); box.AddChild(label); }
    private static Label AddLabel(Container box, string text) { var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, HorizontalAlignment = HorizontalAlignment.Center }; box.AddChild(label); return label; }
    private static void AddButton(Container box, string text, Action action) { var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 44) }; button.Pressed += action; box.AddChild(button); }
    private void LeaveForGame()
    {
        if (_sceneChangeQueued) return;
        _sceneChangeQueued = true;
        _leavingMenu = true;
        // This is the sole terminal cleanup path. It completes before the deferred scene change
        // begins to remove this Control and its Timer child.
        RunTerminalCleanup();
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
    }

    private void StopLobbyBrowser()
    {
        if (IsUsable(_refreshTimer)) _refreshTimer.Stop();
        if (IsUsable(_listener)) _listener.Close();
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
