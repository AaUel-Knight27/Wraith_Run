# Wraith Run — Training Center

Main menu → **Train** → Training Center. Fully offline, one trainee, **Manny only**.

> **Read this first — verification status.** This was written without access to Godot or a C# compiler, so it has
> **not been compiled or run.** I audited it statically (details at the bottom) but expect to fix a handful of small
> compile errors or runtime surprises on first build. The "First run checklist" below tells you exactly where to look.

---

## What you get

24 drills across 6 tracks. The five classes from the roster are the five class tracks. (**"Shooter" = the roster's
`Operator` class** — the code uses the roster name, the menu uses yours.)

| Track | Drills | What it trains |
|---|---|---|
| **Fundamentals** | Movement Course · Flick Aim · Precision (Headshots) · Tracking · Recoil Control · Read the Enemy · Map Reading | movement, aim, recoil, enemy reading, map reading |
| **Shooter** | Weapon Tour · Reload Discipline · Kill Styles · Shoot From a Vehicle | all 8 weapons, ammo/time discipline, kill-style bonuses, shooting from a moving car |
| **Driver** | Slalom · Braking Box · Seat Swap · Hazard Run | handling, stopping distance, the 2 s swap (full-stop *and* brief-slowdown rules), fuel + boost, spike strips |
| **Fixer** | Repair Basics · Fix and Fight | repair channel, repair gating by vehicle speed, **fixing the car while fighting** |
| **Medic** | Triage · Heal Under Fire · Answer the Call | heal channel + charge economy, smoke cover, comms |
| **Demolisher** | Charge Basics · Breach and Clear · Road Denial · Heavy Weapons | charges and blast radius, **comms that change what friends do**, spike strips, bazooka vs tires |

**"Time usage"**: every drill has a par time and is graded against it; Reload Discipline scores *when* you reload;
the results screen shows times. **"Making yourself better"**: every run is graded S–D, saved locally
(`user://training_progress.cfg`), averaged per skill, and the hub's **Your Progress** box points at your weakest skill and
the next drill to run. Each results screen ends with specific coaching notes. Progress is local-only and never touches match stats.

### Controls added by training (registered at runtime, so `project.godot` is untouched)

| Key | Action | Key | Action |
|---|---|---|---|
| **E** | `interact` — get in car / swap seats | **G** | `callout_quick` — the drill's context callout |
| **Q** (hold) | `ability_primary` — heal / repair / arm charge / deploy strip | **V** | `callout_menu` — full callout list |
| **F** | `ability_secondary` — smoke / manual detonate | **M** | `map_toggle` |
| **Tab** | `training_menu` — pause / hub | **Backspace** | `training_restart` |

Existing keys are used as you have them (WASD, Shift sprint, **C** crouch/slide/brake, Space jump/handbrake, R reload, 1–8 weapons).
No collisions with your input map. On touch devices a small action bar appears. If you later add actions with these
names in Project Settings, yours win — nothing here changes.

---

## Manny only

* Every body in training — the trainee, enemies, friends, targets — is `art/characters/manny.glb` (`MannyOnly.cs`).
* The trainee is `Player.tscn` with its `CharacterLoadout` node **removed before it enters the tree**, so no operator tint or
  kit can apply, plus the two networking nodes an offline player must not have. `MannyOnly.VerifyTrainee` logs an error if the
  trainee's Armature is not Manny.
* Training is **per class, on the baseline kit** (`TrainingKit`), not per character. When you add characters, nothing here changes.

---

## Files

**New** (everything under `scripts/training/`, plus `scripts/combat/IShootable.cs`, `scenes/training/TrainingCenter.tscn`, `art/vehicles/…glb`)

```
core/      TrainingSession (root, lifecycle, spawn helpers)   TrainingDrill (base)   DrillRegistry (the catalogue)
           TrainingKit (class baseline numbers)   TrainingStats   TrainingProgress   TrainingDamage   TrainingAnchors/Anchor/Zone
           MannyOnly   TrainingInput   TrainingUtil   TrainingWeapons   TrainingTypes
mechanics/ HealSystem  RepairSystem  ExplosiveSystem(+PlacedCharge)  SmokeSystem  SpikeStripSystem  CalloutSystem  HoldChannel
actors/    TrainingActor (static Manny enemy/friend)  TrainingTarget  TrainingCar  BreachWall
ports/     Ports.cs   (the interfaces the real game systems plug into)
ui/        TrainingHud  TrainingMenu  TrainingMapView
drills/    one file per drill
```

**Changed (two existing files, additive only)**

* `scripts/player/WeaponSwitcher.cs` — **+90 lines, 0 removed.** Adds: the `IShootable` branch in `FireHitscan`
  (see "Why `IShootable`" below), events `ShotFired` / `ReloadStarted` / `WeaponEquipped`, and helpers `RefillAmmo`,
  `TopUpReserve`, `DrainMagazine`, `SetLoadedRounds`, `EquipByName`, `CurrentWeaponData`. Match gameplay is unaffected.
* `scripts/network/LanMenu.cs` — the **Train** card now launches the Training Center (same terminal-cleanup path as `LeaveForGame`).
  It replaces the "NOT BUILT YET" placeholder.

### Why `IShootable`
`WeaponSwitcher` damages players with `targetHealth.RpcId(owner, ReceiveDamage)`. That RPC is not `CallLocal`, so when the
target is on the *same peer* as the shooter (a bot, a dummy, a car, anything offline) Godot drops it and nothing takes damage.
Bodies implementing `IShootable` are hit by a direct call instead; everything else keeps the untouched RPC path. Likewise, bot
fire on the trainee goes through `TrainingDamage.HitTrainee`, which writes `Health.CurrentHealth` directly (never below 1 HP —
"downed" ends a round instead of triggering the match respawn). `PlayerAudio`/`PlayerHud` already watch that property, so hit sounds,
low-health breathing and the health bar react exactly as in a match.

---

## Plugging in your map

The arena is a **placeholder** (floor, grid, signs). To use your own:

1. Add your map to `TrainingCenter.tscn` and untick **Build Placeholder Arena** on the root.
2. Drop **`TrainingAnchor`** nodes (Marker3D) where each kind of drill should run, and set `AnchorId` to one of:
   `hub` · `aim_range` · `move_course` · `combat_yard` · `driving_course` · `road` · `breach_site` · `map_zones`.
   The marker's position **and facing** are used (−Z is forward: targets/courses run that way). Anything you don't place falls
   back to a default position, so a half-finished map still trains.
3. For map reading, drop **`TrainingZone`** nodes (name, half-size, colour). With none placed, the drill builds a 3×3 coloured grid.
4. Drills spawn their own props at the anchors and delete them afterwards. They need open, flat ground of roughly:
   aim/combat ≈ 45 m deep · movement ≈ 130 m long · driving ≈ 250 m long · road ≈ 220 m · breach ≈ 40×40 m.

---

## Wiring the real systems later

The missing systems were built as **training-side mechanics behind interfaces** (`ports/Ports.cs`, `IShootable.cs`). To go live,
make the real thing implement the interface; the mechanic and every drill on it keep working.

| System | Built here as | Port | Real system must |
|---|---|---|---|
| Medic heal | `HealSystem` (3 s channel, 4×25 HP, interrupted when hit, no healing while moving) | `IHealable` (+ `PlayerHealable` adapter for the real `Health`) | return real teammates from `Candidates`; replicate the channel/result |
| Fixer repair | `RepairSystem` (6 s, 3 charges; tire first, then +50 HP; **not** interrupted by damage/strafing so you can fight while repairing) | `IRepairable` | implemented by the real vehicle class |
| Demolisher charges | `ExplosiveSystem` + `PlacedCharge` (5 s arm, fuse, radius falloff, hurts everyone incl. you) | `IPlantable`, `IExplosionReceiver`, group `blast_receiver` | real breach walls/vehicles implement both |
| Spike strips | `SpikeStripSystem` + `SpikeStrip` (3 s deploy, one stop per vehicle per 45 s) | group `training_car`, `PunctureAllTires()` | real vehicles join the group / expose the call |
| Smoke | `SmokeSystem` + `SmokeCloud` (blocks line of sight) | `TrainingSmoke.Blocks(a, b)` | call it from real AI sight checks |
| Comms | `CalloutSystem` (16 callouts, world markers, HUD log, friend AI reacts) | `ICalloutTransport` | write an RPC-backed transport, assign `CalloutSystem.Transport` |
| Vehicle | `TrainingCar` (arcade drive, fuel, boost, tires/HP, 2 seats, 2 s swap with the 2 / 8 m/s rules, AI driver/shooter) | `IRepairable`, `IShootable`, `IPlantable`, `IExplosionReceiver` | replace with the real vehicle; keep the interfaces |
| Class kit numbers | `TrainingKit` | `ApplyCharacter(CharacterData)` | already reads `CharacterEffectIds`; changes **numbers only, never the body** |
| Bots | `TrainingActor` (training-only AI: strafe/peek/rush/move, LOS hitscan) | — | your real bot AI, when it exists, can replace it |

### Numbers that are mine, not yours (all in `TrainingKit.cs`, marked `ASSUMED`)
Derived from your roster notes: heal channel 3.0 s, 4 charges × 25 HP, repair 6 s / 3 charges, arming 5 s, 2 charges, seat swap 2 s,
swap limits 2 / 8 m/s, spike deploy 3 s / 45 s cooldown, smoke 10 s / 4 m. **Not in the roster, so I chose them:** blast radius 6 m,
blast max damage 90, fuse 4 s, +50 body HP per repair charge (from the garage "+50 car HP"), 2 practice smokes for the medic and a
practice spike strip for the demolisher (baseline classes don't have them). Change them in one place.

---

## Adding to it

* **A drill:** write a `TrainingDrill` subclass in `drills/` (override `OnBegin`, `OnTick`, `Objective`, `ComputeRating`, `BuildResults`,
  optionally `CoachingTips`, `ContextCallout`), then add one `DrillDefinition` in `DrillRegistry.cs`. Hub, grades, progress, recommendations
  and the results screen pick it up automatically.
* **A callout:** add a value to `CalloutType` and rows in `CalloutInfo`.
* **A class track:** add a `TrainingTrack` value + its text in `TrainingTypes.cs`, then drills with that `Track`.

---

## First run checklist

1. **Build** (Godot's Build button). Fix any compile errors — see "How I checked" for which kinds are most likely.
2. Run the game → **Train**. You should land in the hub menu. Pick *Fundamentals → Movement Course → START*.
3. Things worth checking first, in rough order of risk:
   * **The car.** The GLB's own scale makes it ~250 m long, so `TrainingCar` measures and rescales it to 4.9 m, turns its front to −Z
     (auto-detected from the hood height), and makes its material opaque + back-face-culled so a seated player can see out. If it drives
     **backwards**, tick `Flip Model Front` on the car (or set it in `TrainingCar.cs`). The three 4096² textures are heavy for mobile —
     consider importing at 2048².
   * **Bots are static** and hold weapons. Manny locomotion, combat, and death clips were removed globally; first-person weapon rigs use their supplied FPS clips.
   * **Seat swap:** *Drive → Seat Swap*; stop under 2 m/s, press **E**.
   * **Heal / repair / plant** show a progress bar and a prompt; the **G** key sends the drill's context callout.
   * Anything printed in red in Godot's Output panel when a drill starts.
4. Known simplifications: no heal/repair/arming sounds yet (only weapon and the RPG explosion sound are reused); driver view is locked to car
   heading; the car does not model suspension or wheel spin (the GLB is one merged mesh); touch action bar is untested.

## How I checked (no compiler available)

* Read all 43 pages of the master plan and every README/merge note; read every Godot API call against your existing code for signatures.
* Found that your `Player.tscn` has no `CharacterLoadout`, `WeaponSoundTuner` or viewmodel, unlike what `MERGE_REPORT_2026-09-27.md` describes; I built on the code as it actually is.
* Static audits over all new code: every identifier compared with your compiling project's vocabulary and the Godot API; bracket balance and
  nesting; every Godot class is `partial` and in a same-named file; call-arity check on all ~1,170 calls to methods I defined; every
  `receiver.Member` against its declaration; no type-name collisions in your global namespace; every `CharacterEffectIds`, weapon name, input action,
  node path and `MenuStyle` signature verified against your files. **Result: no problems found.** That rules out typos and arity mistakes; it cannot
  rule out type-conversion or Godot-version API differences, which is why step 1 above is a real step.
