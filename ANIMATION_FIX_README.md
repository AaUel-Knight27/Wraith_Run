# Animation status

The former full-body Manny locomotion, combat, and death animation system has been removed from the game.
Player and training character models now remain in their static imported pose. Menu operators use the static
Manny model. First-person weapons use the supplied AK, FN pistol, and Benelli FPS animation rigs through
`ViewModelManager` and `ViewModelWeaponData`.

Run `res://scenes/tools/viewmodel_check.tscn` to inspect weapon-to-rig mapping, switch lifecycle, clip playback,
camera-space bounds, materials, and bone scales. This check currently reports the Shotgun arm meshes as not
rendering; its failure is intentional until the Benelli skin/rendering issue is resolved.
