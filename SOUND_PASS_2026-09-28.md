# Sound pass - 2026-09-28

What changed when `sound_effects.zip` was integrated. `audio/SOUND_SLOTS.md` has the full up-to-date
slot list; this is the narrative of the pass and the design decisions behind it - read it before
touching any of the new systems below, especially the death-sound logic.

I could not compile or run this in Godot - there's no engine or `dotnet` available in this
environment. Everything below is a careful read of the existing code plus consistent extension of
its own patterns, not a tested build. Test-before-trusting list is at the bottom.

## New files added

All under `res://audio/Clips/`:

- `Player/Movement/` - `PlayerJump.wav`, `PlayerCrouch.wav`, `PlayerWalkLoop.wav`, `PlayerSprintLoop.wav`
- `Player/Hit Dead And Respawn/` - `PlayerHit_02.wav`, `PlayerDeathFinisher.wav` (alongside the
  originals, which are untouched)
- `Player/Low Health/` - `LowHealthBreathing.wav`
- `Weapons/Impacts/Surfaces/` - `BulletMetalHeavy.mp3`, `BulletMetalThick.mp3`, `BulletWood.mp3`,
  `BulletGlassHit.wav`, `BulletWallHit.wav`, `BulletRicochet.wav`, `BulletFlyBy.wav`
- `UI and HUD/Menu Music/` - `TacticalSilence.mp3`, `TacticalSilenceV2.mp3`

## Code touched

`scripts/data/WeaponData.cs`, `scripts/data/WeaponRegistry.tres`, `scripts/player/WeaponAttachment.cs`,
`scripts/player/WeaponSwitcher.cs`, `scripts/player/PlayerMovement.cs`, `scripts/player/PlayerAudio.cs`
(rewritten), `scripts/player/Health.cs`, `scripts/combat/KillStyle.cs`, `scripts/autoloads/KillFeed.cs`,
`scripts/autoloads/MatchManager.cs`, `scripts/player/PlayerScore.cs`, `scripts/player/PlayerHud.cs`,
`scripts/network/LanMenu.cs`. New file: `scripts/combat/SurfaceType.cs`.

## The death-struggle system - read this one carefully

This was the trickiest piece, so it gets its own section rather than living only in code comments.

**The idea:** a person doesn't always drop the instant they're shot. A clean headshot or an
overwhelming hit does; a body wound, a knife, or finishing off someone already critical plays out
slower. `PlayerDead.wav` is the clean death; the new `PlayerDeathFinisher.wav` is the struggling one.
Exactly one plays per death - never both, never randomized between them.

**The rule**, in `Health.DetermineIfDeathStruggles`:

> Struggles **unless** it was a headshot or a single hit for 100+ damage - and even then, only if it
> *wasn't* melee, and the victim *wasn't* already at 20% health or below when the blow landed.

Spelled out:

| Killing blow | Struggles? |
|---|---|
| Headshot, not melee, victim was above 20% health | **No** - clean death |
| Single hit ≥100 damage, not a headshot, not melee, victim above 20% | **No** - clean death |
| Melee (Karambit), *any* circumstance | **Yes** - always, by design |
| A body-shot kill that wasn't headshot and didn't hit the 100-damage instant-kill line | **Yes** |
| Victim was already at ≤20% health, whatever the finishing blow was | **Yes** - overrides everything above |

That "victim already critical" case is deliberately an override: a headshot on someone already
nearly dead still plays the struggling clip, on the reasoning that someone that far gone doesn't go
quietly just because the last shot was clean.

**This is a distinct feature from the "medic needed" comms callout** mentioned when this was scoped.
That's a player-triggered voice line for a future communication system and doesn't exist yet. This
sound is purely ambient, tied to how the kill happened, and nobody has to press anything for it to
play.

**Why this needed new plumbing, not just a PlayerAudio.cs change:** the fact "was this a struggle"
is only knowable at the instant of the killing blow (headshot/melee/damage/the victim's own health a
moment earlier), and only the victim's own device can compute it (it's the authoritative side of
`ReceiveDamage`). But *every* peer's `PlayerAudio` needs to agree on which clip to play for that
player's death, including bystanders. Broadcasting a brand-new, separately-timed network message for
this risked a race against the already-separate `CurrentHealth` replication that `PlayerAudio`
polls to detect the death in the first place - two different sync mechanisms with no guaranteed
ordering between them.

Instead this piggybacks on `KillFeed.KillAnnounced` - the kill-feed broadcast that already reliably
reaches every peer, exactly once, for every kill in the match (that's how the on-screen kill feed
and scoring already work). One `bool struggles` parameter was added to the end of it. `PlayerAudio`
subscribes to the same signal three other scripts already do (`MatchManager`, `PlayerScore`,
`PlayerHud`) and, when the death is its own player's, stashes the flag until the next time it
notices the alive-to-dead edge locally.

Net effect: `KillAnnouncedEventHandler`'s signature gained a parameter, so every existing subscriber
had its handler signature updated to match (mechanical - none of them use the new value except
`PlayerAudio`). `KillStyle` gained a `Melee` flag, carried through `styleFlags` exactly like
`Headshot` already was - it doesn't affect scoring (`KillStyleBonus.Evaluate` never checks it), it's
only there so the victim's device can tell a blade kill apart from a bullet kill.

## Bullet surface impacts, ricochet, flyby

New `SurfaceType` enum (Generic/Metal/Wood/Glass) and `SurfaceClassifier` in
`scripts/combat/SurfaceType.cs`. **Please read the honest-limitation note in that file** - the map
kit's procedural geometry (all of Depot and Ashfields) has no per-material
physics tagging at all once `MeshBatcher` merges it, so classification is a best-effort node-name
guess, upgradable per-prop via three new Godot groups (`surface_metal`/`surface_wood`/`surface_glass`)
whenever you want to tag something precisely. The Prison interior uses individually named scene
objects, while Depot and Ashfields still rely on procedural map geometry. Add the surface groups to discrete props when precise impact materials are
needed; otherwise hits use the generic wall sound.

A geometry impact (a shot hitting a wall/crate/container, not a person) now **broadcasts** to every
peer instead of only playing on the shooter's own device - previously only the shooter ever heard a
bullet hit a wall. Bazooka is explicitly opted out of surface variation
(`ImpactUsesSurfaceVariation = false` in `WeaponRegistry.tres`) so its explosion sound is untouched.

Bullet flyby is a new near-miss check: for every non-melee shot, every other living player is tested
against the shot's actual path (muzzle to wherever it stopped), and a "whiz" plays near anyone the
ray passed within ~1.4m of, skipping whoever was actually hit and skipping anything closer than 2.5m
of the muzzle (a point-blank near-miss doesn't read as a "whiz"). This needed one small addition to
`PlayerMovement.cs`: every player is now added to a `"players"` Godot group in `_Ready()` (mirroring
the existing `"spawn_point"` group pattern) so this check has a way to find every other player on
the map at all.

## Menu music

Two tracks, played back-to-back in random order, starting when the main menu loads and stopping in
its one shared cleanup path (`RunTerminalCleanup`, which already covers both "leaving for a match"
and "closing the app"). `SettingsManager` was already applying `MusicVolume` to the `Music` bus in
anticipation of exactly this landing eventually, so the Audio settings slider works on it with no
further changes needed there.

## Things worth testing first, specifically

- **Jump sound firing once, not repeatedly, mid-air.** The detection is "entered the Jump state
  this frame, wasn't in it last frame" - should be a single clean trigger, but it's the one part of
  this pass most dependent on exact `MovementState` transition timing I couldn't step through myself.
- **Walk/sprint loop volume relative to footsteps.** These were tuned by ear-less guesswork
  (`VolumeDb = -11.0f`, footsteps at `-3.0f`) - listen for whether the loop is actually reading as
  quiet texture under the footsteps or competing with them.
- **The struggle-death rule against your own expectations**, using the table above - especially the
  "already critical" override, since that's the one condition that can flip a headshot from clean to
  struggling.
- **Low-health breathing threshold (30% of max health)** - not a number you specified, just a
  reasonable default; easy to change (`LowHealthThreshold` constant in `PlayerAudio.cs`).
- **Ricochet chance (22%)** - same story, an implementation-assumption constant
  (`RicochetChance` in `WeaponAttachment.cs`), not something you specified.
