# Merge report — 2026-09-27

You gave me 7 zips. This is what each one turned out to be, and exactly what I did to bring them
together into one project that opens clean in Godot.

## What each zip actually was

| Zip | Status before I started |
|---|---|
| `wraith_run_godot.zip` | Your real, current project. Already had the character system, sound system files, settings/mobile controls, and the Team Deathmatch patch fully merged in. |
| `wraith_run_team_deathmatch_patch.zip` | Already fully applied to the project above. Nothing to do. |
| `files.zip` (sound system) | Already fully applied — word-for-word identical to what's in the main project. Nothing to do. |
| `wraith_run_changes.zip` | **Not applied.** Fixed two real bugs and added 2 maps. |
| `wraith_run_mp5_viewmodel_patch.zip` | **Not applied.** First-person viewmodel arms + reserve ammo. |
| `wraith_run_hud_update.zip` | **Half-applied.** Its data changes made it in; its actual HUD code never did. I left the older, simpler HUD in place — see "What I deliberately did NOT do" below. |
| `gun_sounds.zip` | New raw audio, not wired into anything yet. |

## The project would not compile

`SoundManager.cs` and `WeaponSoundTuner.cs` (already in your project) referenced six properties on
`WeaponData` — `FireVolumeDb`, `FireMaxDistanceMeters`, `FireEmissionAngleDegrees`,
`FireEmissionOffAxisAttenuationDb`, `FireCanDuckMovement`, `FireReportDelayEnabled` — that were
never actually added to that class. That's a hard compile error (CS1061) on the whole project, not
a gameplay bug. I added the six properties and wired real values for all 9 weapons using the table
from `files.zip`'s own README.

On top of that, `SoundManager` wasn't registered as an autoload in `project.godot`, so even once it
compiled, `SoundManager.Instance` would have been null. `WeaponAttachment.PlayFire()` also never
actually called into `SoundManager` — it just played the clip directly with a fixed bus/volume, so
the whole distance-falloff/muzzle-direction/report-delay system was dead code. All three are fixed
now: the autoload is registered, and `PlayFire()` routes through `SoundManager.Instance.PlayWeaponFire`.

## Other things that existed but were never actually turned on

Same pattern, three more times:

- **`CharacterLoadout`** (operator perks/drawbacks) was a fully-written script with a real
  `.uid`, referenced by `Health.cs`, but was never added as a node in `Player.tscn`. So every
  `GetNodeOrNull<CharacterLoadout>(...)` in the project was silently returning null — no operator
  perk ever did anything. Added as the scene's first child (needs to ready before the siblings that
  read it).
- **Reserve ammo** (`MaxReserveAmmo` on `WeaponData`) existed in the data but `WeaponSwitcher.cs`
  never read it — reloading always just topped the mag back up for free. Now wired, using the more
  complete of the two versions I found across your patches (the one that also handles the shotgun's
  shell-by-shell reload correctly).
- **`WeaponSoundTuner` (F6)** and **`AnimationTuner` (F8)** are both real, working debug tools that
  were never added as nodes anywhere in `Player.tscn`, so neither key would have done anything.
  Both are now wired in next to the existing `WeaponGripTuner` (F3). Small bonus fix while I was in
  there: `AnimationTuner` reads/writes `WalkSpeed`/`SprintSpeed`/`CrouchSpeed` by name through
  Godot's property reflection, which only works on real `[Export]` properties — they were `const`,
  so even a wired-up tuner would have failed on those fields specifically. Converting them to
  `[Export]` (needed anyway for the operator move-speed perk) fixes that too.
- **Kriss Vector's model** — `art/weapons/SMGs/KRISS VECTOR.glb` exists in your project (30 MB,
  added 2026-09-23), but `WeaponRegistry.tres` still had `ModelScenePath = ""` for it, left over
  from when the mesh genuinely didn't survive the FBX→GLB conversion. It's pointed at the file now.
  I did **not** add Kriss Vector to `WeaponSwitcher.cs`'s default 4-weapon test loadout — I can't
  verify the grip/hand orientation without opening the editor, so that's a one-line change you can
  make yourself once you've eyeballed it (see "worth checking" below).

## The actual merges

**`WeaponSwitcher.cs`** — the messiest one. Your current version already had Team Deathmatch's
friendly-fire checks and controller weapon-cycling. `mp5`'s version had reserve ammo and viewmodel
hooks but predated TDM. `changes`' version had the CharacterLoadout recoil scaling. All three are
now in one file: TDM's checks stayed as-is, reserve ammo and viewmodel calls came from `mp5`,
recoil scaling came from `changes`.

**`WeaponAttachment.cs`** — merged `mp5`'s viewmodel support (a second, hidden copy of this class
runs on the first-person arms model, aimed by `HandBoneFragment`/`UseViewmodelGrip`/`SkipAudio`)
with the sound-bus routing your project already had, plus the `SoundManager` wiring above and the
one-line 90° rotation fix so the directional muzzle cone actually points down the barrel instead
of out the grip's original axis.

**`Player.tscn`** — added `CharacterLoadout` (first child), `WeaponSoundTuner` and `AnimationTuner`
(siblings of `WeaponGripTuner`), and the MP5 viewmodel + its own nested `WeaponAttachment` under
the first-person camera. Kept your current hand-tuned camera transform rather than reverting to
the older value baked into two of the patches. I ran the `.tscn` validator from
`wraith_run_changes.zip`'s own test harness against the merged scene and every map — all pass:
every resource reference resolves, every property assigned on a node is a real `[Export]` on that
node's script.

**`PlayerMovement.cs`** — `WalkSpeed`/`SprintSpeed`/`CrouchSpeed` are now `[Export]` instead of
`const`, and get scaled once in `_Ready()` by the operator's `MoveSpeedMult`/`CrouchWalkSpeedMult`
if one is equipped.

**Two new maps** — `Ashfields.tscn` and `Depot.tscn` copied in as-is; they didn't touch anything
that already existed.

## Sounds

Everything from `gun_sounds.zip` is sorted into `audio/Clips/Weapons/` (new `SMG/`, `RPG/`, and
`Common/` subfolders) and wired into `WeaponRegistry.tres`:

- **AK-47, M4** — added `ak47 shooting.mp3` / `m4 shooting.mp3`. M4 was previously pointing at
  `Rifle_Shoot-002.wav`/`Rifle_Shoot-003.wav` and `rifle_reload.wav`, none of which exist on disk —
  it would have played nothing on fire. Fixed.
- **P90, Kriss Vector** — were both using the same generic rifle placeholder sound (also including
  the two nonexistent files above). Now each has its own dedicated SMG fire + reload sound.
- **Desert Eagle** — was sharing the exact same fire clip as Glock 19. Now has its own.
- **Shotgun** — reload was pointing at `shotgun_reload_shell.wav`, which never existed (the real
  file is `Shotgun_reload.wav`, no "_shell"). Fixed, plus added the new recording as a second
  variant.
- **Bazooka** — its impact sound was the generic pistol bullet-impact array. Pointed it at the
  `Grenade Explode-00{1,2,3}.wav` files that already existed in your project but were never
  referenced by anything. Also added the new RPG-specific fire/reload sounds as extra variants
  alongside the existing Grenade Launcher ones.
- **All 9 weapons** — replaced the shared `reload_click_rifle.wav` dry-fire click with the new
  dedicated `empty click sound.mp3`, and added the six audio-realism fields per weapon using the
  values `files.zip`'s README derived (distance, directional cone, footstep-ducking, report delay).
- **Two files not wired to anything**: `RPG distaction.wav` and `RPG doppler effect.wav` are real
  recordings (sound like a rocket flyby/whoosh) but there's no "projectile flyby" audio slot on
  `WeaponData` for them to go in — building that would be a new feature, not a fix. They're saved
  at `audio/Clips/Weapons/RPG/Extras/` for whenever you want to add that.
- One more unused file: `H$K mp5 shooting.mp3` — stored at
  `audio/Clips/Weapons/SMG/Shoot/mp5_shooting_unused.mp3`. The MP5 model in this patch is a
  viewmodel-only prop (the hands you see holding whatever gun is actually equipped), not a 9th
  loadout weapon, so there's nowhere for this to plug in today either.

## What I deliberately did NOT do

`wraith_run_hud_update.zip`'s actual HUD code (the richer two-tier tactical HUD with kill-style
bonus popups) is still sitting unused — I didn't merge it. Your current `PlayerHud.cs` is the
simpler HUD with Team Deathmatch's additions on top, and merging in a 519-line HUD rewrite on top
of that would have been a much bigger, riskier change than anything else here, and it's a genuine
design decision (do you want that HUD?) rather than a bug fix. Say the word and I'll do that merge
next.

## Worth checking once you open it in Godot

- First launch will take a minute — every new asset (the mp3s, the mp5 arms glb, the two maps)
  needs Godot's editor to import it for the first time.
- MP5 viewmodel grip position/rotation (`WeaponData.ViewmodelGripPosition/RotationDegrees`) is at
  its default (0,0,0) for every weapon — there's no F-key tuner for it yet (unlike the world-model
  grip's F3 tool), so it may look off-hand until you nudge those two fields per weapon.
- Kriss Vector's model is now wired up but untested — check it doesn't T-pose or clip through the
  hand before adding it back to `WeaponSwitcher.cs`'s test loadout.
- The AK-47 sound-realism numbers (from `files.zip`'s own derivation) have it carrying *less* far
  than the Glock — that's flagged in that README as a real finding from the numbers you gave it,
  not a mistake, and worth A/B-ing in the F6 tuner before you decide it's right for the game.
