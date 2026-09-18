# Sound slots

Every sound the game plays, where it is read from, and what belongs there. Nothing here is
hardcoded in C#: weapon audio lives in `res://scripts/data/WeaponRegistry.tres`, the rest is a
`const string` at the top of the script named in the last column.

Two ways to replace a sound: drop a file over the existing path (zero edits, keep the filename),
or point the string at a new path.

---

## Weapon audio - `scripts/data/WeaponRegistry.tres`

| Weapon | Field | File | Source |
|---|---|---|---|
| AK-47 | `FireSounds` | `Rifle/Shoot/AK_fire.wav` | your upload |
| AK-47 | `ReloadSounds` | `Rifle/Reload/ak_47_assault_rifle_being_unloaded_and_reloaded.wav` | your upload |
| AK-47, M4, P90, Kriss Vector | `EmptySound` | `Rifle/Empty/reload_click_rifle.wav` | your upload, shared |
| M4, P90, Kriss Vector | `FireSounds` / `ReloadSounds` | `Rifle/Shoot/Rifle_Shoot-00#.wav` / `Rifle/Reload/rifle_reload.wav` | original placeholder - no dedicated file was uploaded for these three |
| Glock 19, Desert Eagle | `FireSounds` | `Pistol/Shoot/pistol_shoot.wav` | your upload, shared |
| Glock 19, Desert Eagle | `ReloadSounds` | `Pistol/Reload/pistol_reload.wav` | your upload, shared |
| Shotgun | `FireSounds` | `Shotgun/Shoot/shotgun_fire.wav` | your upload |
| Shotgun | `ReloadSounds` | `Shotgun/Reload/shotgun_reload_shell.wav` | your upload - **one shell**, plays once per shell (see below) |
| Karambit | `FireSounds` | `Melee/Swing/melee_swing.mp3` | your upload - previously empty, the knife had no swing sound at all |
| all | `ImpactSounds` | `Pistol/Impact/Impact-00#.wav` | original - plays when a shot hits geometry, not a person |
| Bazooka | everything | `Grenade Launcher/...` | original - no bazooka file was uploaded |

**Shared "hit a person" sound**, not per-weapon - `scripts/player/WeaponAttachment.cs`,
`FleshImpactSound` constant: `Melee/Impact/flesh_hit.mp3` (your upload). Plays at the impact point
whenever any weapon's shot or swing lands on a player - bullet or blade, same file, since only one
recording was given and "a body got hit" reads the same either way. This is new: broadcast over
`WeaponSwitcher.RpcFleshImpact` so everyone nearby hears it, not just the shooter (the same gap
fire/reload effects had before the second pass - the hitscan runs on the shooter's device only, so
without a broadcast nobody else would ever hear a hit land).

**Two things worth knowing about the files you gave me:**
- `AK_fire.wav` is 2.5s long, almost certainly a real gunshot recording with its natural echo
  tail rather than a dry clip. At the AK's 600 RPM (0.1s between shots), full auto retriggers it
  before the tail finishes - each `Play()` call cuts the previous one short and restarts, which is
  standard and is what makes rapid fire sound continuous rather than looping the echo. No action
  needed, just don't be alarmed if you inspect the file and it looks long for a "single shot".
- `reload_click_rifle.wav` is 1.14s - longer than a bare mechanical click. I used it as the
  dry-fire sound (what plays when you pull the trigger on an empty mag) for all four rifle-class
  weapons. If you actually recorded it as a chamber-check/rack sound rather than a click, that's
  still the right slot; if you meant it as something else, tell me and I'll move it.

## Player audio - `scripts/player/PlayerAudio.cs`

Unchanged this pass - footsteps and hit/death/respawn were already wired to your original nine
`Player_Footstep_0#.wav` clips and the three `Player*.wav` clips. No new files touched these.

## Shooter feedback - `scripts/player/WeaponSwitcher.cs`

Unchanged - hit marker still plays `TargetHit-00#.wav`, owner-only, non-positional.

## Unused, still in the project

`CountdownBeep.wav` - no round timer exists yet.

---

## Still worth sourcing

- Dedicated M4, P90, and Kriss Vector fire/reload sounds (they currently share the AK-era generic
  rifle placeholder).
- A Desert Eagle-specific shot (it currently shares the Glock's `pistol_shoot.wav` - a .50 AE
  should sound much bigger than a 9mm).
- A Bazooka/RPG-specific launch and reload.
- Per-surface impact variation for `ImpactSounds` - concrete, metal container, dirt currently all
  share the same six clips.

Sonniss GDC bundle (gdc.sonniss.com) is still the best single source for these - royalty-free for
commercial use, no attribution, every past year's bundle still downloadable.
