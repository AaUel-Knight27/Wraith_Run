# Build fix — one-line bug

Extract over your project root (just replaces PlayerAnimationController.cs).

## Root cause
`PlayerAnimationController.cs` had `var player = GetParent<Node>();` on line 51. Godot's C#
bindings only expose a generic helper for `GetNode<T>()` — `GetParent()` is plain and
non-generic, so `GetParent<Node>()` doesn't compile ("cannot be used with type arguments").

C# builds the whole project as a single assembly, so one file failing to compile takes down
every class in the project — which is exactly why your error log showed totally unrelated
files (`LanSession.cs`, `LanMenu.cs`) as broken. Nothing was actually wrong with either of
those; there was simply no valid build for anything to run.

## Fix
Changed to `var player = GetParent();` — `GetParent()` already returns `Node`, which is all
`FindSkeleton()` and `GetPathTo()` need.

## How to confirm this was actually it
After extracting, do a full clean rebuild rather than just hitting Play again (a half-stale
build can mask whether the fix landed):
1. Close Godot.
2. Delete `.godot/mono`, and `bin/`/`obj/` next to your `.csproj`.
3. Reopen the project and watch the bottom-right build indicator — it should go from spinner to
   a plain checkmark with no red X.
4. If it builds clean, the LanSession/LanMenu errors should be gone entirely, since that code
   was never actually broken.
