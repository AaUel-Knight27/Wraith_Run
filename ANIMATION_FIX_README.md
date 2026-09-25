# Animation fix — what changed and how to use it

## What was actually wrong
1. **Only the straight-forward clips were ever played.** The locomotion state machine had one
   "Walk"/"Sprint" state wired to `locomotion_sprint_fwd` only. Moving sideways or backwards still
   played the *forward* run cycle, so the legs swung forward while the character travelled sideways —
   that's the "feels wrong" you were seeing on strafe/backpedal in particular.
2. **No speed matching.** `locomotion_sprint_fwd` is animated for ~6 m/s, but the character walks at
   4.7 and sprints at 7.2. Both played the clip at 1.0x, so the feet moved at the clip's speed while
   the body moved at the game's speed — guaranteed foot-sliding.
3. **The left hand was never attached to anything.** The rifle is bone-attached to the right hand, but
   nothing pinned the left (support) hand to the weapon, so it just followed whatever each clip
   happened to do with it — measured up to ~15 cm of drift between clips (sprint vs. aim vs. idle).
4. **Most locomotion clips imported as non-looping** (Godot's importer only turns looping on for a clip
   named `..._loop`), so several would have frozen on their last frame once actually used.

## What was fixed
- `PlayerAnimationController.cs` — rebuilt the locomotion graph as two directional blend spaces (run,
  crouch-walk) covering forward/back/both diagonals, with playback speed computed every frame from the
  ground speed each clip was authored for (measured directly, see `AnimationTuning.cs`).
- `LegHeadingTwist.cs` — since there are no pure left/right strafe clips, sideways movement is produced
  by turning the pelvis toward the travel direction and un-twisting the spine back to the animation's
  original orientation, so the chest/arms/gun keep facing forward while the legs run in the right
  direction. Standard technique for a mocap set without dedicated strafe clips.
- `LeftHandIk.cs` — a small two-bone IK chain (built at runtime, via Godot's `TwoBoneIK3D`) that pins
  the left hand to a fixed spot on the weapon every frame, fading out during reload and for one-handed
  weapons (pistols/melee).
- `PlayerMovement.cs` — `WalkSpeed` / `SprintSpeed` / `CrouchSpeed` are now `[Export]` properties (live-
  editable) instead of `const`.
- `WeaponData.cs` — added `LeftHandMode` / `LeftHandPosition` / `LeftHandRotationDegrees` so each
  weapon can store its own grip adjustment.

Verified in a headless Godot run: foot drift while a foot is planted is now well under 1 m/s in every
direction tested (was multiple m/s on anything but straight-forward walking), and the left hand tracks
its target to a fraction of a millimetre.

## The live tuner (F8)
A new panel next to the existing weapon-grip tuner (F3). Press **F8** in a debug build to open it:

- **Move speed** — live sliders for Walk/Sprint/Crouch speed.
- **Run cadence / Crouch cadence** — the authored ground speed for each direction; raise/lower if a
  particular direction still looks off.
- **Playback stretch limits** — how far a clip is allowed to speed up/slow down before the game gives up
  and lets it slide instead of looking like slow-motion or a benny-hill sprint.
- **Leg heading** — turn the strafe fix on/off and adjust the angle at which it swaps to the backward
  clips.
- **Left hand** — position/rotation offset and elbow pole position for the *current* weapon, live.
- **Save / Copy** — `Save` keeps your numbers across a restart (`user://animation_tuning.cfg`, debug
  only). `Copy AnimationTuning` / `Copy weapon lines` put ready-to-paste C#/`.tres` text on the
  clipboard so you can bake a tuning session into the source permanently.

## Editing animations by hand in Godot (if you want to go further)
- Double-click `art/characters/manny.glb` in the FileSystem dock → the import tab lets you re-inspect
  bones/animations, but the actual keyframes live in the source file, not something you hand-edit in
  Godot itself.
- To retime a clip without touching the source: an `AnimationNodeTimeScale` (used here) or the
  `AnimationPlayer`'s per-track "Scale" isn't exposed in the UI, but you *can* select any animation in
  the **Animation** panel (open it via `AnimationPlayer` node → bottom dock) and drag keyframes closer
  together/further apart on the timeline — this literally speeds up or slows down that stretch of the
  clip.
- To adjust a pose (e.g. nudge a hand), select the `Skeleton3D` → enable **Pose** mode in the toolbar,
  select a bone, move/rotate it, then in the Animation panel right-click the track at that frame →
  **Insert Key** (or press the key icon) to bake the tweak into the clip. Do this on the imported
  animation via an override, since the `.glb` itself is read-only on reimport — Godot supports this
  through **Animation → Save As...** to write a separate `.res`/`.tres` animation resource that keeps
  your edits.
- For anything skeletal/rig-level (bone lengths, hand-to-mesh offsets, adding twist bones), that has to
  happen in the source DCC tool (Blender/Mixamo) and be re-exported — Godot only remaps an existing
  skeleton, it doesn't let you re-rig a mesh.
