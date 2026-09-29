# Character Rig Editor — integration guide

Three new files, two modified files. Drop the new ones in, replace the modified ones (or apply the
small diffs by hand if you've changed those files since your upload).

## Files

**New:**
- `scripts/player/CharacterRigTuning.cs` — the permanent data store (bone/camera/scale overrides,
  save/load, print-to-source). Not debug-only — this is what ships.
- `scripts/player/CharacterRigApplier.cs` — the two runtime consumers that actually pose the
  skeleton and camera every frame, for every player. Not debug-only either.
- `scripts/debug/CharacterRigTuner.cs` — the live editor UI (F2). Debug-only, inert in release
  exports, same rule as `WeaponGripTuner`/`WeaponSoundTuner`/`AnimationTuner`.

**Modified (replace your copies with these, or re-apply by hand):**
- `scripts/player/PlayerAnimationController.cs` — added `IsFirePlaying` (mirrors
  `IsReloadPlaying`) and wired the two new applier classes into `_Ready()`, right after
  `LeftHandIk.Setup(...)`. If you've touched this file since your upload, the diff is exactly:
  1. New property `IsFirePlaying` next to `IsReloadPlaying`.
  2. `CharacterRigTuning.EnsureLoaded();` added next to the existing
     `AnimationTuning.EnsureLoaded();` line.
  3. Eight new lines after `_handIk.Setup(skeleton, _body, this);` that construct
     `CharacterRigOverrideModifier` and `CharacterRigCameraScaleApplier` and call their `Setup()`.
- `scenes/Player.tscn` — added an `ext_resource` for `CharacterRigTuner.cs` and a
  `CharacterRigTuner` node under the Player root, as a sibling of `WeaponGripTuner`. This has to be
  a real scene node (not spawned in code like `AnimationTuner`) because it sends RPCs, which need a
  stable node path that exists on every peer's copy before the first RPC arrives — same reason
  `WeaponGripTuner`/`WeaponSoundTuner` are scene nodes and `AnimationTuner` isn't.

  If you've since edited `Player.tscn` yourself, just add these two things by hand instead of
  overwriting the file: an `ext_resource` line for `res://scripts/debug/CharacterRigTuner.cs`, and
  a `Node` child of the Player root with that script attached (I gave it no `unique_id` since I
  can't generate one outside the editor — Godot will assign one the next time you save the scene).

No other files need to change. Everything is additive and does nothing until you actually tune
something — every lookup falls through to "no change from today's behaviour" until then.

## Using it

Run the game (debug build), press **F2**. You get:

- A **mode dropdown**: Global, Idle, Walk, Sprint, Crouch, CrouchWalk, Slide, Jump, Fall, Aim,
  Reload, Fire. This picks which bucket a new edit is filed under — it does **not** control what
  you see live. What you see is always whatever's really happening (see "How it decides what you
  see" below), so to preview a Crouch edit, actually crouch; to preview a Fire edit, use the
  **Preview Fire** button (real firing is disabled while the panel is open, the same way
  `WeaponGripTuner`'s number panel disables weapon-switch digit keys).
- A **searchable target list**: every bone in Manny's skeleton (type "index", "thumb", "spine",
  "lefthand", etc. to filter), plus two special targets pinned at the top — **Camera
  (first-person)** and **Whole-character scale**.
- **Position / Rotation / Scale fields** for the selected target (Camera has no scale field;
  Whole-character scale has only the scale field). Position is metres, rotation is degrees, scale
  is a multiplier — same units as everything else in your project (`WeaponData`, `LeftHandIk`).
- **Copy / Print ALL** — dumps every non-identity value across every mode as C# lines and copies
  them to the clipboard. Paste them into `CharacterRigTuning.LoadDefaults()` (there's a clearly
  marked spot) and that's your permanent, shipped calibration.
- **Reset this target** / **Reload ALL defaults**, and **Save session** / **Delete saved session**
  for surviving a restart mid-tuning-session (`user://character_rig_tuning.cfg`, debug builds
  only — doesn't affect release).

Walking, crouching, sprinting and aiming keep working while the panel is open (they're polled every
physics frame, not delivered as the input events the panel pauses), so you can hold the actual pose
while you dial it in. Only mouse-look and the weapon-switch digit keys are paused.

## How it decides what you see (the layering)

Every frame, for every player (yours and everyone else's), the system asks: "which modes are
actually active for this player right now?" — in priority order: **Fire** (if the fire one-shot is
playing) → **Reload** (if reloading) → **Aim** (if aiming down sights) → whichever movement state
they're in (Idle/Walk/Sprint/Crouch/CrouchWalk/Slide/Jump/Fall) → **Global**.

For each bone (and for the camera, and for character scale), it walks that list top to bottom and
uses the **first** mode that has an explicit entry for that target. So: a "hands size" tweak you
put in Global applies everywhere by default, but a specific Aim entry for the same bone silently
wins while you're aiming, then falls back to Global the moment you release aim. You never have to
duplicate a Global tweak into every mode — you only add an entry where a mode needs to differ.

## Multiplayer

Whichever single target you have selected is broadcast to every connected peer (rate-limited, with
a keepalive so a peer joining mid-session catches up) — you'll see it applied on their screen from
their own camera, and they'll see your finger/camera/scale tweak on your character from theirs.
This is a **shared rig calibration**, not a per-player cosmetic: everyone's copy of Manny uses the
same tuning data, exactly like `AnimationTuning` and `WeaponGripTuner` already work.

## Things you might want to add later (not built — flagging so you can decide)

- **Per-weapon overrides.** Right now overrides are per-mode only. A Glock and a Bazooka probably
  want different trigger-finger curls even in the same mode (e.g. both "Aim") — that would need a
  second axis (mode × weapon) on top of what's here.
- **Blend time.** A bone override snaps on the instant its mode becomes active (e.g. the moment you
  start aiming) rather than easing in. `AnimationTuning.CrossfadeSeconds` is the existing precedent
  if hard snapping looks bad on a particular bone.
- **Left/right mirroring.** Tuning `RightHandThumb1` doesn't touch `LeftHandThumb1`. Deliberate
  (asymmetric holds are common), but worth knowing if you expect a "mirror" button.
- **The first-person viewmodel skeleton.** `fps_mp5_arms.glb` / `FirstPersonMp5Viewmodel.cs` has its
  own separate rig, but it isn't instanced in `Player.tscn` yet (your first-person view is
  currently Manny's own arms). If you wire the viewmodel in later, this same system could target
  its bones too — it would just need a second "which skeleton" choice in the target list.
- **Death pose.** `PlayDeath()` plays a fixed `AnimationPlayer` clip with the tree switched off, so
  none of this touches ragdoll/death poses.
- **Undo history.** Only "reset this target" and "reload all defaults" exist — no per-step undo.
