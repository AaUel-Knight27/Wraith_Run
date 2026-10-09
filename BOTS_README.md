# Wraith Run - Bots (VS BOTS)

Bots for real matches. They are **not** the training range: **TRAIN BUTTON / Train With Bots is untouched and
still "coming soon"**. Bots live under **PLAY > VS BOTS**, and can also be added to a **PLAY LOCALLY > Host** lobby.

## How to play with bots

1. Main menu > **PLAY** > **VS BOTS**.
2. Pick **Game mode** (Deathmatch / Team Deathmatch), **Bot difficulty** (Easy / Normal / Advanced / Mixed),
   **Number of bots** (1-10) and **Adaptive Bots** on/off.
3. **START MATCH**. Bots are dropped at random walkable places, spread as far apart as possible.

In Team Deathmatch you and the bots are split into two even teams. Mixed rolls Easy/Normal/Advanced per bot
(35% / 40% / 25%). In **Play Locally > Host** there is an **ADD BOTS** panel (default 0) so a LAN match can be filled
with bots; friends who join see the bots as ordinary players.

## What each level is

Numbers live in `scripts/bots/BotProfile.cs` (one table per level). Simulated results against an AK-47 target
(`python3 tools/bot_duel_sim.py`): median time-to-kill, standing / strafing target, at 18 m:

| | Easy | Normal | Advanced |
|---|---|---|---|
| Median kill time, standing target | 1.9 s | 0.62 s | 0.45 s |
| Median kill time, strafing target | 5.7 s (64% never finish) | 1.8 s | 1.07 s |
| Bullets that hit (strafing target) | ~4% | ~40% | ~71% |

**Senses** (each level genuinely perceives differently; nobody has wall-hacks):

| | Easy | Normal | Advanced |
|---|---|---|---|
| Vision cone (half angle) / range | 42 deg / 28 m | 58 deg / 45 m | 70 deg / 75 m |
| Peripheral vision | none | weak | good |
| Time to notice an enemy in view | 0.55-1.0 s | 0.30-0.50 s | 0.20-0.32 s |
| Hearing footsteps/shots | poor (45% noticed, 4.5 m error/10 m) | good | sharp (95%, 0.8 m error) |
| When shot | freezes 0.5-1.1 s, vague direction | brief flinch | instant, knows the direction |
| Remembers a lost enemy | 2 s | 6 s | 12 s |
| Predicts movement | no | mostly | almost fully |
| Team callouts / kill-feed inference | no | sometimes | yes |

All of them: a lost enemy is hunted at its predicted spot; footsteps depend on movement (sprint loud, crouch-walk
nearly silent); walls muffle sound; a bot's own sprinting/shooting masks what it hears.

**Behaviour**

- **Easy**: rushes in the open, stands still to spray (8-30 rounds), keeps clicking on an empty mag, sprints
  everywhere (loud), overshoots flicks, panic-jumps, ignores cover, and about 8% of the time zones out for 3-8 s.
- **Normal**: uses cover and peek-shoots, strafes, flicks ~65% of the time, reloads when safe, looks around corners,
  still misjudges fights and makes ordinary mistakes (about 1 in 10 decisions).
- **Advanced**: patrols unchecked sectors, pre-aims sound sources at head height, holds angles (crouch),
  jiggle-peeks from cover, counter-strafes before a burst, taps at range, flanks, swaps to a sidearm rather than
  reloading under pressure, fights from cover when hurt or outnumbered. Tiny mistakes about 1 in 40 decisions
  (wide peek, poor reload timing).

**Adaptive Bots** (on by default): if you lose several fights in a row, bots slow down a little (reaction, wobble,
fire discipline); if you are dominating they tighten up slightly. Capped to a gentle nudge, it never turns
Advanced into Easy. Turn it off for constant difficulty.

## How it works (for anyone reading the code)

Bots are the normal Player scene driven by `BotBrain` instead of the keyboard:

- `BotBrain*.cs` looks (vision cone + line-of-sight rays), listens (`BotSenses`), remembers, decides, then writes to
  `BotControls` (a virtual keyboard/mouse). `PlayerMovement` and `WeaponSwitcher` read it wherever they read `Input`.
- `BotAim` is a simulated hand: acceleration-limited flicks that can overshoot, post-flick correction, wobble,
  recoil compensation. The bot only fires when its crosshair is on where it *believes* the enemy is
  (delayed + noisy), so misses are real.
- `NavGrid` builds a walkability grid from the map's real colliders when the match starts (works on any map).
  It probes only body height, so roofs above the play space do not block the ground below. A*, line of sight
  (aware of low cover), cover and spawn queries.
- The host simulates all bots; other peers just see players (spawned by the same `MultiplayerSpawner`).
- VS BOTS runs on a real ENet server bound to loopback only and never broadcast, so it exercises exactly the code a
  LAN match does while staying private.
- `PlayerIdentity` gives every player an id: people use their peer id, bots a unique fake id (all bots share the
  host's authority, so authority can't identify them any more).

## Files

New: `scripts/bots/` (`BotProfile`, `BotBrain` x3, `BotAim`, `NavGrid`, `BotNav`, `BotSenses`, `BotDirector`,
`BotControls`, `BotRoster`, `PlayerIdentity`), `tools/bot_duel_sim.py`, this file.

Changed: `LanMenu` (VS BOTS screen, host-lobby bot panel, Train text), `GameWorld` (nav build, random bot spawn),
`LanSession` (`StartSolo`), `MatchManager` (bot settings; **late joiners now receive existing players' teams**),
`PlayerMovement`, `WeaponSwitcher`, `Health`, `KillFeed`, `PlayerScore`, `TeamIndicator`, `KillStyleBonus`,
`PlayerHud`, `TouchControls`, `PlayerAudio`, the three debug tuners.

Fixes that bots needed, which also apply to people:
- Damage from a shooter to a target on the **same machine** was an RPC to yourself (silently dropped). It is now a
  direct call, so the host can hurt bots and bots can hurt the host.
- Scores, teams, kill feed and damage attribution now use `PlayerIdentity` instead of the raw peer id.
- `TeamIndicator` is added to every spawned player in code, because the `Player.tscn` in this project has none
  (it shows ally/enemy tags in Team Deathmatch, and bot names).

## Tuning

Change numbers in `BotProfile.cs` only. Then `python3 tools/bot_duel_sim.py` to see the effect on kill times.
Feels too hard: raise `ReactMin/ReactMax`, `HandNoiseDeg`, `PerceptionDelay`; lower `Lead`, `RecoilCompensation`.
Feels too passive: raise `Aggression` (how often a bot pushes instead of holding/peeking) or lower `CoverUse`.
Max bots is `MaxBots` in `GameWorld.cs`; grid resolution is `NavCell`.

## Things I could not do

I had no Godot or .NET here, so **this has not been compiled or run**. I checked it by: cross-referencing every
identifier and API signature against your source, brace/paren balance, simulating the map's navigability from the
real Prison colliders, and simulating duels with a line-for-line port of the aim code. Please build once; if the
compiler reports anything, send me the message. Also worth a look in play: bots and their spawn paths on the currently selectable maps.

If you later apply the MP5 viewmodel patch, change `_isLocal` in `FirstPersonMp5Viewmodel`/`FirstPersonArms` to
use `PlayerIdentity.IsLocalHuman(player)` so a bot's copy never shows a first-person weapon.
