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
| AK-47, M4, P90, Kriss Vector, Glock 19, Desert Eagle, Shotgun, Bazooka | `EmptySound` | `Rifle/Empty/reload_click_rifle.wav` | your upload, shared - only click on hand, used everywhere a dry-fire click is needed. Karambit has none: melee has infinite "ammo" and never hits the dry-fire branch. |
| M4, P90, Kriss Vector | `FireSounds` / `ReloadSounds` | `Rifle/Shoot/Rifle_Shoot-00#.wav` / `Rifle/Reload/rifle_reload.wav` | original placeholder - no dedicated file was uploaded for these three |
| Glock 19, Desert Eagle | `FireSounds` | `Pistol/Shoot/pistol_shoot.wav` | your upload, shared |
| Glock 19, Desert Eagle | `ReloadSounds` | `Pistol/Reload/pistol_reload.wav` | your upload, shared |
| Shotgun | `FireSounds` | `Shotgun/Shoot/shotgun_fire.wav` | your upload |
| Shotgun | `ReloadSounds` | `Shotgun/Reload/shotgun_reload_shell.wav` | your upload - **one shell**, plays once per shell (see below) |
| Karambit | `FireSounds` | `Melee/Swing/melee_swing.mp3` | your upload - previously empty, the knife had no swing sound at all |
| all except Bazooka | `ImpactSounds` | `Pistol/Impact/Impact-00#.wav` | original - now a **fallback only**. Every weapon with `ImpactUsesSurfaceVariation` true (the default) picks its geometry-impact sound from the surface actually hit instead - see "Surface-varied bullet impacts" below. |
| Bazooka | everything, `ImpactUsesSurfaceVariation = false` | `Grenade Launcher/...` | original - no bazooka file was uploaded. Explicitly opted out of surface variation: an explosion doesn't care whether the wall was concrete or wood. |

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

Footsteps and the original hit/death/respawn clips are unchanged from the previous pass. New this
pass, all in `res://audio/Clips/Player/`:

| Sound | File | Plays when |
|---|---|---|
| Jump | `Movement/PlayerJump.wav` | Take-off, on the transition into the Jump state - not on every frame spent airborne, and not on a fall off a ledge with no jump input. |
| Crouch | `Movement/PlayerCrouch.wav` | Once, on the transition into Crouch or CrouchWalk. |
| Walk loop | `Movement/PlayerWalkLoop.wav` | Quiet, looping, under the existing footstep clicks while Walk or CrouchWalk. Texture, not the primary footstep cue - that's still the nine `Player_Footstep_0#.wav` clips. |
| Sprint loop | `Movement/PlayerSprintLoop.wav` | Same as above, while Sprint. |
| Second hit reaction | `Hit Dead And Respawn/PlayerHit_02.wav` | Randomly alongside the original `PlayerHit.wav` whenever this player takes damage and survives. |
| Low-health breathing | `Low Health/LowHealthBreathing.wav` | Looping, **local player only, non-positional** - starts when this player's own health drops to 30% of max or below, stops when it recovers or they die. |

**Death has two clips now, not a random pick between them - which one plays is decided, not
rolled:**

| Sound | File | Plays when |
|---|---|---|
| Clean death | `Hit Dead And Respawn/PlayerDead.wav` (unchanged) | The kill was a headshot, or a single hit for 100+ damage (a full-health player dropped in one shot) - **unless** it was melee, or the victim was already on the verge of death (≤20% health, the same threshold `KillStyleBonus.VergeOfDeath` uses) when the finishing blow landed. |
| Struggling death | `Hit Dead And Respawn/PlayerDeathFinisher.wav` | Everything else: a body-shot kill that wasn't instantly lethal, **any** melee kill (deliberately never counted as "instant" even though the Karambit can one-shot), or finishing off someone already critical. |

The decision itself is made once, on the victim's own authoritative device, in
`Health.DetermineIfDeathStruggles` (called from `ReceiveDamage` the moment the killing blow lands),
and rides to every peer through the existing `KillFeed.KillAnnounced` broadcast - a `bool struggles`
parameter added onto the end of it - rather than a new message of its own. `PlayerAudio` picks up
that value in `OnKillAnnounced` and consumes it the next time it notices this player's own
alive-to-dead edge. This is intentionally **not** the same thing as a "medic needed" callout - no
comms/voice-line system exists yet; this is purely the ambient sound of the death itself, audible to
anyone nearby the same way the original death clip always was (ordinary 3D positional falloff, nothing
special done to make it carry further).

## Shooter feedback - `scripts/player/WeaponSwitcher.cs`

Hit marker unchanged - still `TargetHit-00#.wav`, owner-only, non-positional.

**Surface-varied bullet impacts, ricochet, and flyby - new this pass**, all in
`res://audio/Clips/Weapons/Impacts/Surfaces/`:

| Sound | File | Plays when |
|---|---|---|
| Metal (heavy) | `BulletMetalHeavy.mp3` | A geometry hit classified as Metal - 50/50 against the "thick" clip below, for variety. |
| Metal (thick) | `BulletMetalThick.mp3` | Same as above, the other half of the time. |
| Wood | `BulletWood.mp3` | A geometry hit classified as Wood. |
| Glass | `BulletGlassHit.wav` | A geometry hit classified as Glass. |
| Wall / generic | `BulletWallHit.wav` | Everything else - the new default, replacing the old shared pistol-impact thud for any weapon that hasn't opted out (see the weapon table above). |
| Ricochet | `BulletRicochet.wav` | Layered on top of a Metal or generic/Wall hit, ~22% of the time (never on Wood/Glass, never for melee). |
| Bullet flyby | `BulletFlyBy.wav` | A shot that passed within ~1.4m of another player's head without hitting them, positioned at the closest point on the shot's own path so 3D falloff makes it loudest for whoever it actually passed. Bullets only - melee never checks for this. |

**How a geometry hit is classified (`scripts/combat/SurfaceType.cs`) - please read this before
assuming it's smarter than it is:** the map kit has no per-material physics tagging. Procedurally
built walls/floors/ruins in all three maps get merged by `MeshBatcher.BuildCollision` into one
collision shape per map chunk regardless of material, so most of the map's geometry has no material
identity left to read at runtime. Classification is a best-effort guess:
1. **Exact route**: add the prop (or any ancestor) to the Godot group `surface_metal`,
   `surface_wood`, or `surface_glass` in the editor. Always wins if present.
2. **Fallback**: guesses from the collider's own node name. This already gets Warzone's hand-placed
   `Container0_0`/`Drum0`-style props right for free (genuinely metal, and real scene nodes rather
   than batched geometry) - nothing currently matches Wood or Glass, since no props with those names
   exist in the shipped maps yet.
3. Anything else - which today means essentially all procedural wall/floor/ruin geometry - falls
   through to the new generic Wall-Hit sound. Still a real upgrade over the single shared
   pistol-thud every surface used before, just not a "the game actually knows it's concrete" one.

Also new: a geometry impact (wall/container/etc., not a person) now **broadcasts** to every peer
over `WeaponSwitcher.RpcSurfaceImpact` instead of only playing locally on the shooter's own device -
the same gap the flesh-impact sound had before the previous pass fixed it for hits-on-people.

## Menu music - `scripts/network/LanMenu.cs`

New this pass. Two tracks in `res://audio/Clips/UI and HUD/Menu Music/` -
`TacticalSilence.mp3` and `TacticalSilenceV2.mp3` - played back to back in random order on the
`Music` bus, starting in `_Ready()` and stopped in `RunTerminalCleanup()` (the menu's one shared exit
path, so this covers leaving for a match and closing the app alike). `SettingsManager` was already
applying `MusicVolume` to the `Music` bus in anticipation of exactly this, so the Audio settings
slider works on it immediately with no further wiring.

## Unused, still in the project

`CountdownBeep.wav` - no round timer exists yet.

---

## Still worth sourcing

- Dedicated M4, P90, and Kriss Vector fire/reload sounds (they currently share the AK-era generic
  rifle placeholder).
- A Desert Eagle-specific shot (it currently shares the Glock's `pistol_shoot.wav` - a .50 AE
  should sound much bigger than a 9mm).
- A Bazooka/RPG-specific launch and reload.
- A landing thud (there is a jump take-off sound now, but nothing plays when a fall ends).
- A slide sound (Slide is still a silent movement state).
- Real per-surface impact accuracy would need actual material tags on map geometry (the
  `surface_metal`/`surface_wood`/`surface_glass` groups above are ready for this) rather than relying
  on the node-name guess - most valuable for the two procedural indoor maps, Depot and Ashfields,
  which have no named props to guess from at all today.
- The separate "medic needed" comms/voice-line callout mentioned when this pass was scoped - not
  built here on purpose; it's a distinct feature (a player-triggered communication line) from the
  struggling death sound above, which is purely ambient and tied to how a kill happened, not
  something a player calls out themselves.

Sonniss GDC bundle (gdc.sonniss.com) is still the best single source for these - royalty-free for
commercial use, no attribution, every past year's bundle still downloadable.
