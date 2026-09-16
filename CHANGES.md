# HUD update — apply on top of the combat update

Extract over your project root again. This is a second, separate patch — if you haven't
applied `wraith_run_combat_update.zip` yet, apply that one first, then this one on top.

## New: PlayerHud.cs
A `Hud` CanvasLayer node added as a child of Player, built entirely in code (same style as
LanMenu). Only the owning peer's Player instance builds any UI — a remote observer's copy of
this node checks `IsMultiplayerAuthority()` and does nothing, so you won't see other players'
HUDs.

Shows:
- A `+` crosshair, dead center.
- A health bar + "NN / MM HP" label, bottom-left.
- Weapon name + ammo ("12 / 30"), "RELOADING…", or "MELEE" for the Karambit, bottom-right.
- A centered "You died — respawning in N..." message while dead.

## Touched again: WeaponSwitcher.cs / Health.cs
Only additive — four new public read-only properties (`WeaponName`, `CurrentAmmo`,
`MagazineSize`, `IsReloading` on WeaponSwitcher; `RespawnTimeRemaining` on Health) so the HUD
can read state without touching how either script's internals work. No combat logic changed.

## Still not there
No damage numbers/hit markers, no kill feed, no minimap. This is deliberately just enough to
see HP and ammo change in real time while you test the two-instance combat loop.
