# manny.glb rig switch — what changed and why

Extract over your project root. This replaces the old arms-only-rig architecture entirely, so
apply this on top of the previous two patches (combat + HUD) rather than instead of them.

## Why this was a bigger change than "swap the file"
The old rig (`Armature_arms.fbx`) carried a custom-retargeted skeleton named `GeneralSkeleton`
with a hand-built `Right_Hand_Attach` bone, plus two meshes on it — a full-body one for remote
players and an arms-only one for your own first-person view — switched by `FirstPersonArms.cs`.

`manny.glb` is a plain, single-mesh Mixamo export: one mesh ("Manny"), one skeleton (named
`Armature`, standard `mixamorig:` bone names), no `Right_Hand_Attach`, no arms-only variant. So
this wasn't a drop-in — the arms-only/full-body split, the weapon attach bone, and the animation
retarget target all had to change together. What you asked for is simpler than what was there
before, so the net result is actually less code, not more.

## New architecture
- **One visible body, always.** `Player.tscn`: `Armature` (manny.glb) is now a direct child of
  the player root, no separate arms mesh, no per-viewer visibility toggling. `FirstPersonArms.cs`
  is now unused — nothing references it anymore, safe to delete.
- **Camera stays on a separate pivot, not a bone.** The `Head` empty + `FirstPersonCamera` setup
  is unchanged (still at y=1.6). I did *not* parent the camera to the skeleton's head bone —
  doing that would make the camera jitter with every locomotion animation (head bob, turns,
  death poses), which is a real problem in first person. A static pivot driven by mouse look,
  positioned at roughly eye height, is the correct way to get a camera "on the face" without
  that side effect. If 1.6 doesn't line up with Manny's actual eye height once you can see it,
  that's a one-line number to nudge in `Player.tscn`.
- **Weapon attaches to `mixamorig:RightHand`.** No purpose-built grip bone exists anymore, so
  `WeaponAttachment.cs` now attaches straight to the hand bone with a zero offset as a starting
  point — expect to tune `Position`/`Rotation` there once you can see how it actually sits in a
  weapon's grip.
- **Animations load from 3 consolidated GLBs, not 40+ FBX files.** Your locomotion/combat/death
  exports each carry many already-correctly-named takes on the same skeleton as manny.glb.
  `PlayerAnimationController.cs` now loops over all three files and pulls every animation out of
  each one's library (previously: one clip per file, hardcoded index 0). Almost all clip names
  it needs (`locomotion_idle`, `combat_ads_idle`, etc.) matched what your state machine already
  expected — two had to be remapped since the old names don't exist in the new combat file:
  - `Jump` and `Fall` both now point at `combat_jump_loop` (previously a single `combat_jump`
    clip that no longer exists). `combat_jump_down` looks like it might be a better fit for
    `Fall` specifically or for landing — worth trying once you can see both play.
- **Death is now a real animation, not a vanish.** `Health.cs` no longer hides the body mesh on
  death (there's nothing to hide separately anymore, and hiding it would just make the character
  disappear instead of visibly dying). Instead it calls `PlayerAnimationController.PlayDeath()`,
  which drops out of the locomotion state machine and plays `"Death From The Front"` once — a
  fixed choice for now, not varied by hit direction (that needs attacker-relative data the hit
  RPC doesn't carry yet). `ResetAfterRespawn()` hands control back to locomotion on respawn.
- **Camera activation moved into `PlayerMovement.cs`.** It was `FirstPersonArms.cs`'s job to
  make sure only your own camera is `Current`; since that script's gone, `PlayerMovement._Ready`
  now sets it directly.
- **Skeleton lookup is now type-based, not name-based.** Both `PlayerAnimationController.cs` and
  `WeaponAttachment.cs` now search for *any* `Skeleton3D` under the player rather than a
  hardcoded node name. A hardcoded name/path mismatch is what broke this project twice already
  (the registry path bug, the missing `Armature_arms.fbx` reference) — this makes a future rig
  swap much less likely to silently break everything again.
- **`PlayerMovementTest.tscn` updated too.** This is your standalone movement/animation sandbox
  (floor + player, no networking) — it was still pointing at the old file. Fixed the same way,
  and it's genuinely the fastest way to check the new animations without needing two instances.

## Things you'll likely need to eyeball and tune once you can actually see it running
- Whether Manny faces the right way. Blender/Mixamo exports sometimes come in facing +Z instead
  of Godot's forward (-Z) — I couldn't verify this without rendering it, so if the character
  visually walks backwards, add `rotation_degrees = Vector3(0, 180, 0)` to the `Armature` node
  in `Player.tscn`.
- The weapon's position/rotation in the hand (currently zero offset, almost certainly needs a
  small nudge per weapon).
- Head/eye height (currently reused 1.6 from the old rig).
