# Weapon sets, Kriss Vector, and the new weapon menus

Written without the ability to compile or run Godot. Everything was checked by reading the code
against the real signatures and by testing the set-validation logic in a standalone port, but
**nothing has been run in the engine yet** - see "Check these in the engine" at the bottom.

## What a player carries

| Slot | Key | Holds | Who |
|---|---|---|---|
| Primary 1 | 1 | Assault rifle, SMG or shotgun | everyone |
| Primary 2 | 2 | Assault rifle, SMG or shotgun | everyone |
| Pistol | 3 | Pistol | everyone |
| Knife | 4 | Melee | everyone |
| Heavy | 5 | Launcher (Bazooka) | **Demolisher operators only** |

A slot only ever accepts its own kind of weapon, so a set can be changed but never grown or shrunk.
The same weapon cannot sit in both primary slots. The rules live in `scripts/data/WeaponSets.cs`
(`WeaponRules`) and are the single source for the lobby, the saved profile and the match.

## Three sets

SHORT RANGE, MID RANGE and LONG RANGE by default. The player can rename each one (16 characters)
and change any of its weapons, on the same screen. Defaults:

| Set | Primary 1 | Primary 2 | Pistol | Knife | Heavy |
|---|---|---|---|---|---|
| SHORT RANGE | Shotgun | Kriss Vector | Glock 19 | Karambit | Bazooka |
| MID RANGE (active) | AK-47 | P90 | Glock 19 | Karambit | Bazooka |
| LONG RANGE | M4 | AK-47 | Desert Eagle | Karambit | Bazooka |

Saved in `user://profile.cfg` under `[weapon_sets]`. Anything read back is re-checked against the
rules and repaired from the defaults, so a hand-edited or old file cannot put an illegal loadout in
a match. A profile from before sets (one saved weapon) keeps that weapon as Primary 1 of the active set.

## Menu flow

Main menu, STARTING WEAPON tile -> **WEAPON SETS** (three cards; picking one makes it active) ->
**WEAPON SET** (name box + the set's weapons, operator on the stage holding the selected one,
description on the right, CHANGE WEAPON / CONTINUE) -> **CHOOSE WEAPON** (category tabs across the
top, weapon list, 3D model, description on the right, ATTACHMENTS and SKINS buttons that open a
"Coming soon" popup, CONTINUE). Back goes up one step; CONTINUE on the set screen returns to the
main menu.

The picker only shows the categories the slot may hold: a primary slot shows ASSAULT RIFLES, SMGS
and SHOTGUNS; the pistol slot only PISTOLS; the knife slot only MELEE; the heavy slot only HEAVY.
The Heavy row is greyed out ("DEMOLISHER ONLY") unless the chosen operator is a Demolisher.
A weapon already in the other primary slot is tagged and cannot be picked twice.

Removed text: the "drag to rotate / scroll or pinch / double-tap" hint and the ROTATE caption
(the rotate arrows stay), the "Pick an operator from the list..." line, and the two paragraphs on
the Customize screen ("Colours are saved per operator..." / "Other players do not see your colours
yet..."). Note the second one was true: colours are still only visible to the local player.

## In a match

* Number keys 1-5 follow the carried set (table above), not the global weapon list. Keys 6-8 and
  their input actions are gone; the CONTROLS screen lists the five keys by name.
* `weapon_cycle_next/prev` (controller shoulder buttons - bound before, but nothing read them) now
  step through the carried set. The touch SWAP button uses `weapon_cycle_next`; MELEE is key 4.
* The player starts holding Primary 1 of the active set.
* Pause menu WEAPON SET and ITEMS screens show only the carried weapons, numbered like the keys, and
  the WEAPON SET header carries the set's name.
* Network: the replicated weapon index is still the position in `WeaponSwitcher.Loadout`, so other
  players need no knowledge of anyone's set. Kriss Vector was **appended** (index 8); never reorder
  that list.
* Bots are unchanged except that they can now pick the Kriss Vector.

## Kriss Vector

`KRISS VECTOR.glb` is wired in `scripts/data/WeaponRegistry.tres`. Grip numbers were derived from the
mesh itself (muzzle on +Z, up +Y, the pistol grip located from a side render) and calibrated against
where the AK-47, M4 and P90 sit in the hand: rotation (-90, -90, 0), position (0.036, 0.164, 0),
scale 0.55, muzzle distance 0.34. The model has a suppressor built in, so the muzzle flash comes
from its tip; it becomes an attachment option once attachments exist.

## Menu scale

The menus are laid out for 1280x720 but the project viewport is 845x480, which squeezed the side
panels over the 3D stage. `LanMenu` now sets the window's content scale to 1280x720 while a menu is
on screen and restores the project's viewport size when the menu scene exits, so the HUD is
untouched. If the lobby looked right to you before, delete the `MenuDesignSize` lines in
`LanMenu._Ready` / `_ExitTree`.

## Check these in the engine

1. Kriss Vector: right hand on the grip, left hand on the foregrip, muzzle flash at the tip. If the
   hands are off, F8 (AnimationTuner) -> "Copy weapon lines" and paste under the Kriss entry.
2. Menu scale: main menu and all lobby screens at your usual window size.
3. Typing a set name (including clearing the box and clicking away - the default must return).
4. Demolisher operator: Heavy row unlocked in the set screen and key 5 in a match; any other
   operator: Heavy greyed out and key 5 does nothing.
5. The Kriss texture set is large (several 4K PNGs, ~30 MB glb); fine on desktop, worth downsizing
   for Android.

## Files

New: `scripts/data/WeaponSets.cs`, `scripts/ui/WeaponScreens.cs`.
Changed: `scripts/data/PlayerProfile.cs`, `scripts/data/WeaponData.cs` (Description field),
`scripts/data/WeaponRegistry.tres` (descriptions, Kriss grip), `scripts/player/WeaponSwitcher.cs`,
`scripts/player/TouchControls.cs`, `scripts/bots/BotBrain.Combat.cs`,
`scripts/autoloads/SettingsManager.cs`, `scripts/network/LanMenu.cs`, `scripts/ui/LoadoutScreens.cs`
(old flat weapon screen removed), `scripts/ui/MenuStyle.cs` (WideTab, SetCard),
`scripts/ui/PauseMenu.cs`, `project.godot` (weapon_6..8 removed).

`data/weapons/*.tres` (the older stripped-down registry) is not used by any code and was left alone.
