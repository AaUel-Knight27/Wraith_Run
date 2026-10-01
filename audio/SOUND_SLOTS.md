# Sound slots - what plays where (as wired 2026-09-30)

Paths are under `audio/Clips/`. Source names are the files as supplied in `audio.zip`.

## Weapons  (`Weapons/`)
| Weapon | Fire | Reload | Empty |
|---|---|---|---|
| AK-47 | Rifle/Shoot/ak47_shooting.mp3 | Rifle/Reload/ar_reload.mp3 | Common/empty_click.mp3 |
| M4 | Rifle/Shoot/m4_shooting.mp3 | Rifle/Reload/ar_reload.mp3 | same |
| P90 | SMG/Shoot/p90_shooting.mp3 | SMG/Reload/smg_reload.mp3 | same |
| Kriss Vector | SMG/Shoot/kriss_vector_shooting.mp3 | SMG/Reload/smg_reload.mp3 | same |
| Glock 19 | Pistol/Shoot/glock_19.wav (user-supplied) | Pistol/Reload/pistol_reload.mp3 | same |
| Desert Eagle | Pistol/Shoot/desert_eagle_shooting.mp3 | Pistol/Reload/pistol_reload.mp3 | same |
| Shotgun | Shotgun/Shoot/shotgun_shooting.mp3 | Shotgun/Reload/shotgun_reload.mp3 | same |
| Karambit | Weapons/Melee/knife_whoosh.wav (user-supplied) | - | - |
| Bazooka | RPG/Shoot/rpg_shoot.wav | RPG/Reload/rpg_reload.mp3 | same |

Bazooka extras: `RPG/Impact/rpg_explosion.wav` (source "RPG distaction") plays wherever a rocket
lands, on any surface or on a player. `RPG/Extras/rpg_doppler_effect.wav` is the whoosh heard by
anyone a rocket passes within 9 m of.
`SMG/Shoot/mp5_shooting_unused.mp3` - no MP5 in the roster, so it is parked, not wired.

## Bullet impacts  (`Weapons/Impacts/Surfaces/`)
Chosen by the surface's group (`surface_metal` / `surface_wood` / `surface_glass`, see SurfaceType.cs);
anything else is a plain wall. Metal plays Heavy for weapons with BodyDamage >= 30 (AK, M4, Deagle),
Thick for the rest. Broadcast to every peer, max 2 per trigger pull (so shotgun pellets don't smear).
- BulletMetalHeavy / BulletMetalThick / BulletWood / BulletGlassHit / BulletWallHit
- BulletRicochet: layered on metal/wall hits - ~60% on a glancing hit, ~12% metal / ~4% wall head-on. Never wood/glass/shotgun/bazooka.
- bullet_flyby.wav (user-supplied whoosh): near-miss sound for a listener a single-projectile shot passes within 3 m of.

## Player  (`Player/`)
- Movement/PlayerWalkLoop.wav (walk; also crouch-walk, quieter and slower). Restarted at 1.2 s - the clip is 2.0 s but its last 0.8 s is silence.
- Movement/PlayerSprintLoop.wav (sprint), Movement/PlayerJump.wav (take-off), Movement/PlayerCrouch.wav (going into a crouch)
- Movement/jump_landing.mp3 plays on the replicated airborne-to-grounded transition (Jump/Fall to a grounded state).
- Hit Dead And Respawn/PlayerHit.wav (took a non-lethal hit), PlayerDeathFinisher.wav (the struggle)
- Hit Dead And Respawn/PlayerRespawn.wav is the user-supplied respawn cue.
- PlayerMedicNeeded.wav is retained as a future-use asset and is not loaded or played in the current game.
- Low Health/LowHealthBreathing.wav (local player only, under 30% health)
- The old per-step footstep clips are only used if the walk/sprint loops are missing.

## Death sound rules
- Headshot or Bazooka hit -> instant death, NO sound for anyone.
- Any other kill (body shot, any melee kill) -> struggling sound, positional (audible within ~25 m, loud inside ~6 m).
- Deathmatch: struggle plays at once. No medic call.
- Team Deathmatch, non-instant death: silent bleed-out for `Health.TeamBleedOutSeconds`, then struggle -> 3 s -> respawn.

Bots follow the same death/struggle rules. Bot shots produce flyby whizzes for the human listener.
Bots never get low-health breathing.

## Menu  (`UI and HUD/`)
- Menu Music/TacticalSilence.mp3 + TacticalSilenceV2.mp3 - both, alternating, starting on a random one.
- Menu Sounds/StartClick (PLAY, START HOSTING, joining a game), SelectSound (mode/map/list/toggle choices),
  InterfaceClick (every other button, and Escape/back), ErrorSound (could not host / connect / listen).
  Played by the `UiSounds` autoload on the `UI` bus.

## Current fallback clips

- Generic impact uses Impacts/Surfaces/BulletWallHit.wav.
- Bazooka keeps its explosion sound for every impact; it does not use surface variations.
- Local hit marker uses Impacts/Feedback/hit_marker.mp3 (user-supplied).
- Flesh impact uses Impacts/Feedback/flesh_hit.wav (user-supplied), separate from the player's hurt voice.

The supplied Opus-in-Ogg Glock, flesh, knife and whoosh recordings are decoded to PCM WAV for Godot 4.7 compatibility. The original downloads are unchanged.

The master development plan also describes future audio systems (combat music, vehicle engine pitch,
environment-specific footsteps, collapse/cursed-event sounds, and kill-feed/low-health HUD cues).
Those are not implemented in the current sound-slot inventory; see AUDIO_MISSING_ASSETS.md.
