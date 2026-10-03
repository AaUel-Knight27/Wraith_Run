# Wraith Run - hub build, update 2 (extract over your project, same paths)

This zip replaces the earlier wraith_run_hub_patch.zip: it contains EVERYTHING from the first patch plus the tablet work.

## Do this first
Project Settings > Application > Run > Main Scene: set it to res://scenes/Splash.tscn (it was GameWorld.tscn, which skips the hub
and shows the old in-match menu). Or open scenes/Hub.tscn and press F6.

## Tablet (new)
- scripts/ui/TabletDevice.cs: the tablet model held up close; the menu is drawn on the glass of the model (a fixed-size
  SubViewport) and the mouse is mapped through the glass.
- scripts/ui/HubTablet.cs: layout like your reference: BACK, big PLAY button (SPACE), HOME / OPERATOR / PLAYERS / CHAT tabs
  (Q / E to switch), people count, settings, power. PLAY becomes START (host) or READY / UNREADY (others) once you are in a lobby.
  HUD and chat feed hide while the tablet is up.
- scripts/ui/LoadoutScreens.cs: compact layout so the operator screens fit the tablet.

## Hub (from update 1)
scenes/Hub.tscn, scripts/world/Hub*.cs, MatchManager / GameWorld / Health / PlayerHud / PauseMenu / GameSession / MatchResults /
SplashScreen / LanMenu hooks, carry slots in WeaponSwitcher (keys 1-5), Team Deathmatch mode, bot weapon-table fix, Sniper category.
