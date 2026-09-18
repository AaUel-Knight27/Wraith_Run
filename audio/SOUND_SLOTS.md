# Sound slots

Every sound the game plays, where it is read from, and what belongs there. Nothing here is
hardcoded in C# any more: weapon audio lives in `res://scripts/data/WeaponRegistry.tres`, the
rest is a `const string` at the top of the script named in the last column.

Two ways to replace a sound:

1. **Drop a file over the existing path.** Zero edits. Keep the filename.
2. **Point the string at a new path.** For weapons that is a field in the registry (editable in
   the inspector); for the others it is one constant in one script.

Format: 16-bit WAV, 44.1 kHz, mono for anything positional (3D panning needs mono - a stereo
file gets collapsed and you lose the punch). Trim the leading silence, or every shot will feel
late.

---

## Weapon audio - `scripts/data/WeaponRegistry.tres`

| Weapon | Field | Currently | Wants |
|---|---|---|---|
| AK-47 | `FireSounds` | Rifle_Shoot-001/002/003 | 3-4 AK cracks, close-mic, with a tail |
| AK-47 | `ReloadSounds` | rifle_reload | mag out / mag in / bolt |
| M4 | `FireSounds` | Rifle_Shoot-001/002/003 *(shared with AK)* | its own report - flatter, faster than the AK |
| M4 | `ReloadSounds` | rifle_reload *(shared)* | AR-15 mag change |
| P90 | `FireSounds` | Rifle_Shoot *(placeholder)* | SMG, higher pitch, tighter tail |
| Kriss Vector | `FireSounds` | Rifle_Shoot *(placeholder)* | fast SMG |
| Glock 19 | `FireSounds` | Pistol_Shoot-001/002/003 | fine as is |
| Glock 19 | `ReloadSounds` | Pistol_Reload | fine as is |
| Desert Eagle | `FireSounds` | Pistol_Shoot *(shared)* | its own - much bigger, .50 AE |
| Shotgun | `FireSounds` | Shotgun_Shoot-001/002/003 | fine as is |
| Shotgun | `ReloadSounds` | Shotgun_reload 1 | **one shell**, not a full reload - it now plays once per shell |
| Karambit | `FireSounds` | *(empty)* | knife swoosh + flesh hit |
| Bazooka | `FireSounds` | Grenade Launcher-001/002/003 | rocket launch whoosh |
| all | `ImpactSounds` | Impact-001..006 | surface-varied impacts |
| all | `EmptySound` | *(empty)* | dry-fire click - this plays on an empty mag and is currently silent |

`FireAudioUnitSize` per weapon sets how far the shot carries: Bazooka 26 m, shotgun 18, AK/M4 16,
Desert Eagle 14, P90/Kriss 13, Glock 10, Karambit 6.

## Player audio - `scripts/player/PlayerAudio.cs`

| Slot | Constant | Currently |
|---|---|---|
| Footsteps (9 clips) | `FootstepDirectory` | Player_Footstep_01..09 |
| Taking damage | `HitSound` | PlayerHit.wav |
| Death | `DeadSound` | PlayerDead.wav |
| Respawn | `RespawnSound` | PlayerRespawn.wav |

Footstep cadence is in the same file: walk 0.48 s, sprint 0.34 s, crouch-walk 0.72 s.

Not wired yet, and worth adding when you have clips: landing thump, slide scrape, jump grunt,
and per-surface footsteps (the map is concrete, metal container tops and dirt - one footstep set
covers all three right now).

## Shooter feedback - `scripts/player/WeaponSwitcher.cs`

| Slot | Constant | Currently |
|---|---|---|
| Hit marker | `HitMarkerSounds` | TargetHit-001..005 |

Non-positional and owner-only: it is feedback about your own shot, so nobody else hears it.

## Unused, still in the project

`CountdownBeep.wav` - no round timer exists yet.

---

## Where to get replacements

- **Sonniss GDC bundle** (gdc.sonniss.com) - the big one. Royalty-free for commercial use, no
  attribution, and every past year is still downloadable. Best single source for weapons.
- **Freesound**, with the licence filter set to CC0 - large, free account needed to download.
- **Kenney** (kenney.nl) - CC0, no sign-up. Good for UI and impacts, not for guns.

Licence check before you ship: CC0 needs no credit, CC-BY needs a credit line, CC-BY-NC cannot
go in anything you sell.
