# Fuzzy bone matching + real diagnostics

Extract over your project root (replaces 2 files). The build itself is fine now — these are
the two runtime issues from your latest log.

## 1. "could not find a Skeleton3D with a 'mixamorig:RightHand' bone" — fixed
Godot's glTF importer sanitizes bone names on import — this is documented engine behavior, and
it does NOT necessarily keep "mixamorig:RightHand" as the literal bone name after import (exactly
how it mangles it isn't something I can verify without running the importer myself, which I
don't have access to here). `WeaponAttachment.cs` now searches for any bone whose name *contains*
"righthand" (case-insensitive) instead of requiring an exact match, so it survives whatever the
real sanitized name turns out to be. If it still can't find one, the error now prints every
actual bone name on the skeleton, so we'd see the real name directly instead of guessing again.

## 2. "Animation clip 'combat_jump_loop' ... was not found" — instrumented, not yet fixed
This one I genuinely can't diagnose further without seeing real output — I pulled
"combat_jump_loop" directly from your file's raw data and it's an exact match to what's in
there, so something in Godot's own import is renaming it (or another clip) in a way I can't
predict blind. Rather than guess a third name, `PlayerAnimationController.cs` now prints every
animation name actually present in the library the moment this lookup fails. Run it again and
send me that line — it'll say "Animations actually present: locomotion_..., combat_..., ..." —
and I'll fix the dictionary entry from real data instead of another guess.

I also changed the transition-wiring loop to only connect states that actually loaded, so a
missing clip no longer cascades into a wall of unrelated `_can_connect`/`No such node` errors
downstream — you should see far fewer error lines even before this is fully resolved.

## Not related to your code
"NO GRAB" (X11 mouse capture) is very likely a harmless Linux/X11 warning from switching window
focus while two instances are open, not something to fix in script — worth ignoring unless
input is actually broken. If it is, dropping the X11 vs Wayland/mouse-mode detail from the log
would help track it down as a separate issue.
