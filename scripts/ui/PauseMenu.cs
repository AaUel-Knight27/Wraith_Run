using Godot;

/// <summary>
/// In-game menu key handling: Esc (keyboard), Start (gamepad), Android's Back button/gesture, or the MENU
/// button TouchControls draws. It opens the SAME tablet as the hub (HubTablet, in its in-game mode), so
/// the menu looks and behaves the same everywhere: RESUME / LEAVE on the home page, OPERATOR, SETTINGS.
/// This class only decides when it opens and closes; the tablet itself lives in HubTablet.
///
/// It does NOT pause the game. This is a LAN multiplayer shooter: the other players keep playing, the
/// clock keeps running and you can still be shot while you read the tablet - the same as every online
/// shooter's menu. While it is open YOUR gameplay input is stopped (GameUiState) and the mouse is
/// released; closing hands both back.
///
/// Added by PlayerHud as a child of the local player's HUD - never for remote players' copies of the
/// player. In the hub the hub's own tablet is the menu, so this stays out of the way there.
/// </summary>
public partial class PauseMenu : CanvasLayer
{
    /// <summary>Set by PlayerHud before the node enters the tree.</summary>
    public CharacterBody3D Player { get; set; } = null!;

    private HubTablet? _tablet;
    private bool _inHub;
    private bool _connectionLost;

    public bool IsOpen => _inHub ? HubTablet.Instance?.IsOpen ?? false : _tablet?.IsOpen ?? false;

    public override void _Ready()
    {
        Layer = 80;
        GameUiState.EnsureInputActions();
        // The hub has its own tablet (HubTablet), built by the hub. Building another here would put two
        // tablets in the same room.
        if (HubState.Active)
        {
            _inHub = true;
            SetProcess(false);
            SetProcessInput(false);
            return;
        }
        SetProcess(false);
        _tablet = new HubTablet { Name = "GameTablet", GamePlayer = Player };
        AddChild(_tablet);
        Multiplayer.ServerDisconnected += OnServerDisconnected;
    }

    public override void _ExitTree()
    {
        if (_inHub) return;
        if (Multiplayer != null) Multiplayer.ServerDisconnected -= OnServerDisconnected;
    }

    public void Toggle()
    {
        // In the hub the tablet is the menu (HubTablet); this one stays out of the way.
        if (_inHub) { HubTablet.Instance?.Toggle(); return; }
        if (_connectionLost) return;
        _tablet?.Toggle();
    }

    public override void _Input(InputEvent @event)
    {
        if (_inHub || _tablet == null || _connectionLost) return;

        // A key row in SETTINGS > CONTROLS is waiting for a key: Escape then means "cancel the rebind".
        if (_tablet.IsOpen && _tablet.IsCapturingKey) return;

        bool pausePressed = @event.IsActionPressed(GameUiState.PauseAction);
        bool backPressed = _tablet.IsOpen && @event.IsActionPressed("ui_cancel");
        if (!pausePressed && !backPressed) return;

        // The match-results screen owns the whole screen; the menu must not open under it.
        if (GameUiState.ResultsOpen) return;

        GetViewport().SetInputAsHandled();
        // With the "leave this match?" question showing, Escape / Back answers NO rather than closing everything.
        if (_tablet.IsOpen && _tablet.CancelLeaveQuestion()) return;
        Toggle();
    }

    public override void _Notification(int what)
    {
        // Android Back button / gesture. Needs application/config/quit_on_go_back=false in
        // project.godot, otherwise the engine quits the app before a script ever sees it.
        // (The hub's tablet handles this itself.)
        if (what != (int)NotificationWMGoBackRequest || _inHub || _tablet == null || GameUiState.ResultsOpen) return;
        if (_tablet.IsOpen && _tablet.CancelLeaveQuestion()) return;
        Toggle();
    }

    private void OnServerDisconnected()
    {
        // The host closed the match (or the Wi-Fi dropped). There is nothing left to play, so lock the
        // tablet open on a single way out instead of letting the player wander a frozen arena.
        if (_connectionLost || _tablet == null) return;
        // After the final whistle the host going back to its menu is normal; the results screen has
        // its own way out, so do not bury it under a CONNECTION LOST page.
        if (MatchManager.Instance != null && MatchManager.Instance.IsMatchEnded) return;
        _connectionLost = true;
        _tablet.ShowConnectionLost();
    }
}