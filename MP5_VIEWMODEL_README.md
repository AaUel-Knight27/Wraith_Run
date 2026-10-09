# MP5 first-person viewmodel - what this patch does

You sent two things: `animated-mp5.zip` (an FBX arms+MP5 rig with baked Idle/Reload/Shoot
animations) and your current `wraith_run_godot.zip`. This patch turns the FBX into a game-ready
asset and wires it in as the local player's first-person hands + camera anchor, on top of the
existing weapon system rather than instead of it - **shared character is untouched**: his skeleton, his body
mesh, his locomotion/combat animations, and what every *other* player sees of you are all exactly
as they were.

## What changed, in one paragraph

There are now two `WeaponAttachment`s per player instead of one. The original, on shared character's
`Armature`, still renders the world-space weapon everyone else sees. A new one, on the new
viewmodel's own skeleton, renders a second copy of the equipped weapon positioned for your own
first-person view, parented under `FirstPersonCamera` so it inherits every bit of head look and
recoil for free. The viewmodel's own arms play the rig's Idle/Shoot/Reload/Equip clips. Every
weapon in your current loadout (AK-47, M4, P90, Glock 19, Desert Eagle, Shotgun, Karambit, Bazooka)
already works with it, because it reuses the exact same "attach a model to a hand bone at a
per-weapon grip offset" system `GripPosition`/`GripRotationDegrees` already use - it just has a
second set of numbers (`ViewmodelGripPosition`/`ViewmodelGripRotationDegrees`) for the new hand.

## The new asset: `art/characters/fps_mp5_arms.glb`

I don't have Godot, Blender, or a GPU in the environment I did this in, so this went FBX ->
(`assimp`) -> glTF -> a Python script (`pygltflib`) that fixed the following, then Godot's own
importer takes it from there exactly like `AshenCorvus.fbx`:

- **Removed 14 nodes** that were Blender rig gizmos, not art: a camera-frustum outline, IK pole
  shapes, reticle rings, and the loose `A_Camera` reference object. None of it was meant to render;
  `assimp`'s FBX path doesn't carry Blender's per-object viewport/render visibility, so all of it
  came through as ordinary visible meshes. What's left is the arm/hand mesh, the skeleton, and the
  MP5's own mesh parts (which you won't normally see rendered - more on that below).
- **Fixed both materials.** The FBX's own material blocks carried absolute Windows texture paths
  (`C:\Users\...`) that resolve on nobody's machine, and the arm/hand material had no texture link
  in the file at all - just a flat grey color. The real textures were sitting right there in your
  zip; I wired `pl0000_handr_d/n.png` onto the arm material and
  `M_LayeredMaterial_Base_color/Normal_OpenGL.png` onto the gun material, packing Roughness+Metallic
  into one glTF-spec ORM texture (the FBX had them as separate flat images). I also re-saved every
  texture with normal PNG compression - the shipped files were ~12.6 MB each for what's actually
  2048x2048 (essentially uncompressed); they're now 3-4 MB with no quality loss.
- **Fixed the unit scale and placed it relative to the camera.** The source FBX is authored in
  centimetres (confirmed by comparing its own bone lengths against `AshenCorvus.fbx`'s). The rig also
  ships its own camera reference - a bone called `CameraHelper`, which its Blender camera was
  constrained to. I measured `CameraHelper`'s bind-pose transform and baked the inverse of it,
  scale included, directly into the file's root node. The practical effect: this asset, instanced
  with **no extra transform** as a child of `FirstPersonCamera`, lands exactly where the rig's own
  author intended the eye to be - which is what "replace the camera position with the animation I
  built" means here. (The full derivation - the matrix chain, the bind-pose measurements, the
  verification that it actually resolves to identity - was done step by step; ask if you want the
  numbers re-explained or re-derived for a re-export.)
- **Renamed the four clips** from `Armature|Idle` / `Armature|Reload` / `Armature|Shoot` /
  `Armature|Take` to `mp5_idle` / `mp5_reload` / `mp5_shoot` / `mp5_equip`. The first three are
  self-explanatory; the fourth is a **guess based on evidence, not a label the file had**: its hand
  rotation starts away from rest and ends bit-for-bit at `mp5_idle`'s resting rotation, which is
  what a weapon-raise animation looks like and not what a stray default Blender action name would
  be. If you look at it and it's actually something else, it's easy to rename back.

### A defect I found but did not try to fix

The MP5's own body mesh in the FBX has a real scale problem - even correctly skinned, it comes out
roughly 400-600x too large relative to the hand mesh (which is correctly scaled). This isn't
something my conversion introduced; I traced it back to the source FBX's own vertex data before
touching anything. I didn't chase it further because **it doesn't matter for how this ships**: the
embedded gun meshes are hidden by `FirstPersonMp5Viewmodel.cs` and never rendered - every weapon,
MP5 included if you ever add it to the loadout, goes through `WeaponAttachment` exactly like the
other eight. If you ever want the rig's own MP5 model specifically (say, to add "MP5" as a ninth
loadout entry), it needs to be re-scaled properly, ideally by re-exporting that one mesh from
Blender with its transform applied, rather than patched around in glTF.

One more assumption worth flagging: I bound `pl0000_handr_n.png` as an OpenGL-convention normal map
(matching how `M_LayeredMaterial_Normal_OpenGL.png` is explicitly labeled, and how glTF/Godot
expect normal maps by default). If the hand's shading looks inverted/bumpy-in-reverse, that map
needs its green channel flipped.

## Files in this patch

**New:**
- `art/characters/fps_mp5_arms.glb` - the cleaned, corrected asset described above.
- `scripts/player/FirstPersonMp5Viewmodel.cs` - drives the viewmodel: local-only visibility, hides
  shared character's body for your own view, hides the rig's own (broken-scale) gun meshes, plays
  Idle/Shoot/Reload/Equip.

**Changed (additive - diff them before merging, same as your character-system patch):**
- `scripts/data/WeaponData.cs` - added `ViewmodelGripPosition`/`ViewmodelGripRotationDegrees`.
  Nothing existing renamed or removed.
- `scripts/player/WeaponAttachment.cs` - the hand-bone-name const became an `[Export]`
  (`HandBoneFragment`, still defaults to `"righthand"` - shared character's attachment is unaffected), plus
  three new `[Export]`s (`UseViewmodelGrip`, `HiddenFromOwner`, `SkipAudio`) that are all off by
  default, so this class behaves exactly as before unless a scene opts in.
- `scripts/player/WeaponSwitcher.cs` - equips/fires/reloads the viewmodel attachment alongside the
  existing one, all through nullable references (`_viewmodelAttachment`, `_viewmodel`) so a scene
  without the new nodes still runs exactly as it does today.
- `scenes/Player.tscn` - added the new asset + script as ext_resources, added
  `HiddenFromOwner = true` to the existing `Armature/WeaponAttachment`, and added
  `Head/FirstPersonCamera/FirstPersonMp5Viewmodel` (the new asset instanced with no extra
  transform, per the placement note above) with its own child `WeaponAttachment`
  (`HandBoneFragment = "Hand.R"`, `UseViewmodelGrip = true`, `SkipAudio = true`).

## How the pieces fit together

```
Player
├─ Armature (AshenCorvus.fbx)               <- world model: body, legs, third-person weapon, unchanged
│   └─ WeaponAttachment                  HiddenFromOwner=true (see below)
├─ Head
│   └─ FirstPersonCamera                 <- PlayerMovement owns look/recoil/FOV, still does
│       └─ FirstPersonMp5Viewmodel     <- new: fps_mp5_arms.glb, no transform of its own
│           └─ WeaponAttachment          HandBoneFragment="Hand.R", UseViewmodelGrip=true
```

`WeaponSwitcher.ApplyWeaponIndex` now calls `Equip()` on both attachments, and `RpcFireEffects`/
`RpcReloadEffects` now also drive the viewmodel's own animation and muzzle flash. Ammo, fire rate,
hit registration, damage, and everything networked are untouched - this patch is purely what you
see and hear of your own weapon.

`HiddenFromOwner = true` on the world attachment stops you from seeing two overlapping guns (the
close-up viewmodel one and shared character's own world-scale one) - it hides that attachment's model **and**
muzzle flash from the device that owns the player, while leaving both fully visible to everyone
else, the same way `PlayerMovement` already makes only your own `Camera3D` current. **This will get
in your way the next time you use `WeaponGripTuner` (F3)** to tune a *world*-model grip, since
you're also that weapon's owner - just uncheck `HiddenFromOwner` on `Armature/WeaponAttachment` in
the Inspector while you're tuning, and check it back after.

## How to test

1. Open the project in Godot, let it reimport (`fps_mp5_arms.glb` needs a first-time import same as
   any new asset), and run.
2. You should see the new arms in first person, positioned by the camera-relative placement above,
   with no manual offset needed.
3. Switch weapons (number keys) - each should show `mp5_equip` play once and settle into
   `mp5_idle`, holding whatever the equipped weapon's model is at the baseline hold (exact for
   anything MP5-shaped; other weapons may want a nudge, see below).
4. Fire and reload - `mp5_shoot`/`mp5_reload` should play, with a muzzle flash from the viewmodel's
   own barrel position, and everyone else should still see your world-model weapon and hear/see its
   effects exactly as before.

## Tuning a weapon that doesn't sit right

Every weapon starts from the MP5's own measured hold (the "baseline" in `WeaponAttachment.cs`), so
anything roughly rifle-shaped needs no numbers at all. For a pistol or the knife, edit that
weapon's `.tres`:

- `ViewmodelGripPosition` (metres, in Hand.R's frame) and `ViewmodelGripRotationDegrees` (degrees) -
  same "adjustment on top of a default hold" pattern `LeftHandPosition` already uses.

There's no F-key panel for these yet (unlike `GripPosition`'s `WeaponGripTuner`/F3) - edit the
`.tres` and re-run, or use `FirstPersonMp5Viewmodel.ExtraPositionMeters`/`ExtraRotationDegrees` via
the Scene dock's **Remote** tab while playing for a quick global nudge (that moves the whole
viewmodel, every weapon at once, so fold a per-weapon result into the fields above afterward rather
than leaving it there).

## Known limitations / natural follow-ups

- **No left-hand IK on the viewmodel.** The rig's own two-hand pose is baked into
  `mp5_idle`/`mp5_shoot`/`mp5_reload` and looks right for anything MP5-shaped. For a one-handed
  weapon (Glock, Karambit) the support hand will still show the MP5's own grip pose rather than
  dropping away - porting `LeftHandIk.cs` to `Hand.L` on this rig (its bone names are `UpperArm.L`/
  `LowerArm.L`/`Hand.L`, not `LeftHandIk`'s current `leftarm`/`leftforearm`/`lefthand` suffixes, so
  it needs the same bone-name generalization `WeaponAttachment` got) is the natural next step if
  that bothers you in practice.
- **Remote peers still build a viewmodel they never show.** `FirstPersonMp5Viewmodel` hides the
  whole thing for anyone but its owner, but the scene, skeleton, and second `WeaponAttachment` are
  still instanced on every peer's copy of every player. Harmless but not free; skipping the
  instance entirely for non-owned players is a reasonable optimization later.
- **MP5 itself isn't in the loadout.** This patch is the *system* the animation asked for, not a
  new playable weapon - `Loadout` in `WeaponSwitcher.cs` is untouched. Adding "MP5" as a ninth
  weapon needs its own properly-scaled model (see the defect note above) plus a `WeaponData` entry;
  happy to do that as a follow-up if you want it.
