# Wraith Run — Team Deathmatch patch

This is a small patch, not a full re-export of your project: it only contains the files that are
new or changed. Unzip it over your project root (overwrite when prompted) and these 9 paths will
be added/updated; everything else — art, audio, data, maps — is untouched.

## What's in it

**New**
- `scripts/autoloads/MatchManager.cs` — autoload that owns the whole Team Deathmatch system: team
  assignment (server-decided, auto-balanced), Friendly Fire and Team Indicators toggles, Score
  Limit / Time Limit, team score, and match-end detection.
- `scripts/player/TeamIndicator.cs` — the floating name label above a player's head: green for a
  teammate (visible through walls), red for an enemy (hidden behind walls by the normal depth
  test, no raycasting needed).

**Changed**
- `scripts/player/Health.cs` — `ReceiveDamage` now skips the hit when the attacker is a teammate
  and Friendly Fire is off. This is the authoritative check (runs on the victim's own device, same
  as every other damage rule already did).
- `scripts/player/WeaponSwitcher.cs` — `FireHitscan` skips damage *and* hit-marker feedback for a
  friendly target under the same rule, so a friendly hit gives no confirmation. The ammo, muzzle
  flash, recoil and animation still happen either way — the round still "fires", it just does
  nothing to a teammate.
- `scripts/network/GameWorld.cs` — one added line: the host assigns itself a team the same way it
  already spawns itself (peer 1 never raises `PeerConnected` for itself).
- `scripts/network/LanMenu.cs` — the "Play Locally" lobby screen: **Game Mode** is now a real
  choice (Deathmatch / Team Deathmatch) for the host, and a new **Match Settings** panel appears
  once Team Deathmatch is picked — see below. The left column is now inside a `ScrollContainer` so
  this fits on a phone screen in landscape.
- `scripts/player/PlayerHud.cs` — adds a "TEAM ALPHA" line under your own score, a live
  "ALPHA 12 - BRAVO 9" line top-centre, and a "TEAM ALPHA WINS" banner when the match ends. All
  three only appear in Team Deathmatch; Deathmatch (FFA) looks exactly like it did before.
- `project.godot` — registers the new `MatchManager` autoload (right after `KillFeed`, since
  `MatchManager` listens to its kill events).
- `scenes/Player.tscn` — adds a `TeamIndicator` node as a sibling of `Health`/`Score`.

## How it works

**Game Mode** — on the Play Locally screen, only the host picks Deathmatch or Team Deathmatch.
A joining client just sees "Deathmatch" (informational) since it doesn't decide the mode — it
picks up the host's real choice automatically the moment it connects.

**Teams** — two teams, ALPHA and BRAVO. The server assigns each joining player to whichever team
currently has fewer players, then tells every peer (itself included) through one broadcast, so
everyone always agrees on who's on which team.

**Friendly Fire** — off by default. When off, shooting a teammate does nothing (no damage, no hit
marker); the check runs on the victim's own device, which is where every damage decision in this
codebase already happens.

**Team Indicators** — on by default. Shows a name label over each player: green through walls for
a teammate, red (only when you can actually see them — normal wall occlusion, not a raycast) for
an enemy.

**Score Limit / Time Limit** — sliders in Match Settings (10–100 kills, 3–20 minutes). Every peer
tallies the team score itself from the same kill events everyone already receives, so no extra
network traffic is needed to keep it in sync. When either limit is hit, a "TEAM X WINS" (or "MATCH
DRAW" on a tied time-out) banner shows on every screen.

## What's deliberately *not* in this patch

You said "just the obvious system, nothing more," so a few things a full Team Deathmatch mode
might eventually want are intentionally left out for a later pass:
- No hard match-end: the banner shows, but players can keep playing after it (there's no
  return-to-lobby flow anywhere in the project yet to send them back to).
- No team-specific spawn zones — respawn is still fully random across the map, same as Deathmatch.
- No per-team colour on the kill feed text or on player skins — only the new name-label indicator.
- A joining client doesn't see the host's Game Mode/Friendly Fire choice before connecting (LAN
  discovery packets don't carry it) — it only learns the real settings once it's connected.

## I could not compile or test this myself

Important: this sandbox has no Godot engine, no .NET/dotnet SDK, and no network access to install
one, so I was not able to open the project or build it. What I *did* do: read every file this patch
touches in full, matched this project's exact conventions (the RPC/authority pattern, the autoload
style, the MenuStyle UI helpers), and cross-checked every method and property name this patch uses
against where it's actually defined. That's a thorough static review, not a substitute for
actually running it.

**Please open the project in Godot and check the Output panel for errors before relying on this.**
The one area I'm least certain about without a compiler is the exact Label3D property names in
`TeamIndicator.cs` (`NoDepthTest`, `OutlineModulate`, `Billboard`) — if there's a compile error,
it's most likely there, and the editor will point straight at the line.

### Suggested test pass
1. Open the project — check Output for errors on load.
2. Host a game, pick **Team Deathmatch**, leave the defaults, start hosting.
3. Join from a second device/instance. Confirm you land on a team and see a green nameplate on
   your host and a red one on the AI/other team once you're on opposite teams (join a couple of
   times if auto-balance puts you both on the same team).
4. With Friendly Fire off, shoot your teammate — confirm no damage, no hit marker.
5. Turn Friendly Fire on (before hosting), confirm teammate damage now works.
6. Turn Team Indicators off, confirm nameplates disappear.
7. Lower Score Limit to 10, play until one team hits it, confirm the "TEAM X WINS" banner appears
   on both screens.
8. Go back and pick plain **Deathmatch** — confirm everything looks and plays exactly as before
   this patch (no nameplates, no team score line, no Match Settings panel).
