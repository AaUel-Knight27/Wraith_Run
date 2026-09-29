using Godot;

/// <summary>
/// Implemented by every in-game debug tool so the F2 hub (DebugHub.cs) can list it, open it and
/// close it. To add a new tool: implement this interface on the tool's node, call
/// DebugHub.Register(this) once the tool is fully built (only if it is actually armed - i.e. a
/// debug build and the local player), and DebugHub.Unregister(this) from _ExitTree(). That is the
/// whole integration; the hub never needs editing. See README_DEBUG_HUB.md.
/// </summary>
public interface IDebugTool
{
	/// <summary>Name shown in the F2 list. The list is sorted by this, so it also fixes the 1-9
	/// number shown next to the tool.</summary>
	string ToolName { get; }

	/// <summary>One short line under the name in the list: what the tool is for.</summary>
	string ToolDescription { get; }

	/// <summary>Optional direct hotkey that opens/closes this tool without going through the
	/// list. Key.None for "list only".</summary>
	Key ShortcutKey { get; }

	/// <summary>True while the tool's panel/overlay is showing.</summary>
	bool IsToolOpen { get; }

	/// <summary>Shows the tool. Must be safe to call when it is already open.</summary>
	void OpenTool();

	/// <summary>Hides the tool and gives the player their mouse/keyboard back. Must be safe to
	/// call when it is already closed.</summary>
	void CloseTool();
}
