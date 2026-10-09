# Needs hand test

The exported Linux-equivalent Lean build was exercised headless. It spawned the selected character with the production Player scene, equipped and fired all 11 WeaponRegistry weapons, hit one target in each lane and incremented each counter, toggled the pause menu, loaded GameVehicle and all 12 registered character models, and loaded all five maps. No missing-resource or missing-script errors were reported. The Windows Lean export completed, but this host cannot run the Windows executable.

- **Visual character and weapon presentation:** check AshenCorvus scale, surface textures/tint, weapon-in-hand alignment for all weapons, viewmodel visibility, and the character menu preview. The FBX contains a rest-pose clip only; full-body locomotion animation remains static.
- **Range interaction and appearance:** click the weapon selector and reset button, check F6 reset, target flash, signage, counters, lane visibility, and HUD layout at normal resolution. Headless verified hit events/counters with simulated fire input, not rendered presentation or mouse feel.
- **Real input and pause presentation:** test mouse capture/aiming, Escape, gamepad Start, and Android Back. Headless toggled PauseMenu directly and did not exercise these devices or render the tablet UI.
- **Audio:** listen to weapon reports, impacts, UI sounds, music, and spatial falloff on a real audio device. The headless export used dummy audio.
- **Vehicle handling:** drive GameVehicle and check collision, seats, and controls. Headless verified that the scene instantiates only.
- **Maps and multiplayer:** walk and drive each map to check collision, spawn placement, lighting, decals, and Prison’s ruin appearance; test LAN join/leave, replicated weapons, and remote audio. Headless verified scene loading, not visual quality or multiplayer sessions.
- **Windows runtime:** launch `Windows Lean` on Windows and verify startup, menus, range routing, and map transitions. The Windows executable exported successfully but was not run on this Linux host.
