# Needs hand test

Headless checks confirm the five map scenes load, the lean export resolves all eleven weapon models and all 21 audio folders that currently contain clips, and the pause menu changes its open state. These checks cannot confirm rendered quality, audible mix, or real input feel.

- **Player body and character roster — blocked:** `res://art/characters/manny.glb` is missing. `Player.tscn` cannot resolve its Armature; the headless run reports a scene parse error and WeaponSwitcher null reference. The character menu also cannot load its model, and TrainingCenter cannot create its trainee. Restore Manny or prepare and wire a compatible replacement before testing character visuals, animation, weapon grip, movement, or training.
- **Weapon play in a real match — blocked:** the isolated AK attachment loads and starts `ak47_shooting.mp3`, but firing, reloads, hitscan, muzzle effects, and sound positioning need a working Player skeleton and a live match.
- **Pause menu presentation and input:** the menu opens/closes in code, but verify its rendered tablet/menu, mouse capture, Escape, gamepad Start, and Android Back on a running game.
- **Audio quality:** listen on a real audio device to footsteps, impacts, weapon reports, UI sounds, music, and spatial falloff. Dummy headless audio only confirms streams load and playback starts.
- **Texture appearance:** inspect the AK, FN502, and Benelli viewmodels at 2048 in motion and close range. No separate 4096 viewmodel maps were found for P90 or Kriss Vector; verify their imported/embedded materials visually.
- **Maps and network play:** walk and drive each map to check collision, spawn placement, lighting, decals, and ruin presentation. Test LAN join/leave, replicated weapons, and remote audio with multiple clients.
- **Windows Lean build:** the Windows PCK export succeeded, but the Windows executable was not run on this Linux host. Launch the preset on Windows and check startup, menus, and map transitions.
