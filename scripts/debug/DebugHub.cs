using Godot;
using System.Collections.Generic;

/// <summary>
/// One-key manager for every in-game debug tool. Autoload (see project.godot), debug builds only.
///
///   F2 in the game        -> the TOOL LIST opens (mouse freed, gameplay input paused).
///   Click a tool / 1-9    -> the list closes and that tool opens.
///   F2 inside a tool      -> that tool closes and you are back on the list.
///   F2 / Esc on the list  -> the list closes and you are back in the game.
///
/// Tools that also have a direct hotkey (IDebugTool.ShortcutKey - F3 grip, F6 sound, F8 animation)
/// still work as fast toggles from anywhere. Only one tool is ever open at a time, so tools no
/// longer stack on top of each other, and there is exactly one place that decides who owns the
/// mouse and the gameplay input.
///
/// Tools register themselves from their own _Ready() (see IDebugTool.cs), and only when they are
/// really armed: debug build AND the local player's copy. So the list always shows exactly what is
/// usable right now, and it is empty on the main menu.
/// </summary>
public partial class DebugHub : CanvasLayer
{
	public static DebugHub? Instance { get; private set; }

	/// <summary>True while the F2 list or any debug tool is on screen. The in-game pause menu checks
	/// this so Escape closing the hub's list does not also open the game menu. Always false in a
	/// release build, where the hub is disabled.</summary>
	public static bool IsOpen => Instance != null && Instance._enabled && Instance._state != HubState.Closed;

	private const Key HubKey = Key.F2;

	private static readonly Color Gold = new(1.0f, 0.82f, 0.30f);
	private static readonly Color Dim = new(1.0f, 1.0f, 1.0f, 0.62f);

	private enum HubState { Closed, Menu, Tool }

	private readonly List<IDebugTool> _tools = new();
	private HubState _state = HubState.Closed;
	private IDebugTool? _activeTool;
	private bool _enabled;

	// What the LIST itself paused while it is open (a tool pauses its own things separately - the
	// order of "list releases, then tool takes" in OpenRegistered() is what keeps the two from
	// saving each other's paused state as if it were the normal state).
	private bool _lockHeld;
	private CharacterBody3D? _lockedBody;
	private WeaponSwitcher? _lockedSwitcher;
	private bool _switcherWasProcessing = true;
	private bool _bodyWasProcessingUnhandledInput = true;
	private Input.MouseModeEnum _mouseBefore = Input.MouseModeEnum.Captured;

	private Control _menuRoot = null!;
	private VBoxContainer _listBox = null!;
	private PanelContainer _bar = null!;
	private Label _barLabel = null!;

	// -------------------------------------------------------------------------------------------
	// Static entry points used by the tools. All of them are safe when the hub is missing or
	// disabled (release build), falling back to driving the tool directly.
	// -------------------------------------------------------------------------------------------

	public static void Register(IDebugTool tool) => Instance?.RegisterTool(tool);

	public static void Unregister(IDebugTool tool) => Instance?.UnregisterTool(tool);

	/// <summary>Opens a tool the proper way (closing whatever else was open first). For tools that
	/// have a second way in, like the grip tuner's F7 entry panel.</summary>
	public static void Open(IDebugTool tool)
	{
		if (Instance != null && Instance._enabled) Instance.OpenRegistered(tool);
		else tool.OpenTool();
	}

	/// <summary>The "Back to tools" buttons inside the tools call this.</summary>
	public static void Back(IDebugTool tool)
	{
		if (Instance != null && Instance._enabled && Instance._activeTool == tool) Instance.EndActiveTool(returnToMenu: true);
		else tool.CloseTool();
	}

	// -------------------------------------------------------------------------------------------
	// Lifecycle
	// -------------------------------------------------------------------------------------------

	public override void _Ready()
	{
		Instance = this;
		Layer = 120;
		ProcessMode = ProcessModeEnum.Always;
		_enabled = OS.IsDebugBuild();
		if (!_enabled)
		{
			// Release export: no UI, no input, no per-frame work. The tools are inert there too.
			SetProcess(false);
			SetProcessInput(false);
			return;
		}
		BuildUi();
	}

	public override void _ExitTree()
	{
		if (Instance == this) Instance = null;
	}

	public override void _Input(InputEvent @event)
	{
		if (!_enabled) return;
		if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;
		Key code = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;

		if (code == HubKey)
		{
			ToggleHub();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (_state == HubState.Menu)
		{
			if (code == Key.Escape)
			{
				CloseMenu();
				GetViewport().SetInputAsHandled();
				return;
			}
			int index = DigitIndex(code);
			if (index >= 0 && index < _tools.Count)
			{
				OpenRegistered(_tools[index]);
				GetViewport().SetInputAsHandled();
				return;
			}
		}

		// Direct hotkeys (F3 / F6 / F8 ...).
		foreach (IDebugTool tool in _tools)
		{
			if (tool.ShortcutKey == Key.None || tool.ShortcutKey != code) continue;
			if (_state == HubState.Tool && _activeTool == tool) EndActiveTool(returnToMenu: false);
			else OpenRegistered(tool);
			GetViewport().SetInputAsHandled();
			return;
		}
	}

	public override void _Process(double delta)
	{
		// A tool closed itself (its own Back button, or it was torn down): follow it back to the
		// list instead of leaving the hub thinking a tool is still open.
		if (_state == HubState.Tool && (_activeTool == null || !_activeTool.IsToolOpen))
			EndActiveTool(returnToMenu: true);
	}

	private static int DigitIndex(Key code)
		=> code >= Key.Key1 && code <= Key.Key9 ? (int)(code - Key.Key1) : -1;

	// -------------------------------------------------------------------------------------------
	// Registration
	// -------------------------------------------------------------------------------------------

	private void RegisterTool(IDebugTool tool)
	{
		if (!_enabled || _tools.Contains(tool)) return;
		_tools.Add(tool);
		_tools.Sort((a, b) => string.CompareOrdinal(a.ToolName, b.ToolName));
		if (_state == HubState.Menu) RefreshList();
	}

	private void UnregisterTool(IDebugTool tool)
	{
		if (!_enabled || !_tools.Remove(tool)) return;
		if (_activeTool == tool)
		{
			// The tool is leaving the tree (match ended, player removed) - it cleans up after
			// itself, so just forget it.
			_activeTool = null;
			_bar.Visible = false;
			if (_state == HubState.Tool) _state = HubState.Closed;
		}
		if (_state == HubState.Menu) RefreshList();
	}

	// -------------------------------------------------------------------------------------------
	// State changes. Every path goes through these so the pause/resume of the mouse and the
	// gameplay input always happens in the same order: whoever had it lets go BEFORE the next
	// owner takes it.
	// -------------------------------------------------------------------------------------------

	private void ToggleHub()
	{
		switch (_state)
		{
			case HubState.Closed: ShowMenu(); break;
			case HubState.Menu: CloseMenu(); break;
			default: EndActiveTool(returnToMenu: true); break;
		}
	}

	private void ShowMenu()
	{
		RefreshList();
		AcquireLock();
		_bar.Visible = false;
		_menuRoot.Visible = true;
		_state = HubState.Menu;
	}

	private void CloseMenu()
	{
		_menuRoot.Visible = false;
		ReleaseLock();
		_state = HubState.Closed;
	}

	private void OpenRegistered(IDebugTool tool)
	{
		if (!_tools.Contains(tool))
		{
			tool.OpenTool();
			return;
		}
		if (_state == HubState.Tool && _activeTool == tool && tool.IsToolOpen) return;

		if (_state == HubState.Menu)
		{
			_menuRoot.Visible = false;
			ReleaseLock();
		}
		else if (_state == HubState.Tool && _activeTool != null && _activeTool.IsToolOpen)
		{
			_activeTool.CloseTool();
		}

		_activeTool = tool;
		_state = HubState.Tool;
		tool.OpenTool();

		if (!tool.IsToolOpen)
		{
			GD.PushWarning($"DebugHub: '{tool.ToolName}' did not open.");
			_activeTool = null;
			ShowMenu();
			return;
		}
		_barLabel.Text = $"DEBUG  -  {tool.ToolName}";
		_bar.Visible = true;
	}

	private void EndActiveTool(bool returnToMenu)
	{
		IDebugTool? tool = _activeTool;
		_activeTool = null;
		_bar.Visible = false;
		_state = HubState.Closed;
		if (tool != null && tool.IsToolOpen) tool.CloseTool();
		if (returnToMenu) ShowMenu();
	}

	// -------------------------------------------------------------------------------------------
	// Pausing the game while the LIST is open (same three things the tools' own panels pause:
	// weapon switch / fire polling, PlayerMovement's mouse-look, and the mouse mode itself).
	// -------------------------------------------------------------------------------------------

	private CharacterBody3D? FindLocalPlayer()
	{
		foreach (Node node in GetTree().GetNodesInGroup("players"))
			if (node is CharacterBody3D body && body.IsMultiplayerAuthority()) return body;
		return null;
	}

	private void AcquireLock()
	{
		if (_lockHeld) return;
		CharacterBody3D? body = FindLocalPlayer();
		if (body == null) return; // main menu etc.: nothing to pause, the cursor is already free

		_lockedBody = body;
		_lockedSwitcher = body.GetNodeOrNull<WeaponSwitcher>("WeaponSwitcher");
		if (_lockedSwitcher != null)
		{
			_switcherWasProcessing = _lockedSwitcher.IsProcessing();
			_lockedSwitcher.SetProcess(false);
		}
		_bodyWasProcessingUnhandledInput = body.IsProcessingUnhandledInput();
		body.SetProcessUnhandledInput(false);
		_mouseBefore = Input.MouseMode;
		Input.MouseMode = Input.MouseModeEnum.Visible;
		_lockHeld = true;
	}

	private void ReleaseLock()
	{
		if (!_lockHeld) return;
		_lockHeld = false;

		bool bodyAlive = _lockedBody != null && IsInstanceValid(_lockedBody);
		if (_lockedSwitcher != null && IsInstanceValid(_lockedSwitcher))
			_lockedSwitcher.SetProcess(_switcherWasProcessing);
		if (bodyAlive)
		{
			_lockedBody!.SetProcessUnhandledInput(_bodyWasProcessingUnhandledInput);
			Input.MouseMode = _mouseBefore;
		}
		_lockedBody = null;
		_lockedSwitcher = null;
	}

	// -------------------------------------------------------------------------------------------
	// UI. Built from code, like every other debug tool here - no .tscn to go stale.
	// -------------------------------------------------------------------------------------------

	private void BuildUi()
	{
		// ---- the tool list ----
		_menuRoot = new Control { Name = "DebugHubMenu", Visible = false };
		_menuRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_menuRoot.MouseFilter = Control.MouseFilterEnum.Stop;
		AddChild(_menuRoot);

		var dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = Control.MouseFilterEnum.Ignore };
		dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_menuRoot.AddChild(dim);

		var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
		center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		_menuRoot.AddChild(center);

		var panel = new PanelContainer { Name = "DebugHubPanel" };
		panel.AddThemeStyleboxOverride("panel", MakeBox(new Color(0.06f, 0.07f, 0.09f, 0.97f), Gold, 2, 8));
		center.AddChild(panel);

		var margin = new MarginContainer();
		foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
			margin.AddThemeConstantOverride(side, 20);
		panel.AddChild(margin);

		var column = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
		column.AddThemeConstantOverride("separation", 8);
		margin.AddChild(column);

		var title = new Label { Text = "DEBUG TOOLS", Modulate = Gold };
		title.AddThemeFontSizeOverride("font_size", 24);
		column.AddChild(title);

		var subtitle = new Label
		{
			Text = "Pick a tool (click it or press its number). Press F2 inside a tool to come back here.",
			Modulate = Dim,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		column.AddChild(subtitle);
		column.AddChild(new HSeparator());

		_listBox = new VBoxContainer();
		_listBox.AddThemeConstantOverride("separation", 8);
		column.AddChild(_listBox);

		column.AddChild(new HSeparator());
		var footer = new HBoxContainer();
		footer.AddThemeConstantOverride("separation", 10);
		column.AddChild(footer);
		footer.AddChild(MakeButton("Close (F2 / Esc)", CloseMenu));
		footer.AddChild(new Label { Text = "Direct hotkeys still work from the game.", Modulate = Dim });

		// ---- the strip shown while a tool is open ----
		_bar = new PanelContainer { Name = "DebugHubBar", Visible = false };
		_bar.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
		_bar.GrowHorizontal = Control.GrowDirection.Both;
		_bar.GrowVertical = Control.GrowDirection.Begin;
		_bar.OffsetTop = -16.0f;
		_bar.OffsetBottom = -16.0f;
		_bar.AddThemeStyleboxOverride("panel", MakeBox(new Color(0.06f, 0.07f, 0.09f, 0.92f), Gold, 1, 6));
		AddChild(_bar);

		var barMargin = new MarginContainer();
		foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
			barMargin.AddThemeConstantOverride(side, 8);
		_bar.AddChild(barMargin);

		var barRow = new HBoxContainer();
		barRow.AddThemeConstantOverride("separation", 12);
		barMargin.AddChild(barRow);

		_barLabel = new Label { Modulate = Gold };
		barRow.AddChild(_barLabel);
		barRow.AddChild(MakeButton("Back to tools (F2)", () => EndActiveTool(returnToMenu: true)));
	}

	private void RefreshList()
	{
		foreach (Node child in _listBox.GetChildren())
		{
			_listBox.RemoveChild(child);
			child.QueueFree();
		}

		if (_tools.Count == 0)
		{
			_listBox.AddChild(new Label
			{
				Text = "No debug tools are loaded here.\nThey live on the player - start or join a match and press F2 again.",
				Modulate = Dim,
			});
			return;
		}

		for (int i = 0; i < _tools.Count; i++)
		{
			IDebugTool tool = _tools[i];
			string number = i < 9 ? (i + 1).ToString() : "-";
			string hotkey = tool.ShortcutKey != Key.None ? $"    [{tool.ShortcutKey}]" : string.Empty;
			var button = MakeButton($"{number}    {tool.ToolName}{hotkey}\n       {tool.ToolDescription}",
				() => OpenRegistered(tool));
			button.Alignment = HorizontalAlignment.Left;
			button.CustomMinimumSize = new Vector2(0, 54);
			_listBox.AddChild(button);
		}
	}

	private static StyleBoxFlat MakeBox(Color fill, Color border, int borderWidth, int radius)
	{
		var box = new StyleBoxFlat { BgColor = fill, BorderColor = border };
		box.SetBorderWidthAll(borderWidth);
		box.SetCornerRadiusAll(radius);
		return box;
	}

	private static Button MakeButton(string text, System.Action onPressed)
	{
		// No keyboard focus: otherwise Space or Enter would press the button again after a click.
		var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
		button.Pressed += onPressed;
		return button;
	}
}
