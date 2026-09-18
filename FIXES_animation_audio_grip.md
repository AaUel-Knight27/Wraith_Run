# Fourth pass: the stepping glitch, your uploaded sounds, and the gun-fit tuner

Extract over the project root. Let Godot reimport on open.

Touched: `scripts/player/PlayerAnimationController.cs`, `scripts/player/WeaponAttachment.cs`,
`scripts/player/WeaponSwitcher.cs`, `scripts/data/WeaponRegistry.tres`, `scenes/Player.tscn`,
`audio/SOUND_SLOTS.md`.
New: `scripts/debug/WeaponGripTuner.cs`, `GRIP_TUNING.md`, nine audio files under `audio/Clips/`.

---

## 1. The stepping glitch

Confirmed by measurement, not guesswork: I extracted the Hips bone's translation curve from
`locomotion_walk_fwd`, `locomotion_sprint_fwd`, and `locomotion_crouch_walk` directly from the
glb. Net displacement over a full cycle is ~0 in every case - these are authored as in-place leg
cycles, which is correct for this movement system (the physics code moves the character, the
animation just has to keep cycling underneath it). Their durations are 0.5-2.1 seconds.

None of their Blender names end in `_loop`. Godot's glTF importer only turns looping on for a
take whose name ends in that suffix - it's how `combat_jump_loop` became `combat_jump` with
looping already on, in the earlier pass. Every one of these clips imported as `LOOP_NONE`.

The state machine only calls `Travel()` when the *state* changes. While you keep walking, the
state stays `Walk` the whole time, so nothing tells the clip to restart. It plays its one cycle
and then holds on the last frame while you keep moving - then jumps and restarts the next time
you re-enter the state (stop and start walking again, or transition through another state and
back). Repeated every time, that's exactly "glitchy and unstable": a stutter-freeze-jump instead
of a clean loop.

Fixed by forcing `LoopMode = Linear` on every clip the locomotion state machine references,
overriding whatever the importer decided, right after the clip library is built. Nothing else
changed - the one-shot fire/reload/death clips are untouched and still play once, correctly.

## 2. Your uploaded sounds

All nine files are in and wired. Full mapping is in `audio/SOUND_SLOTS.md` - the short version:
AK gets its own fire and reload recording, all four rifle-class weapons (AK/M4/P90/Kriss) share
your dry-fire click, both pistols share your pistol shot/reload, the shotgun gets your fire and
its new per-shell reload clip (it measures 0.506s, matching the existing 0.5s-per-shell design
almost exactly), and the Karambit - which had no fire sound at all before this - now has your
swing.

One I added on my own initiative: `flesh_hit.mp3` wasn't a clean fit for any of the existing
per-weapon slots, because it isn't a per-weapon sound - it's what a hit sounds like regardless of
what dealt it. I wired it as a shared "hit landed on a person" cue, broadcast so everyone nearby
hears it, not just the shooter (the same gap fire sounds had before the previous pass - the
hitscan runs only on the shooter's own device, so anything triggered from inside it needs an
explicit broadcast or nobody else ever hears it). If that's not what you had in mind for that
file, it's a one-line change in `WeaponAttachment.cs`'s `FleshImpactSound` constant.

Two things worth flagging about the files themselves - not a request to fix anything, just what
I noticed while wiring them: `AK_fire.wav` is 2.5s, likely a real recording with its natural echo
tail; at 600 RPM that retriggers before the tail finishes, which is normal and is what makes full
auto sound continuous. And `reload_click_rifle.wav` is 1.14s, longer than a bare click - I used it
as the dry-fire sound anyway since that's the closest fit, but if you recorded it for something
more specific, say so and I'll move it.

## 3. Gun fit: the live tuner

I can't see your screen, so I can't visually verify a grip offset the way you can. Rather than
keep guessing at numbers you'd have to test through an editor-reimport cycle, **F3** in-game now
opens a live calibrator: nudge the equipped weapon's position/rotation/scale in real time, see it
move immediately, then **F4** prints the exact numbers ready to paste into
`scripts/data/WeaponRegistry.tres`. Full instructions, including which axis is which and why the
numbers look like they do, are in `GRIP_TUNING.md`.

This is a dev-only tool - it touches nothing but the local view, sends nothing over the network,
and has zero effect on hitscan or any other peer. It has no effect on a weapon with no model
(Kriss Vector, currently).

I didn't further hand-tune the existing registry numbers beyond what the last pass already
computed from geometry - spending more effort guessing without visual feedback has a low ceiling
compared to five minutes with the tuner actually looking at it. The measured starting values are
still there as your starting point.

## What I verified, what I couldn't

Verified: every `res://` audio path in the registry resolves to a file that exists (checked
programmatically against the filesystem); every uploaded file opens as valid WAV/MP3; scene and
resource references (`SubResource`/`ExtResource` ids, node parents, `load_steps` counts) are all
consistent; brace balance across every touched C# file; the Hips-bone measurement behind the loop
fix is from the actual glb data, not assumed.

Not verified, same as last time: no .NET SDK in this container, so none of this was compiled. The
tuner is new surface area - `Input.IsPhysicalKeyPressed` with the `Key` enum and the
`AnimationLibrary`/`Animation.LoopMode` calls are the parts most worth a close look if the build
fails.
