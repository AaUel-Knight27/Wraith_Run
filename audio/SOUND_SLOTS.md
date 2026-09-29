# Sound slots

Every sound the game plays, where it is read from, and what belongs there. Nothing here is
hardcoded in C#: weapon audio lives in `res://scripts/data/WeaponRegistry.tres`, the rest is a
`const string` at the top of the script named in the last column.

Two ways to replace a sound: drop a file over the existing path (zero edits, keep the filename),
or point the string at a new path.

**2026-09-29 rebuild**: every array below now holds exactly one clip per slot. Earlier passes left
some weapons with 2 clips that were randomly alternated shot-to-shot (AK-47, Shotgun `FireSounds`)
and some with a 2nd `ReloadSounds` entry that was actually dead code - `WeaponAttachment.PickOne`
only ever reads index 0, so a longer reload array never did anything. Both were the source of the
"confused/repeated" sound complaint. Collapsing every slot to one clip fixes the first and just
removes now-pointless duplicate files for the second.

---

## Weapon audio - `scripts/data/WeaponRegistry.tres`

| Weapon | Field | File |
|---|---|---|
| AK-47 | `FireSounds` | `Rifle/Shoot/ak47_shooting.mp3` |
| AK-47 | `ReloadSounds` | `Rifle/Reload/ak_47_assault_rifle_being_unloaded_and_reloaded.wav` (unchanged, no new AK reload was supplied) |
| M4 | `FireSounds` / `ReloadSounds` | `Rifle/Shoot/m4_shooting.mp3` / `Rifle/Reload/ar_reload.mp3` |
| P90 | `FireSounds` / `ReloadSounds` | `SMG/Shoot/p90_shooting.mp3` / `SMG/Reload/smg_reload.mp3` |
| Kriss Vector | `FireSounds` / `ReloadSounds` | `SMG/Shoot/kriss_vector_shooting.mp3` / `SMG/Reload/smg_reload.mp3` (shared with P90 - one clean shared file, intentional) |
| Glock 19 | `FireSounds` | `Pistol/Shoot/pistol_shoot.wav` - **still the old placeholder**, no dedicated Glock recording has been supplied yet |
| Glock 19, Desert Eagle | `ReloadSounds` | `Pistol/Reload/pistol_reload.mp3` (shared) |
| Desert Eagle | `FireSounds` | `Pistol/Shoot/desert_eagle_shooting.mp3` |
| Shotgun | `FireSounds` / `ReloadSounds` | `Shotgun/Shoot/shotgun_shooting.mp3` / `Shotgun/Reload/shotgun_reload.mp3` |
| Karambit | `FireSounds` | `Melee/Swing/melee_swing.mp3` (unchanged, no new melee file was supplied) |
| Bazooka | `FireSounds` / `ReloadSounds` | `RPG/Shoot/rpg_shoot.wav` / `RPG/Reload/rpg_reload.mp3` |
| Bazooka | `ImpactSounds`, `ImpactUsesSurfaceVariation = false` | `RPG/Impact/rpg_explosion.wav` |
| all except Bazooka | `ImpactSounds` | `Pistol/Impact/Impact-00#.wav` - fallback only, see "Surface-varied bullet impacts" below |
| AK-47, M4, P90, Kriss Vector, Glock 19, Desert Eagle, Shotgun, Bazooka | `EmptySound` | `Common/empty_click.mp3` (shared). Karambit has none - melee never hits the dry-fire branch. |

**Not wired to any live gameplay code yet**: `RPG/Extras/rpg_doppler_effect.wav`. There is no
in-flight-projectile audio system for the Bazooka (no flyby-style effect fires while a rocket is
travelling), so this file is sourced and sitting in place for whenever that lands - same status
the old `_unused`-suffixed RPG files had before this pass.

**Parked, not used by any weapon**: `SMG/Shoot/mp5_shooting_unused.mp3`. The MP5 first-person
viewmodel was removed from the project this session and is not one of the 9 roster weapons, so
this file isn't referenced anywhere. Kept in case an MP5 gets added back later.

**Shared "hit a person" sound**, not per-weapon - `scripts/player/WeaponAttachment.cs`,
`FleshImpactSound` constant: `Melee/Impact/flesh_hit.mp3` (unchanged, no new recording supplied).

## Player audio - `scripts/player/PlayerAudio.cs`

| Sound | File | Plays when |
|---|---|---|
| Jump | `Movement/PlayerJump.wav` | Take-off, on the transition into the Jump state. |
| Crouch | `Movement/PlayerCrouch.wav` | Once, on the transition into Crouch or CrouchWalk. |
| Walk loop | `Movement/PlayerWalkLoop.wav` | Quiet, looping, under the discrete footstep clicks while Walk or CrouchWalk. |
| Sprint loop | `Movement/PlayerSprintLoop.wav` | Same, while Sprint. |
| Hit reaction | `Hit Dead And Respawn/PlayerHit.wav` | Whenever this player takes damage and survives. Was previously a random pick between two clips - collapsed to one (see rebuild note above). |
| Low-health breathing | `Low Health/LowHealthBreathing.wav` | Looping, local player only, non-positional - starts at ≤30% health, stops on recovery or death. |
| Clean death | `Hit Dead And Respawn/PlayerDead.wav` (unchanged) | Headshot or a single 100+ damage hit - **unless** melee, or the victim was already ≤20% health. |
| Struggling death | `Hit Dead And Respawn/PlayerDeathFinisher.wav` | Everything else: a non-instant body-shot kill, any melee kill, or finishing off someone already critical. This is the "throat struggling" file from this pass's upload. |

The instant-vs-struggling decision is made once, on the victim's own device, in
`Health.DetermineIfDeathStruggles`, and rides to every peer through `KillFeed.KillAnnounced`. It is
already correctly 3D positional (`AudioStreamPlayer3D` on the `Voice` bus) - "heard by anyone
nearby" falls out of ordinary distance falloff, no proximity code needed. This is intentionally
**not** the same thing as a "medic needed" callout - that is a separate, not-yet-built comms/voice
line system; this is purely the ambient sound of the death itself.

## Shooter feedback - `scripts/player/WeaponSwitcher.cs`

Hit marker unchanged - still `TargetHit-00#.wav`, owner-only, non-positional.

**Surface-varied bullet impacts, ricochet, and flyby**, all in
`res://audio/Clips/Weapons/Impacts/Surfaces/` (all 7 files refreshed this pass with the new
recordings, same paths, same classification logic in `scripts/combat/SurfaceType.cs` - unchanged):

| Sound | File |
|---|---|
| Metal (heavy) | `BulletMetalHeavy.mp3` |
| Metal (thick) | `BulletMetalThick.mp3` |
| Wood | `BulletWood.mp3` |
| Glass | `BulletGlassHit.wav` |
| Wall / generic | `BulletWallHit.wav` |
| Ricochet | `BulletRicochet.wav` |
| Bullet flyby | `BulletFlyBy.wav` |

## Menu music - `scripts/network/LanMenu.cs`

`res://audio/Clips/UI and HUD/Menu Music/` - `TacticalSilence.mp3` and `TacticalSilenceV2.mp3`,
both refreshed this pass with the new recordings at the same paths. Playback logic unchanged.

## Unused, still in the project

`CountdownBeep.wav` - no round timer exists yet.

## Still worth sourcing

- A dedicated Glock 19 fire sound (currently the old placeholder `pistol_shoot.wav`).
- A landing thud, a slide sound (Slide is still a silent movement state).
- Real per-surface impact accuracy still needs actual material tags on map geometry - see
  `SurfaceClassifier`'s own doc comment.
- The separate "medic needed" comms/voice-line callout - distinct from the struggling death sound,
  not built here on purpose.
- An in-flight audio system for the Bazooka's rocket, to make use of `rpg_doppler_effect.wav`.
