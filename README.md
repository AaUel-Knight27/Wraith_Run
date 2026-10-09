# Wraith Run

Wraith Run is a 3D first-person combat game built with Godot and C#. The project includes LAN multiplayer, solo matches against AI bots, several match rulesets, character and weapon loadouts, vehicles, a player hub, and a shooting range.

## Requirements

- Godot **4.7.2 .NET** (the project uses `Godot.NET.Sdk/4.7.2`)
- .NET 8 SDK for desktop development
- For Android builds, the project file targets .NET 9; install the Android workloads and export templates required by your Godot .NET setup

## Open and run

1. Open `project.godot` in Godot 4.7.2 .NET.
2. Allow Godot to import the project assets and build the C# solution.
3. Run the project from the editor. The configured entry scene is `scenes/Splash.tscn`.

Godot export presets are provided for Android, Windows Desktop, and Windows Lean. Export requirements and signing configuration depend on the target platform.

## Play

The project has a first-person player controller with keyboard/mouse and touch controls. From the menus, players can start a solo bot match, host or join a match on a local network, customize their operator and loadout, or enter the shooting range. The in-world hub also provides access to match setup and practice.

### Default keyboard and mouse controls

| Action | Default |
|---|---|
| Move | W, A, S, D |
| Look | Mouse |
| Jump | Space |
| Sprint | Shift |
| Crouch | C |
| Aim | Right mouse button |
| Fire | Left mouse button |
| Reload | R |
| Interact | E |
| Select weapon slots | 1, 2, 3 |
| Cycle weapon | Mouse wheel |
| Pause / menu | Esc |

Key bindings and look settings can be changed in the game settings. Touch controls are implemented for mobile and can be configured there as well.

### Modes and arenas

The match rules are implemented in `scripts/modes/GameModeBase.cs` and configured through the lobby. Rulesets include Default, Team Deathmatch, Pure Chase, Storm Respawn, Clan Chase, Cursed Deathmatch, Convoy Assault, and 2v2 Duo Buggy. Available arenas are Warehouse, Prison, Ashfields, Depot, and Lab. Match modes differ in team structure, vehicles, timed scoring, and event rules; check the in-game mode descriptions for the selected ruleset.

Solo bot matches offer configurable opponents and difficulty. LAN matches use a host-authoritative ENet session; discovery broadcasts over UDP on port 7778 and gameplay connections use port 7777. If automatic discovery is unavailable, use the host's local IP address.

## Project layout

- `project.godot` — Godot project settings, input map, startup scene, and autoloads
- `Wraith Run.csproj` / `Wraith Run.sln` — C# project and solution
- `scenes/` — splash, menu, player, game world, hub, vehicle, maps, and training scenes
- `scripts/` — gameplay systems, including player movement and combat, bots, networking, match rules, UI, stats, and vehicles
- `data/` — weapon and character resources and registries
- `art/` — character, weapon, vehicle, environment, and UI assets
- `audio/` — audio clips and bus configuration
- `mapkit/` — reusable map building, materials, props, and scattering tools

Important autoloads include `LanSession`, `MatchManager`, `MatchStats`, `CareerTracker`, `SettingsManager`, `SoundManager`, and `KillFeed` (see the `[autoload]` section of `project.godot`).

## Player data and settings

Godot stores per-user settings and account/career data in its `user://` directory, outside the project checkout. The settings system persists display, audio, controls, and touch options; account and career systems track player identity and match progress.

## Credits and asset licenses

Third-party asset credits and license information are maintained in `shipping_credits/` and in license files shipped alongside assets. Review those notices before redistributing or commercially using the game or its assets; individual asset terms may differ.
