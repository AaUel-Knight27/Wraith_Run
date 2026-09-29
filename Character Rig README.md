# Character Rig Editor — integration guide

Three new files, two modified files. Drop the new ones in, replace the modified ones (or apply the
small diffs by hand if you've changed those files since your upload).

## Files

**New:**
- `scripts/player/CharacterRigTuning.cs` — the permanent data store (bone/camera/scale overrides,
  save/load, print-to-source). Not debug-only — this is what ships.
- `scripts/player/CharacterRigApplier.cs` — the two runtime consumers that actually pose the
  skeleton and camera every frame, for every player. Not debug-only either.
- `scripts/debug/CharacterRigTuner.cs` — the live editor UI (opened from the F2 debug-tools list; see README_DEBUG_HUB.md). Debug-only, inert in release
  exports, same rule as `WeaponGripTuner`/`WeaponSoundTuner`/`AnimationTuner`.

**Modified (replace your copies with these, or re-apply by hand):**
- `scripts/player/PlayerAnimationController.cs` — added `IsFirePlaying` (mirrors
  `IsReloadPlaying`) and wired the two new applier classes into `_Ready()`, right after
  `LeftHandIk.Setup(...)`. If you've touched this file since your upload, the diff is exactly:
  1. New property `IsFirePlaying` next to `IsReloadPlaying`.
  2. `CharacterRigTuning.EnsureLoaded();` added next to the existing
     `AnimationTuning.EnsureLoaded();` line.
  3. Eight-ish new lines after `_handIk.Setup(skeleton, _body, this);` that construct
     `CharacterRigOverrideModifier` and `CharacterRigCameraScaleApplier` and call their `Setup()`
     (the override modifier's `Setup()` also takes a `WeaponAttachment?`, found via
     `_body.GetNodeOrNull<WeaponAttachment>("Armature/WeaponAttachment")`, so it knows which
     weapon is equipped for the weapon-scoped half of the lookup - see "How it decides what you
     see" below).
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

Run the game (debug build), press **F2**, then pick **Character Rig Editor** from the list (press **2**, or click it). F2 inside the editor returns to the list. You get:

- A **mode dropdown**: Global, Idle, Walk, Sprint, Crouch, CrouchWalk, Slide, Jump, Fall, Aim,
  Reload, Fire. This picks which bucket a new edit is filed under — it does **not** control what
  you see live. What you see is always whatever's really happening (see "How it decides what you
  see" below), so to preview a Crouch edit, actually crouch; to preview a Fire edit, use the
  **Preview Fire** button (real firing is disabled while the panel is open, the same way
  `WeaponGripTuner`'s number panel disables weapon-switch digit keys).
- A **weapon dropdown**: "Any weapon" plus every weapon in `WeaponRegistry.tres`, so a Glock and a
  Bazooka can have different finger curls, different ADS camera offsets, whatever you want — same
  "pick a filing bucket" philosophy as the mode dropdown. Picking a weapon here doesn't require
  having it equipped (you can plan ahead); hit **Match equipped** to jump the dropdown to whatever
  you're actually holding right now. Leave it on "Any weapon" and nothing changes from before this
  existed.
- A **searchable target list**: every bone in Manny's skeleton (type "index", "thumb", "spine",
  "lefthand", etc. to filter), plus two special targets pinned at the top — **Camera
  (first-person)** and **Whole-character scale**.
- **Position / Rotation / Scale fields** for the selected target (Camera has no scale field;
  Whole-character scale has only the scale field). Position is metres, rotation is degrees, scale
  is a multiplier — same units as everything else in your project (`WeaponData`, `LeftHandIk`).
- **Copy / Print ALL** — dumps every non-identity value across every mode AND weapon as C# lines
  and copies them to the clipboard. A weapon-scoped line gets a trailing `"WeaponName"` argument; an
  "Any weapon" line looks exactly like it always did. Paste them into
  `CharacterRigTuning.LoadDefaults()` (there's a clearly marked spot) and that's your permanent,
  shipped calibration.
- **Reset this target** / **Reload ALL defaults**, and **Save session** / **Delete saved session**
  for surviving a restart mid-tuning-session (`user://character_rig_tuning.cfg`, debug builds
  only — doesn't affect release).

Walking, crouching, sprinting and aiming keep working while the panel is open (they're polled every
physics frame, not delivered as the input events the panel pauses), so you can hold the actual pose
while you dial it in. Only mouse-look and the weapon-switch digit keys are paused.

## How it decides what you see (the layering)

Every frame, for every player (yours and everyone else's), the system asks two questions: "which
modes are active for this player right now?" and "which weapon are they holding?"

Mode priority order: **Fire** (if the fire one-shot is playing) → **Reload** (if reloading) →
**Aim** (if aiming down sights) → whichever movement state they're in
(Idle/Walk/Sprint/Crouch/CrouchWalk/Slide/Jump/Fall) → **Global**.

For each of those modes, in order, the system checks the player's actual equipped weapon FIRST,
then falls back to "Any weapon" before moving on to the next, less-specific mode. So for a player
in Aim holding a Bazooka, the full lookup order for any given bone is: (Fire, Bazooka) → (Fire, Any)
→ (Reload, Bazooka) → (Reload, Any) → (Aim, Bazooka) → (Aim, Any) → (Idle, Bazooka) → (Idle, Any) →
(Global, Bazooka) → (Global, Any) — and the first one of those that has an explicit entry wins.

That means:
- A "hands size" tweak you put in **(Global, Any weapon)** applies to every weapon, in every mode,
  by default.
- A **(Aim, Any weapon)** entry for the same bone silently wins over Global the moment any weapon
  is aimed, regardless of which one.
- A **(Aim, Bazooka)** entry wins over both of those, but only while aiming the Bazooka
  specifically — aiming a Glock still falls through to whatever Aim/Any-weapon or Global says.
- A **(Global, Bazooka)** entry (no specific mode) would apply "whenever holding the Bazooka,
  in any mode" — handy for something like a heavier grip stance that should hold regardless of
  what you're doing, without touching every other weapon's Global tuning.

You never have to duplicate a broad tweak into every mode or every weapon — you only add an entry
where something needs to differ from what it would otherwise fall through to.

## Multiplayer

Whichever single target you have selected (bone/camera/scale, in whichever mode AND weapon scope)
is broadcast to every connected peer (rate-limited, with a keepalive so a peer joining mid-session
catches up) — you'll see it applied on their screen from their own camera, and they'll see your
finger/camera/scale tweak on your character from theirs. This is a **shared rig calibration**, not
a per-player cosmetic: everyone's copy of Manny uses the same tuning data, exactly like
`AnimationTuning` and `WeaponGripTuner` already work.

## Things you might want to add later (not built — flagging so you can decide)

- **Blend time.** A bone override snaps on the instant its mode/weapon combination becomes active
  (e.g. the moment you start aiming, or the moment you switch to the Bazooka) rather than easing
  in. `AnimationTuning.CrossfadeSeconds` is the existing precedent if hard snapping looks bad on a
  particular bone.
- **Left/right mirroring.** Tuning `RightHandThumb1` doesn't touch `LeftHandThumb1`. Deliberate
  (asymmetric holds are common), but worth knowing if you expect a "mirror" button.
- **The first-person viewmodel skeleton.** `fps_mp5_arms.glb` / `FirstPersonMp5Viewmodel.cs` has its
  own separate rig, but it isn't instanced in `Player.tscn` yet (your first-person view is
  currently Manny's own arms). If you wire the viewmodel in later, this same system could target
  its bones too — it would just need a second "which skeleton" choice in the target list.
- **Death pose.** `PlayDeath()` plays a fixed `AnimationPlayer` clip with the tree switched off, so
  none of this touches ragdoll/death poses.
- **Undo history.** Only "reset this target" and "reload all defaults" exist — no per-step undo.
