# Live weapon grip tuner

Every weapon's `GripPosition` / `GripRotationDegrees` / `ModelScale` in
`scripts/data/WeaponRegistry.tres` was computed from the raw mesh geometry - each model's
longest axis, its muzzle end, an estimate of where the handle is. That's a real starting point,
not a guess pulled from nowhere, but nobody has looked at the result on screen, and it shows:
the guns don't sit right in the hand yet.

There's no way for me to see your screen, so rather than keep guessing blind, `F3` in-game opens
a live tuner that lets you nudge the equipped weapon in real time and copy the exact numbers back
out. This is the "clean guide" - it's faster and more accurate than a written one, because you
get to see the result of every change immediately.

## Controls

Press **F3** to open the overlay. It shows the current weapon's live position, rotation, and
scale in the top-left corner.

| Keys | Effect |
|---|---|
| Arrow keys | move the weapon left/right and forward/back |
| Page Up / Page Down | move the weapon up/down |
| `[` / `]` | rotate yaw - **this is the one you'll use most**, it's what points the barrel down your sight line |
| `,` / `.` | rotate pitch |
| `;` / `'` | rotate roll |
| `-` / `=` | scale the model up/down |
| Hold **Shift** | coarse steps, for closing in fast - release it to fine-tune |
| **F4** | print the current values to the Godot output console, formatted ready to paste |
| **F5** | reset the weapon to whatever's currently saved in the .tres, discarding your changes |
| **F3** | close the overlay |

## Workflow

1. Run the game, equip the weapon you want to fix (number keys 1-4).
2. Press **F3**.
3. Nudge position first (get the gun roughly where it should sit relative to your view), then
   rotation (point the barrel straight down your aim line - ADS is the best pose to check this
   in, since that's when a misaligned barrel is most obvious), then scale last.
4. Press **F4**. The console (visible in the Godot editor's Output panel, or in a terminal if you
   ran an exported build from one) prints something like:

       --- AK-47 grip (paste into scripts/data/WeaponRegistry.tres) ---
       GripPosition = Vector3(0.031, 0.024, 0.015)
       GripRotationDegrees = Vector3(-90, 0, 4)
       ModelScale = 0.98
       ---

5. Open `scripts/data/WeaponRegistry.tres`, find that weapon's `[sub_resource ...]` block (search
   for `WeaponName = "AK-47"`), and replace its `GripPosition` / `GripRotationDegrees` /
   `ModelScale` lines with the three printed above.
6. Press **F5** to reload the weapon fresh and confirm it still looks right, or switch weapons and
   back. Repeat for the next gun.

## Notes

- All three numbers are in the **hand bone's local frame**, same as the registry field - +Y is the
  line of fire and +X is up in that frame, which is why most rotations are near -90 or 90 on one
  axis. You don't need to think about this while tuning; it only matters if you're reading the
  printed numbers and wondering why they don't look like "normal" world-space values.
- The tuner only touches the local player's own view. It sends nothing over the network - it
  can't be used mid-match to see or affect anyone else, and it has no effect on hitscan or damage.
- It's a development tool, not shipped behaviour. Nothing stops it from being present in a
  release build; if that matters to you before shipping, either remove
  `scripts/debug/WeaponGripTuner.cs` and its node from `scenes/Player.tscn`, or gate `_Ready` on
  `OS.IsDebugBuild()`.
- If a weapon has no model (Kriss Vector right now - its mesh didn't survive the FBX-to-GLB
  conversion), there's nothing to tune; the overlay will show the weapon name with no scale
  change taking visible effect.
