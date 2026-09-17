# Fix pass: FOV, camera, manny.glb placement

Extract over the project root. Touches 5 files:
`art/characters/manny.glb`, `scenes/Player.tscn`, `scripts/player/PlayerMovement.cs`,
`scripts/player/WeaponAttachment.cs`, `project.godot`.

Delete `.godot/imported/manny.glb-*.scn` (or just let Godot reimport on open) so the
new glb scale is picked up.

---

## 1. manny.glb was exported 2.14x too large

The glTF root node `Armature` had `scale = 0.021364`. The three animation exports on the
same rig have `0.01` (locomotion and death) — the bone rest translations are byte-identical
across all four files, so `0.01` is the intended unit conversion and manny's was wrong.

Measured before: **3.82 m tall**, hips at y=2.14, against a 1.8 m capsule.
Measured after: **1.805 m tall**, feet at y=0.01, hips at y=1.00, crown at y=1.80.

The pivot is at the feet, which is what a `CharacterBody3D` with a 1.8 m capsule centred at
0.9 wants.

Fix the Blender export too (apply scale / export at 0.01), otherwise a re-export regresses
this. `player animation combat.glb` also has a wrong root scale (0.09369) — harmless right
now, because `PlayerAnimationController` only rebinds bone tracks and bone-local values are
in the same centimetre units in every file, but worth fixing at the source.

## 2. The body mesh was 2.14 m behind the player

`Player.tscn` had the Armature instance at `Vector3(0, 0, 2.14)`. That's the hips' Y offset
(2.1355) written onto Z, so the character stood a little over two metres *behind* the
capsule that was being shot at and collided with. Reset to identity.

## 3. Camera was inside the body

With the model at 2.14x, eye height 1.66 landed near the character's waist, so the camera
was rendering from inside the torso. After the scale fix, 1.66 sits inside the head instead.

Measured the head geometry directly: at eye height the face front is at z = -0.086, the back
of the skull at z = +0.140. Camera is now at `(0, 0.09, -0.15)` on the Head pivot — eye level
~1.69, and 6 cm clear of the face with `near = 0.05`.

This is a workaround, not a real solve. There is no arms-only view model any more, so the
proper fix is either splitting the head into its own `MeshInstance3D` in Blender and hiding
it for the local player, or putting the head on a separate visual layer. Bone-scale tricks
won't hold: the Mixamo clips carry scale tracks on `mixamorig:Head`, so the AnimationTree
overwrites any override every frame.

## 4. ADS FOV — the actual bug

Aiming was a `MovementState`. Being a state made it **mutually exclusive** with every other
state, so:

- Aim while crouched → state went `Crouch` → `Aim`, which made `UpdateColliderAndHead`
  receive `crouching == false` and stand the capsule back up while you were still holding C.
- Aim while walking → the AnimationTree travelled to `combat_ads_idle`, so you slid around in
  a frozen aim pose with no walk cycle.
- Aim in the air → nothing at all. `UpdateAirMovement` only ever assigns `Jump`/`Fall`, so the
  state never became `Aim` and the FOV never moved.
- Aim was only evaluated inside `UpdateGroundMovement`, i.e. never while sliding either.

Now:

- `IsAiming` is an independent flag, evaluated every physics frame, cancelled only by Sprint
  and Slide. Crouched, walking and airborne ADS all work.
- `MovementState.Aim` survives purely as an *animation* state, entered only when standing
  still and upright on the ground, so it no longer clobbers real locomotion.
- `ReplicatedAimState` now carries the real flag rather than a state comparison.
- FOV moved into `UpdateFov`. The transition step is derived from the configured range
  instead of a hardcoded `20.0f / 0.15f`, so retuning the numbers doesn't silently change the
  transition duration.
- `BaseFov` / `SprintFov` / `AimFov` / `FovTransitionSeconds` are `[Export]`, tunable in the
  inspector while running.

## 5. FOV was also being stretched by the window

`project.godot` had a **648x321** viewport — 2.02:1. Godot's `Camera3D` defaults to
`KEEP_HEIGHT`, so `fov` is the *vertical* angle and the horizontal angle comes from the
aspect. At that window size a vertical 75 gave a horizontal **114 degrees**, and ADS at 55
gave 93 — a fisheye that barely zoomed when you aimed. That alone would read as "FOV is
broken."

Viewport is now 1280x720. `keep_aspect` and `fov` are written explicitly on the camera so
the intent is visible in the inspector. `KEEP_HEIGHT` is kept deliberately: it matches
Unity's `Camera.fieldOfView` axis, so the 75/90/55 numbers carried over from the Unity build
mean the same thing they did there.

At 16:9, vertical 75 is horizontal ~106, which is still wide. If it looks fisheye, drop
`BaseFov` to 65-70 in the inspector — the values are exported now precisely so you can do
that without a rebuild.

## 6. Head position was written twice per frame

`UpdateColliderAndHead` set `_head.Position`, then the end of `_PhysicsProcess` overwrote it
with `_head.Position.Y + _headOffset.Y` and dropped the base X/Z. It happened to work because
the base X/Z were zero. Now `UpdateColliderAndHead` stores `_headBaseHeight` and the position
is composed once, at the end, from base + bob + shake.

## 7. Bonus: the weapon was 100x too small

Everything parented under a bone inherits the skeleton's 0.01 root scale, so the AK-47 was
rendering at 1/100 size — effectively invisible. `WeaponAttachment` now cancels the
skeleton's global scale on the weapon instance. Derived at runtime rather than hardcoded to
100, so a future re-export at a different unit scale still works.

The grip offset is still `Vector3.Zero`. Now that the weapon is actually visible at the
right size, that's the next thing to eyeball and nudge.

---

## Not touched (still open)

- `WeaponSwitcher.IsHeadZoneHit` measures against a remote player's `Head` node, but `Head`'s
  *position* isn't in the replication config — only its rotation. Remote copies also return
  early from `_PhysicsProcess`, so their `Head` stays at the scene default 1.6. Headshots on a
  crouched opponent are tested against a standing head.
- No ADS movement-speed penalty. Aiming currently walks at full `WalkSpeed`. That's a design
  call, not a bug, so I left it.
- `FirstPersonArms.cs` is still dead code.

---

# Second pass: facing direction + respawn pose

## 8. The character faced backwards

The rig's forward axis is +Z, but Godot's forward is -Z, so every player was rendered turned
180 degrees from the direction they were actually facing and moving. Two independent checks
agree: `mixamorig:RightHand` sits at x = -0.47 (a character facing -Z has its right hand at
+X), and the head geometry's deep side is +Z (0.140 behind the head bone vs 0.086 in front),
which is the face.

The Armature instance in `Player.tscn` now carries a 180-degree Y rotation. Everything
parented under the skeleton - the bone attachment and the weapon on it - rotates with it, and
the Head pivot and camera are siblings, so neither is affected.

If you ever re-export from Blender, rotating the armature 180 degrees on Z there and applying
it is the cleaner place to fix this; then drop the rotation from the scene.

## 9. Respawning as a corpse

`Health.Respawn()` only ever runs on the owning peer - `_PhysicsProcess` returns early for
everyone else. So `ResetAfterRespawn()` was only ever called on one machine. Every *other*
peer's copy of that player kept `_isDead = true` and an inactive AnimationTree, frozen in the
death pose permanently. What replicates is `CurrentHealth`, not the respawn call.

`PlayDeath` was already hanging off the health setter, which is why death looked right
everywhere. The reset now hangs off the same setter, on the dead-to-alive edge:

    ApplyVisualState(wasAlive, alive)
      wasAlive && !alive  -> PlayDeath()
      !wasAlive && alive  -> ResetAfterRespawn()

Edge-triggered on purpose - firing on every write would restart the locomotion state machine
each time you took a point of damage. The duplicate call in `Respawn()` is gone.

Second half of the same bug, this one affecting the local player too: `PlayDeath` switches
the AnimationTree off and drives the death clip through the AnimationPlayer directly.
Re-enabling the tree does not cancel that - the AnimationPlayer keeps holding the corpse pose
and fights the tree for the same bones. `ResetAfterRespawn` now calls `_animationPlayer.Stop()`
before reactivating the tree.
