# Lobby redesign (2026-09-30)

Built on top of wraith_run_godot_1.zip. Nothing was removed: pause menu, scoreboard, debug hub,
settings panel, splash, per-weapon ammo and every earlier system are untouched.

## What changed
- Main menu: selected operator stands in the middle (live 3D, idle pose, holding the chosen weapon).
  Top-left row = CHOOSE OPERATOR / CHOOSE WEAPON. PLAY and TRAIN (names only) bottom-left. No title, no right column.
- Drag the operator/weapon to spin it, scroll or pinch to zoom, drag while zoomed to pan (weapons: tilt),
  double-click / double-tap to reset. Works with mouse and touch.
- CHOOSE OPERATOR: all 25 operators plus STANDARD ISSUE, class filter tabs, 3D preview, skill, buffs and
  drawbacks. Kit lines tagged IN GAME (a system reads them) or PLANNED (data only, listed anyway).
- CHOOSE WEAPON: all 9 registry weapons (incl. Kriss Vector, tagged PLANNED because it is not in
  WeaponSwitcher.Loadout), turning 3D model, stat bars. Pick = the weapon you spawn with.
- CUSTOMIZE: button beside EQUIP on the operator screen. Jacket / pants / boots+gloves swatches, live on the model, saved per operator; back returns to the operator screen.
- Icons: all redrawn (angular, military). New: chevron-up, weapon, class-*, wpn-*.

## Wiring into the match
- PlayerProfile (user://profile.cfg) stores operator, starting weapon, colours.
- Player.tscn: added CharacterLoadout as first child. It reads the lobby's operator on the owning device only.
  Health / PlayerMovement / recoil modifiers now come from the equipped operator.
- WeaponSwitcher: spawns with the lobby weapon (through the replicated index); operator recoil multiplier applied.
- CharacterAppearance: paints jacket/pants/accent regions onto a copy of the mesh from skin weights
  (manny.glb has plain white vertex colours, so the tint shader had no mask). Imported file untouched.
- character_tint.gdshader: new skin_color and use_regions uniforms (defaults keep old behaviour).

## Known limits
- Operator kit and colours are not replicated: other players still see the plain body.
- Written without the ability to compile or run Godot; first build may need small fixes.
- Train button is still the "not built yet" screen (bot AI does not exist).

## Merged with the bots overlay (wraith_run_bots_overlay.zip)
The overlay was built on the ORIGINAL project, so applying it directly over the upgraded project would have
overwritten the pause menu, per-weapon ammo and the lobby. This package is a three-way merge of: the upgraded
project + the lobby redesign + the bots overlay. Use it instead of the overlay (BOTS_README.md is included).
- Kept from the upgrade: GameUiState input blocking (bots ignore it, people obey it), per-weapon ammo, TryEquip /
  TryGetAmmo, pause menu, SettingsPanel, EnsureSessionActive.
- Kept from bots: everything under scripts/bots/, VS BOTS screen, host bot panel, StartSolo, PlayerIdentity, same-machine
  damage fix.
- Bots never take your lobby operator, colours or starting weapon (CharacterLoadout and WeaponSwitcher use
  PlayerIdentity.IsLocalHuman instead of IsMultiplayerAuthority, because bots share the host's authority).
- Main menu: Train stays a "coming soon" screen (bots are under PLAY > VS BOTS); the old CUSTOMIZE GUN box stays removed.
- Not compiled or run. Merged files: LanMenu, WeaponSwitcher, PlayerMovement, GameWorld (hand-resolved); the rest merged cleanly.
