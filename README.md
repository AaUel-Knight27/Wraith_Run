# Full quality_audio swap + bug fixes

Every old sound is now replaced with something from your quality_audio pack, except four files
nothing in the pack can replace (listed below) - those are deliberately untouched. Pulled from your
GitHub `main` branch (Godot 4.7.2 .NET); if your local checkout has diverged, diff before
overwriting rather than copying blind - especially `project.godot` and
`scripts/data/WeaponRegistry.tres`, see their notes below.

## How to apply

1. Copy every file below into the matching path in your checkout, overwriting what's there.
2. Run `cleanup_old_audio.sh` from your repo root (`./cleanup_old_audio.sh`). It deletes
   `audio/Clips/` (keeping the four files still genuinely referenced) and a stale duplicate weapon
   registry this pass turned up - full explanation in the script's own header comment - then
   deletes itself. Safe to re-run.
3. Open the project once in the Godot editor so it imports the new files under `audio/SFX/`.

## New files

- **`scripts/autoloads/Audio.cs`** + **`scripts/autoloads/SoundBank.cs`** - the general-purpose
  sound-bank autoload, adapted from the scripts in your zip. Scans `audio/SFX/` at startup and
  turns every file into a playable id automatically - drop a new clip in later, no code changes.
  Fixed a path-casing bug from the original pack (`res://Audio/SFX`, capital A, silently finds
  nothing on Linux/exported builds since `res://` is case-sensitive there) and rewired bus routing
  onto your actual mixer (`default_bus_layout.tres`) instead of two redundant new buses - footsteps
  land on `Movement` and keep the gunfire-ducking you already built. This is a separate system from
  `SoundManager.cs`, which keeps doing the bespoke physics for a weapon's own gunshot report
  (falloff, occlusion, the crack/boom delay) - this one is for everything else.

## Every old sound's replacement

**Player (`scripts/player/PlayerAudio.cs`)** - rewritten. The walk/sprint loop system is gone
entirely: the pack has no multi-second pre-mixed loop file to replace
`PlayerWalkLoop.wav`/`PlayerSprintLoop.wav` with, only discrete one-shot steps, so footsteps are now
handled solely by the surface-aware one-shot system (already in place from the ADS/Tab work) at the
same cadence the old per-step fallback used. That made the OLD per-step fallback redundant too (it
only ever ran when the loops were missing, which is now always - leaving both in would have doubled
every footstep), so it's removed as well, along with everything that only existed to feed it.
  - Hit grunt -> `impacts/blood_01/02/03.wav` (3 variants, was 1)
  - Death struggle -> `voice/male/man_down.wav`
  - Respawn -> `voice/male/relief.wav`
  - Jump -> `player/low_hop.wav` (closest thing to a take-off sound the pack ships)
  - Landing -> now only the surface-specific `surfaces/{material}/land` from the ADS pass; the old
    separate generic landing thud is retired rather than stacked under it
  - Low-health breathing -> **left on the old file** (`LowHealthBreathing.wav`). No breathing-loop
    clip anywhere in the pack.
  - Crouch -> **dropped, no replacement.** Nothing in the pack reads as a crouch-settle sound; I'd
    rather leave it silent than reuse something that doesn't fit. Say the word if you find/add one.

**Weapon world sounds (`scripts/player/WeaponAttachment.cs`)**
  - Flesh impact (bullet/blade hitting a body) -> `impacts/blood_01/02/03.wav` (3 variants, rolled
    independently from the player's own hit-grunt pool above, so the two rarely match)
  - Metal impact -> `surfaces/metal/metal_impact.wav` (heavy) / `metal_impact01.wav` (thick)
  - Wood impact -> `surfaces/wood/wood_impact.wav`
  - Glass impact -> `surfaces/glass/impact 01.wav`
  - Generic wall impact -> `surfaces/concrete/concrete_impact.wav`
  - Bullet flyby (whip-crack of a close shot) -> `impacts/bullet_whiz_01/02/03.wav`
  - **Ricochet whine -> reuses the same bullet-whiz pool.** No distinct ricochet clip in the pack;
    both are a metallic zing past the ear, close enough, rolled independently of flyby.
  - **RPG doppler flyby -> left on the old file.** No RPG/Bazooka weapon exists in
    `WeaponRegistry.tres` yet for this to matter to, and the pack has no doppler/rocket-flyby
    equivalent to give it anyway if one gets added.
  - Also added `RollReloadVariant()` (mirrors the existing `RollFireVariant()`) so a weapon with
    more than one `ReloadSounds` entry actually alternates between them instead of only ever
    playing the first - matters for the shotgun now that its reload uses both `shell_in_01.wav` and
    `shell_in_02.wav` (it's called once per shell).

**Hit marker (`scripts/player/WeaponSwitcher.cs`)** -> `ui/hitmark.wav`.

**UI (`scripts/ui/UiSounds.cs`)** - the pack only ships three generic UI one-shots and nothing
error- or "go"-flavoured, so these are the least-bad picks rather than a real 1:1 swap:
  - Click -> `ui/scroll.wav`
  - Select -> `ui/notification.wav`
  - Start -> `gameplay/kill.wav` (a short, decisive stinger - reused for "the big go action" since
    nothing in the pack is purpose-built for it)
  - Error -> `misc/noise.wav`

**Menu music (`scripts/network/LanMenu.cs`) -> left on the old files.** The pack is sound effects
only, no music tracks - there's nothing to swap `TacticalSilence.mp3`/`TacticalSilenceV2.mp3` for.

**Weapon fire/reload (`scripts/data/WeaponRegistry.tres` - this is the file the game actually
loads, see its own note below)**

| Weapon | Fire | Reload | Note |
|---|---|---|---|
| AK-47 | `generic/fire_09/19/24.wav` | `shared/mag_in.wav` | pack's `ak/` folder has bolt sounds only, no fire clips |
| M4 | `m4/fire_01/02/03.wav` | `shared/mag_in.wav` | exact match |
| P90 | `mp7/fire_01/02/03.wav` | `mp7/mag_in.wav` | no "p90" in the pack; mp7 is the closest-class compact PDW |
| Kriss Vector | `mp5/fire_01/02/03.wav` | `mp5/mag_in.wav` | no "kriss"/"vector" in the pack; mp5 is the closest-class SMG |
| Glock 19 | `m1911/fire_01/02/03.wav` | `glock/mag_in.wav` | pack's `glock/` folder has bolt/mag but no fire clips; reload still uses the real Glock mag sound |
| FN 502 | `fiveseven/fire_01/02/03.wav` | `shared/mag_in.wav` | the pack's "fiveseven" is a real FN pistol - best brand match available |
| Desert Eagle | `deagle/fire_01/02/03.wav` | `shared/mag_in.wav` | exact match |
| Shotgun | `generic/fire_05/15/29.wav` | `benelli/shell_in_01.wav` + `shell_in_02.wav` (alternates) | the pack's own "benelli" folder is the same Benelli M4 this weapon's model already is, but has no fire clips - only the pump/shell sounds, which the reload now uses |
| Sniper | `kar98k/fire_01/02/03.wav` | `kar98k/mag_in.wav` | kar98k is the one bolt-action in the pack with real fire clips (r700 only has bolt/mag) |

Also, for every weapon: `EmptySound` (dry-fire click) -> `weapons/shared/bolt_open.wav` (no
explicit dry-click sound in the pack; a sharp mechanical click is the closest substitute) and
`ImpactSounds` -> `explosions/explosion.wav` (this field is only used by a weapon with
`ImpactUsesSurfaceVariation = false` - none of the current 9 do, so this is unused today, just
pointed at something sensible for whenever a launcher-type weapon uses it).

## Other things found and fixed along the way

- **`scripts/player/WeaponSwitcher.cs`** - one-line bounds-check fix, carried over from the ADS
  pass: the RPC validating an incoming surface-impact value was hardcoded to
  `surface > (int)SurfaceType.Glass`; now bounds against the real top of the enum.
- **`scripts/ui/HubTablet.cs`** - the Tab/Esc bug from before (Tab didn't open the tablet from
  closed, only Escape did).
- **A stale, unused duplicate weapon registry under `data/weapons/`** - found while tracing every
  sound reference in the project to make sure nothing was missed. The game actually loads
  `scripts/data/WeaponRegistry.tres` (confirmed via `WeaponSwitcher.cs`'s `ResourceLoader.Load`
  call) - that's the file this delivery updates. `data/weapons/*.tres` and
  `data/weapons/WeaponRegistry.tres` are a second, older copy of the same 7-9 weapons that nothing
  in the project loads, with different and out-of-date stats from the live ones (e.g. AK-47
  `BodyDamage` is 34 there vs. 30 in the live file). `cleanup_old_audio.sh` removes these too -
  comment that section out in the script if you'd rather keep them as reference.
- `project.godot` - unchanged this pass (already has the `Audio` autoload line from before).

## Still pending: the scope-overlay/zoom system

Haven't started this yet - it's a genuinely separate feature from the audio work above
(render-to-texture scope view, a reticle per optic, FOV/zoom per weapon) and I didn't want to hold
up delivering the sound swap to get into it. Tell me how you want it scoped - e.g. is each image a
fixed optic for a specific weapon (which one(s)?), or a selectable attachment any weapon can equip -
and I'll build it next.
