# Third pass: the three log errors, the weapon system, audio, and a real map

Extract over the project root. Let Godot reimport on open.

Touched: `scripts/data/WeaponData.cs`, `scripts/data/WeaponRegistry.tres`,
`scripts/player/WeaponAttachment.cs`, `scripts/player/WeaponSwitcher.cs`,
`scripts/player/PlayerAnimationController.cs`, `scripts/player/PlayerMovement.cs`,
`scripts/player/Health.cs`, `scripts/network/GameWorld.cs`,
`scripts/network/PlayerNetworkSynchronizer.cs`, `scenes/Player.tscn`,
`scenes/GameWorld.tscn`, `data/weapons/*.tres`.
New: `scripts/player/PlayerAudio.cs`, `scenes/maps/Warzone.tscn`, `audio/SOUND_SLOTS.md`.

---

## A. The three errors in your log

### 1. `combat_jump_loop` not found

The clip really is called `combat_jump_loop` inside
`player animation combat.glb` — I checked the file. Godot renames it on import: a trailing
`_loop` is a **name suffix** (`nodes/use_name_suffixes` is on), so the importer turns looping
on and strips the suffix, leaving `combat_jump`. That is why your runtime dump lists
`combat_jump` and no `_loop` anything.

Fixed, and the state table now says so in a comment. The lesson generalises: for this project
the runtime clip dump is the source of truth, never the raw glb.

### 2. `Weapon attachment could not load .../AK47.fbx`

You converted the weapon FBXs to GLB, but `WeaponRegistry.tres` still pointed at the old
`.fbx` paths. Every path is now `.glb` and matches a file that exists — I verified all nine
against the filesystem.

One casualty: **there is no Kriss Vector model any more.** Only a stray `KRISS VECTOR.mtl`
survived the conversion; the mesh is gone. Its `ModelScenePath` is now empty and
`WeaponAttachment` treats "no model" as a warning rather than an error — the weapon still
fires, reloads, damages and makes noise, it just has nothing in hand. Re-export it when you
can.

There was also a second, unused copy of the weapon data in `data/weapons/*.tres` with the same
stale paths. Nothing loads it (`WeaponSwitcher` and `WeaponAttachment` both read
`res://scripts/data/WeaponRegistry.tres`), but I fixed its paths too so it can't bite later.
You should delete one of the two copies — two sources of truth for weapon stats will diverge.

### 3. `on_replication_start: ... ERR_UNCONFIGURED`

`PlayerNetworkSynchronizer` built the `SceneReplicationConfig` in `_Ready`. `MultiplayerSpawner`
starts replication for a spawned node **before** that node's children reach `_Ready`, so the
config was always one frame late and the engine logged this on every single spawn.

The config now lives in `Player.tscn` as a real `SceneReplicationConfig` sub-resource, which
exists the moment the scene is instantiated. `PlayerNetworkSynchronizer` stays as a guard: if
the scene's config ever goes missing it rebuilds the same list and warns, rather than leaving
players silently unsynchronized.

---

## B. Weapon system

### Every weapon was the wrong size and pointing the wrong way

The models are wildly inconsistent. Measured lengths of their longest axis, against the real
weapon:

| Model | Measured | Real | Factor |
|---|---|---|---|
| AK47.glb | 0.883 m | 0.88 | 0.993 |
| M4.glb | 0.721 | 0.84 | 1.165 |
| Glock 19.glb | 0.411 | 0.187 | 0.455 |
| Desert Eagle.glb | 0.940 | 0.27 | 0.287 |
| shootgun mossberg.glb | 13.834 | 1.01 | 0.073 |
| p90_-_stargate.glb | 19.722 | 0.50 | 0.025 |
| RPG.glb | 2.717 | 0.95 | 0.350 |
| Karambit.glb | 0.277 | 0.20 | 0.721 |

They also disagree about which axis the barrel runs down — AK and Deagle use +Z, everything
else uses ±X.

`WeaponData` now carries `ModelScale`, `GripPosition`, `GripRotationDegrees` and
`MuzzleDistance`, and the registry ships the values above. The rotations aren't guesses: I
sampled `combat_ads_idle` at t=0 and computed the right-hand bone's basis in the aiming pose.
In that pose the hand bone's local **+Y is the line of fire** and its local **+X is up**, so
each weapon gets the rotation that maps its own barrel axis onto +Y.

These are derived, not eyeballed. Expect to nudge them — they're `[Export]`s, and a weapon
re-reads them on equip, so pressing its number key again applies an inspector change without a
restart.

### Other weapon changes

- **The shotgun is a shotgun.** 8 pellets in a 4.5-degree cone, uniform over the disc so the
  pattern doesn't clump. Body damage dropped 70 → 14 *per pellet* (8 × 14 = 112, still a
  one-shot at point blank, but it falls off as the pattern opens).
- **Shell-by-shell reload.** `ShellByShellReload` is on for the shotgun: `ReloadTime` becomes
  the per-shell time (0.5 s), it loads one at a time, and pulling the trigger mid-tube
  interrupts it and keeps whatever's loaded.
- **Recoil exists.** `RecoilVertical` / `RecoilHorizontal` / `RecoilRecoveryMs` were defined and
  unused. The kick now lands on the Head pivot, not just the camera, so it moves the real point
  of aim — the hitscan ray comes off the camera, which is a child of Head. ADS halves it.
- **Dry fire.** Empty mag gives a click and a 0.35 s rate limit instead of silently spamming.
- **Muzzle flash.** A billboard quad plus a short-lived omni light at the muzzle, re-rolled per
  shot so a burst doesn't look like one frozen sprite.
- **Hip-fire bonus was broken.** `CaptureKillStyle` tested `State == MovementState.Aim`, but Aim
  stopped being a movement state in the last pass. It read false whenever you aimed while
  moving, so careful ADS shots were being scored as hip-fire. Now uses `IsAiming`.

### Networking: you couldn't see or hear other people fight

Three separate holes, all fixed:

- `PlayFire()` only ever ran inside the authority's own `_Process`, so **nobody heard anyone
  else shoot.** Fire effects are now an unreliable broadcast RPC (`CallLocal`, so the shooter
  runs the same path). Reload is the reliable equivalent.
- `Equip` only ran locally, so **every remote player appeared to be holding the default AK**
  regardless of what they'd switched to. The weapon is now a replicated property
  (`ReplicatedWeaponIndex`), not an event — which also means a peer joining mid-match sees the
  right gun in everyone's hands.
- Impacts and hit markers are local to the shooter, which is correct for the hit marker and a
  known simplification for impacts. Noted below.

### Player animation around the weapon

The AnimationTree root is now a blend tree, not a bare state machine:

    Locomotion (state machine) -> Fire (one-shot) -> Reload (one-shot) -> output

Both one-shots are **filtered to the upper body** — spine, neck, head, shoulders, arms, hands,
fingers, matched by substring against the real bone names — so firing and reloading play over
whatever the legs are doing instead of replacing it. That's the difference between shooting
while running and freezing mid-stride to shoot.

If either combat clip is missing, it falls back to the plain locomotion state machine and warns.
A missing clip should cost the fire animation, not the whole rig.

---

## C. Audio

**I could not download sounds.** This container's network only reaches package registries —
freesound.org and opengameart.org both return 403. So what I did instead was build the system
properly around the 43 clips you already have, and make swapping them a one-line edit.

### What the system does now

- **Sounds are data, not code.** `WeaponData` carries `FireSounds[]`, `ReloadSounds[]`,
  `ImpactSounds[]`, `EmptySound` and `FireAudioUnitSize`. The hardcoded `switch (weaponName)`
  in `WeaponAttachment` is gone. Adding a weapon no longer means editing a C# file.
- **Variation.** Multiple fire clips are loaded per weapon and picked at random per shot, with
  ±6% pitch jitter, so a burst doesn't repeat one waveform.
- **Distance is per weapon.** `FireAudioUnitSize` — Bazooka 26 m, shotgun 18, AK/M4 16, Deagle
  14, pistol 10, Karambit 6.
- **Footsteps.** Your nine `Player_Footstep_*.wav` clips were sitting unused. `PlayerAudio.cs`
  plays them at a per-state cadence (walk 0.48 s, sprint 0.34 s, crouch-walk 0.72 s), on every
  peer, positionally — so you can hear someone sprint up behind you.
- **Hit / death / respawn.** `PlayerHit.wav`, `PlayerDead.wav`, `PlayerRespawn.wav` were also
  unused. Driven by watching replicated `CurrentHealth` rather than by signals, because Health
  only emits `Respawned` on the owning peer.
- **Hit markers.** The five `TargetHit-*.wav` clips play non-positionally for the shooter only.
- **Impacts.** A one-shot at the bullet's impact point, parented to the scene root so it doesn't
  follow the barrel while it plays.

### Replacing the sounds

`audio/SOUND_SLOTS.md` lists every slot, its current file and what should go there. Two ways to
swap: drop a new file over the existing path (zero edits), or point the registry string at a new
path. Sources worth using, in order:

- **Sonniss GDC bundle** (`gdc.sonniss.com`) — the 2026 edition is around 7.5 GB across 347 files, royalty-free for commercial use with no attribution, and a decade of past bundles is still up. This is the one to get for weapons.
- **Freesound**, filtered to CC0 — as of September 2026 the license filter shows about 381,000 CC0 sounds out of 734,000. Free account needed to download.
- **Kenney** — fully CC0, no sign-up, no attribution; good for UI and impacts, not for guns.

Watch the licence: CC-BY needs a credit line and CC-BY-NC can't ship in anything you sell.

---

## D. Map

`scenes/maps/Warzone.tscn` — a 60 × 60 m arena, instanced by `GameWorld.tscn`. Kept small on
purpose, per your "nothing big, just this size". About 150 props, all simple boxes and cylinders
sharing 11 materials, which is what the HD 520 wants.

Laid out from the plan's world section (`apocalyptic city ruins blend into open wasteland`,
built from modular pieces rather than one monolith):

- **Perimeter** — 7 m blast walls with ribs, so it reads as panels rather than one slab.
- **Shipping containers** — 18 at real dimensions (6.06 × 2.44 × 2.59), some stacked two high
  with a jitter on the top one, in rust / olive / blue / tan.
- **Central ruin** — a two-storey concrete shell: pillars, a first-floor slab, broken ground
  walls with gaps to run through, waist-high parapets upstairs you can crouch behind, and a
  rubble ramp up. This is the contested middle.
- **Watchtower** in the north-west, deck at 5.7 m, reachable off the stacked containers.
- **Cover** — 20 jersey barriers, 6 sandbag nests, 4 burnt-out vehicle hulks, fuel drums, pipe
  stacks, and five shallow craters (no collision on those, so nothing traps a player).
- **Lighting** — low warm sun with shadows, a cool fill light, dusty orange procedural sky,
  distance fog. Filmic tonemap.
- **8 spawn markers** in the `spawn_point` group, on the perimeter, each facing the middle.

`GameWorld.cs` now uses those markers instead of dropping everyone on a 2 m grid at the origin —
which, on this map, would have spawned all four players inside the central ruin on top of each
other. It falls back to the old grid with a warning if a map has no markers.

`Health.SpawnAreaHalfExtent` went 18 → 26 to match. The arena diagonal is ~85 m, so FR-PL-06's
50 m respawn minimum is now reachable for most death positions instead of never.

**If it runs badly**, the first thing to try is turning off `shadow_enabled` on the `Sun` node —
150 shadow casters is the only part of this map that's expensive.

---

## E. What I verified, and what I couldn't

Verified mechanically:

- Every `res://` path referenced from any `.tres`, `.tscn` or `.cs` resolves to a file that
  exists. No stale `.fbx` references remain anywhere.
- Every `SubResource`/`ExtResource` id referenced in the scenes is declared; every `[node]`
  parent path exists; `load_steps` counts are correct.
- Brace balance across all C# files.
- Weapon geometry, grip rotations and the hand-bone basis are computed from the actual files,
  not assumed.

**Not verified: none of the C# was compiled, and nothing was run.** There is no .NET SDK in this
container. The API usage is written against Godot 4.7 (`Godot.NET.Sdk/4.7.2`), but if something
fails to build, the likely candidates are the AnimationTree construction in
`PlayerAnimationController.BuildGraph` / `AddOneShotLayer` and the RPC attributes in
`WeaponSwitcher` — those use the most surface area. Send me the error and it'll be quick.

Also not verified, because it needs eyes on the screen: whether the grip offsets actually look
like a held weapon, and whether the upper-body animation filter blends the way it should.

## Still open

- `WeaponSwitcher.IsHeadZoneHit` measures against a remote player's `Head` node, but only its
  *rotation* is replicated, not its position, and remote copies return early from
  `_PhysicsProcess` — so their `Head` stays at the default 1.6 and headshots on a crouched
  opponent are tested against a standing head. This one matters now that the map has cover.
- Impact sounds are local to the shooter only.
- The Bazooka is registered but not in the 4-slot test loadout and has no splash damage.
- No bullet-hole decals, no tracers, no shell ejection.
- `FirstPersonArms.cs` is still dead code.
- Two copies of the weapon data still exist (see A.2).
