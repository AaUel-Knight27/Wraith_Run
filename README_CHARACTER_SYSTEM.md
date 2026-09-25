# Character system drop-in

Merge these into the matching folders in the Godot project (same relative paths). Nothing here
changes existing behavior by default - CharacterLoadout.CharacterId is empty on Player.tscn's
Player, so every GetModifier() call returns its default and the game plays exactly as it does
today until an operator is actually assigned.

## New files
- scripts/data/StatModifier.cs         - one buff/drawback entry (effect id, value, note)
- scripts/data/CharacterEffectIds.cs   - the full effect vocabulary; comments say which are wired
- scripts/data/CharacterData.cs        - REPLACES the earlier draft (same name, new shape)
- scripts/data/CharacterRegistry.cs    - unchanged in behavior, GetById now returns nullable
- scripts/player/CharacterLoadout.cs   - loads the equipped operator, exposes GetModifier()
- data/characters/*.tres (25 files)    - the full roster from the design doc
- data/characters/CharacterRegistry.tres

## Modified files (surgical, additive - diff them before merging)
- scripts/player/Health.cs          - MaxHealth *= OnFootMaxHealthMult in _Ready()
- scripts/player/PlayerMovement.cs  - WalkSpeed/SprintSpeed/CrouchSpeed: const -> [Export],
                                       scaled by MoveSpeedMult/CrouchWalkSpeedMult in _Ready()
- scripts/player/WeaponSwitcher.cs  - ApplyRecoil() scaled by WeaponRecoilMult
- scenes/Player.tscn                - added a CharacterLoadout child node (first child, empty
                                       CharacterId), bumped load_steps 16 -> 17

## To actually see an operator's kit in effect
Open Player.tscn, select the CharacterLoadout node, set CharacterId to one of the 25 ids (e.g.
"ironsight_kessler" for -12% recoil), and run the scene.

## Wired vs not (see CharacterEffectIds.cs for the full breakdown)
4 of the roster's ~40 distinct effects touch systems that exist today: on-foot max HP, base move
speed, crouch-walk speed, weapon recoil. Everything else (fuel, seat swap timing, vehicle
handling, repair, the ability/charge economy for Fixer/Medic/Demolisher, stamina, reserve ammo,
weapon-swap delay, smoke) is real roster data with nowhere to plug in yet, because those systems
are still docs-only in this project. The data model won't need to change when they're built - see
CharacterEffectIds.cs comments for what each one is waiting on.
