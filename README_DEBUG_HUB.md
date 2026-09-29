# F2 Debug Hub

One key for every debug tool. Debug builds only (the hub and all tools are inert in release exports).

## How to use it

| You press | What happens |
|---|---|
| **F2** in the game | The **tool list** opens. Mouse is freed, gameplay input is paused. |
| Click a tool, or press **1-9** | The list closes and that tool opens. |
| **F2** inside a tool (or its *Back to tools* button) | The tool closes and you are back on the list. |
| **F2** or **Esc** on the list | The list closes and you are back in the game. |
| **F3 / F6 / F8** | Direct fast-toggle for Grip / Sound / Animation tuner, from anywhere (as before). |

While a tool is open, a small strip at the bottom-centre of the screen shows which one it is,
with a *Back to tools (F2)* button. Only one tool is open at a time, so panels no longer stack.

The list currently shows (alphabetical, so the numbers are stable):

1. Animation Tuner `[F8]`
2. Character Rig Editor
3. Weapon Grip Tuner `[F3]`
4. Weapon Sound Tuner `[F6]`

The Character Rig Editor used to be on F2. F2 now belongs to the hub, so the rig editor is opened
from the list (press **F2**, then **2**).

Keys inside the tools are unchanged: grip tuner F4 / F5 / F7 and nudge keys, sound tuner F1 / F9.
F7 still opens the grip tuner's number-entry panel from cold.

## Files

New
- `scripts/debug/DebugHub.cs` - the autoload: list, bottom strip, F2 / Esc / 1-9 / shortcut handling.
- `scripts/debug/IDebugTool.cs` - the small interface every tool implements.

Changed
- `project.godot` - one line: `DebugHub` added to `[autoload]`.
- `scenes/Player.tscn` - **adds the `WeaponSoundTuner` node**. It was missing from the scene, so F6
  never did anything before.
- `scripts/debug/CharacterRigTuner.cs`, `WeaponGripTuner.cs`, `WeaponSoundTuner.cs`,
  `AnimationTuner.cs` - each implements `IDebugTool` and registers with the hub. Their own
  toggle-key polling (F2 / F3 / F6 / F8) was removed; the hub owns those keys now.

Two small fixes made on the way:
- `AnimationTuner` now only arms for the **local** player, as its header comment always said. Before, every
  remote player's copy also listened for F8 and stacked a second panel.
- `AnimationTuner` now frees the mouse when it opens and gives it back when it closes. Before, you had to
  press Esc after F8 before the sliders worked, and clicking the world re-captured the mouse while the panel
  was still open.

## Adding the next tool

1. Make the tool's node implement `IDebugTool` (`ToolName`, `ToolDescription`, `ShortcutKey`,
   `IsToolOpen`, `OpenTool()`, `CloseTool()`).
2. Call `DebugHub.Register(this);` at the end of `_Ready()` - only when the tool is really armed
   (debug build + local player).
3. Add `public override void _ExitTree() => DebugHub.Unregister(this);`
4. Don't poll your own open key. Return a `ShortcutKey` if you want a direct hotkey, or `Key.None`
   to be list-only. Add a *Back* button that calls `DebugHub.Back(this)`.

The hub never needs editing, and the list picks the new tool up automatically.

## Quick test checklist (about 3 minutes)

1. Host a game, spawn in. Press **F2**: list shows four tools, cursor visible, you cannot look or shoot.
2. Press **F2** again: back in the game, mouse captured, look/shoot/1-8 weapon keys work.
3. **F2**, click *Weapon Sound Tuner*: panel opens (top right), bottom strip shows its name. Drag a slider,
   *Test fire*. Press **F2**: list again. Click *Weapon Grip Tuner*: nudge with arrows while looking around.
   Press **F7**: number-entry panel opens. Press **F2**: list.
4. **F2**, press **1** (Animation Tuner): sliders work immediately; walk with WASD, switch weapons
   with 1-8. **F2** back, then **Esc** to leave the list. Mouse look works again.
5. From inside any tool press **F3**, **F6** or **F8**: it switches straight to that tool (or closes it if it
   was already the open one).
6. Main menu: **F2** shows the list with "No debug tools are loaded here".
