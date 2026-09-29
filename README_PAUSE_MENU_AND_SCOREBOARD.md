# In-game menu and scoreboard

## Controls
| | PC | Phone | Gamepad |
|---|---|---|---|
| Open / close menu | Esc | MENU button (top centre) or Android Back | Start |
| Scoreboard | hold Tab | SCORE button (tap to show, tap to hide) | hold Back/Select |

The menu does NOT pause the match (it is multiplayer). It only stops YOUR input and frees the mouse.

## Menu screens
RESUME, OBJECTIVES, MAP, WEAPON SET, ITEMS, SETTINGS, QUIT GAME.
* OBJECTIVES - mode rules, team scores + progress bars, time left / match time, your score, kills, deaths, assists, K/D.
* MAP - top-down map drawn from the arena's real StaticBody3D collision shapes (works for every map). Shows you and, in Team Deathmatch, teammates. Enemies are never shown.
* WEAPON SET - all 8 loadout weapons with stats; click one to equip it.
* ITEMS - health, ammo (magazine + reserve) for EVERY weapon, melee, operator kit.
* SETTINGS - the same settings screen as the main menu (now one shared class: SettingsPanel).
* QUIT GAME - leave match (back to main menu) or quit to desktop. If the host leaves, clients get a CONNECTION LOST screen.

## Scoreboard
Mode, match clock (time left in TDM), your K/D + kills/deaths/assists/score, and every player's rank, score, K, D, A, K/D. Team Deathmatch splits YOUR TEAM (friends) / ENEMY TEAM. You are highlighted, dead players dimmed.

## Files
New: scripts/ui/{PauseMenu,Scoreboard,MapView,SettingsPanel,GameUiState,GameSession}.cs, scripts/autoloads/MatchStats.cs
Changed: MatchManager (clock now runs in every mode, EndSession), PlayerMovement + WeaponSwitcher (input gate, per-weapon ammo), TouchControls (+MENU/SCORE), VirtualJoystick (ResetTouch), PlayerHud (creates the menus), GameWorld, LanMenu (settings moved out, Android Back), DebugHub (IsOpen), project.godot.

## project.godot (only 2 lines)
* [application]  `config/quit_on_go_back=false`   (lets Android Back open the menu instead of closing the app)
* [autoload]     `MatchStats="*res://scripts/autoloads/MatchStats.cs"`   (after MatchManager/KillFeed)

## Behaviour changes to know about
* Ammo is now per weapon (KeepAmmoPerWeapon in WeaponSwitcher; set to false for the old "swap = free reload"). Respawning refills everything.
* Esc no longer just frees the mouse - it opens the menu.
* Kills/deaths on the scoreboard come from MatchStats (all peers agree, late joiners get a snapshot). PlayerScore.Kills is still owner-only.
