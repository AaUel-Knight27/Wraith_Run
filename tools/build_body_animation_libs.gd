# Rebuilds the 4 body AnimationLibrary files from the original Mixamo source FBX files.
# You already have the sources (they're what you uploaded) - to rerun this, drop them at:
#   art/animations/player/body/source/locomotion.fbx
#   art/animations/player/body/source/Combat.fbx
#   art/animations/player/body/source/death.fbx
#   art/animations/player/body/source/pistol/<the 14 "pistol *.fbx" files, see pistol_files below>
# then from a terminal in the project root:
#   godot --headless --path . --import          (first, so the new FBX get imported)
#   godot --headless --path . --script tools/build_body_animation_libs.gd
extends SceneTree

const OUT_DIR = "res://art/animations/player/body/"

func _rewrite_track_paths(anim: Animation, strip_prefix: String) -> Animation:
	var a: Animation = anim.duplicate(true)
	for t in range(a.get_track_count()):
		var p: String = str(a.track_get_path(t))
		if strip_prefix != "" and p.begins_with(strip_prefix):
			p = p.substr(strip_prefix.length())
			a.track_set_path(t, NodePath(p))
	return a

# Removes baked-in forward/sideways travel from the Hips root track so the clip
# plays in place (physics/CharacterBody3D stays the single source of truth for
# world movement). Vertical (Y) motion -- crouch bob, jump arc, death collapse --
# is left untouched. No-op for clips that were already exported "in place".
func _strip_horizontal_root_motion(anim: Animation) -> Dictionary:
	var hips_track := -1
	for t in range(anim.get_track_count()):
		var p: String = str(anim.track_get_path(t))
		if anim.track_get_type(t) == Animation.TYPE_POSITION_3D and p.ends_with(":mixamorig_Hips"):
			hips_track = t
			break
	if hips_track == -1:
		return {"fixed": false, "drift": 0.0}
	var key_count := anim.track_get_key_count(hips_track)
	if key_count == 0:
		return {"fixed": false, "drift": 0.0}
	# Linear detrend: subtract the straight-line ramp from the first key's X/Z to
	# the last key's X/Z. This removes net travel (the thing that fights the
	# CharacterBody3D) while preserving the natural frame-to-frame hip sway that
	# makes the gait look alive -- it is NOT the same as clamping every key to a
	# single fixed point, which would make every clip look stiff.
	var first: Vector3 = anim.track_get_key_value(hips_track, 0)
	var last: Vector3 = anim.track_get_key_value(hips_track, key_count - 1)
	var t_first: float = anim.track_get_key_time(hips_track, 0)
	var t_last: float = anim.track_get_key_time(hips_track, key_count - 1)
	var duration: float = t_last - t_first
	var max_drift := 0.0
	for k in range(key_count):
		var v: Vector3 = anim.track_get_key_value(hips_track, k)
		var t: float = anim.track_get_key_time(hips_track, k)
		var frac: float = 0.0
		if duration > 0.0001:
			frac = (t - t_first) / duration
		var ramp_x: float = lerp(first.x, last.x, frac)
		var ramp_z: float = lerp(first.z, last.z, frac)
		var new_x: float = v.x - ramp_x + first.x
		var new_z: float = v.z - ramp_z + first.z
		var d := Vector2(v.x - first.x, v.z - first.z).length()
		if d > max_drift:
			max_drift = d
		anim.track_set_key_value(hips_track, k, Vector3(new_x, v.y, new_z))
	return {"fixed": max_drift > 0.05, "drift": max_drift}

func _load_player(path: String) -> AnimationPlayer:
	var res = load(path)
	var inst = res.instantiate()
	var ap: AnimationPlayer = inst.find_child("AnimationPlayer", true, false)
	return ap

func _build_from_map(src_path: String, strip_prefix: String, name_map: Dictionary, skip: Array, lib_out: AnimationLibrary) -> void:
	var ap := _load_player(src_path)
	if ap == null:
		printerr("No AnimationPlayer in ", src_path)
		return
	for orig_name in ap.get_animation_list():
		if skip.has(orig_name):
			continue
		var clean: String = name_map.get(orig_name, orig_name)
		var a: Animation = ap.get_animation(orig_name)
		var rewritten: Animation = _rewrite_track_paths(a, strip_prefix)
		var rm := _strip_horizontal_root_motion(rewritten)
		var err := lib_out.add_animation(clean, rewritten)
		if err != OK:
			printerr("  failed to add ", clean, " err=", err)
		else:
			var tag = "  [root motion removed, was %.2fm]" % rm["drift"] if rm["fixed"] else ""
			print("  + %-40s <- %s (%.2fs, %d tracks)%s" % [clean, orig_name, rewritten.length, rewritten.get_track_count(), tag])

func _strip(s: String) -> String:
	return s

func _set_looping(lib: AnimationLibrary, clip_names: Array) -> void:
	for clip_name in clip_names:
		if lib.has_animation(clip_name):
			lib.get_animation(clip_name).loop_mode = Animation.LOOP_LINEAR

func _init():
	# ---- locomotion.fbx -> locomotion_lib.res (generic universal movement) ----
	var loco_lib := AnimationLibrary.new()
	var loco_map := {}
	for n in ["Armature|locomotion_crouch_idle","Armature|locomotion_crouch_strafe_left","Armature|locomotion_crouch_strafe_right",
		"Armature|locomotion_crouch_to_standing_rifle","Armature|locomotion_crouch_walk","Armature|locomotion_crouch_walk_back",
		"Armature|locomotion_crouch_walk_back_left","Armature|locomotion_crouch_walk_back_right","Armature|locomotion_crouch_walk_fwd_left",
		"Armature|locomotion_crouch_walk_fwd_right","Armature|locomotion_idle","Armature|locomotion_sprint_back",
		"Armature|locomotion_sprint_back_left","Armature|locomotion_sprint_back_right","Armature|locomotion_sprint_fwd",
		"Armature|locomotion_sprint_fwd_left","Armature|locomotion_sprint_fwd_right","Armature|locomotion_stand_to_crouch",
		"Armature|locomotion_strafe_right","Armature|locomotion_walk","Armature|locomotion_walk_back",
		"Armature|locomotion_walk_back-right","Armature|locomotion_walk_fwd","Armature|locomotion_walk_fwd_left",
		"Armature|locomotion_walk_fwd_right"]:
		loco_map[n] = n.substr(9) # strip "Armature|" (9 chars)
	print("=== locomotion.fbx ===")
	_build_from_map("res://art/animations/player/body/source/locomotion.fbx", "Armature/", loco_map, [], loco_lib)
	_set_looping(loco_lib, [
		"locomotion_crouch_idle", "locomotion_crouch_strafe_left", "locomotion_crouch_strafe_right",
		"locomotion_crouch_walk", "locomotion_crouch_walk_back", "locomotion_crouch_walk_back_left",
		"locomotion_crouch_walk_back_right", "locomotion_crouch_walk_fwd_left", "locomotion_crouch_walk_fwd_right",
		"locomotion_idle", "locomotion_sprint_back", "locomotion_sprint_back_left", "locomotion_sprint_back_right",
		"locomotion_sprint_fwd", "locomotion_sprint_fwd_left", "locomotion_sprint_fwd_right",
		"locomotion_strafe_right", "locomotion_walk", "locomotion_walk_back", "locomotion_walk_back-right",
		"locomotion_walk_fwd", "locomotion_walk_fwd_left", "locomotion_walk_fwd_right"])
	var e1 := ResourceSaver.save(loco_lib, OUT_DIR + "locomotion_lib.res")
	print("saved locomotion_lib.res err=", e1)

	# ---- Combat.fbx -> rifle_lib.res (rifle/SMG/shotgun combat set) ----
	var rifle_lib := AnimationLibrary.new()
	var rifle_map := {
		"Armature|combat_ads_idle": "combat_ads_idle",
		"Armature|combat_fire_rifle": "combat_fire_rifle",
		"Armature|combat_fire_rifle_crouch": "combat_fire_rifle_crouch",
		"Armature|combat_fire_rifle_crouch_walk": "combat_fire_rifle_crouch_walk",
		"Armature|combat_fire_rifle_sprint": "combat_fire_rifle_sprint",
		"Armature|combat_fire_rifle_walk": "combat_fire_rifle_walk",
		"Armature|combat_holster_rifle": "combat_holster_rifle",
		"Armature|combat_jump": "combat_jump",
		"Armature|combat_jump_back": "combat_jump_back",
		"Armature|combat_jump_down": "combat_jump_down",
		"Armature|combat_jump_fwd": "combat_jump_fwd",
		"Armature|combat_jump_rifle": "combat_jump_rifle",
		"Armature|combat_reload_rifle": "combat_reload_rifle",
		"Armature|combat_reload_rifle_crouch": "combat_reload_rifle_crouch",
		"Armature|combat_reload_rifle_sprint": "combat_reload_rifle_sprint",
		"Armature|combat_reload_rifle_walk": "combat_reload_rifle_walk",
		"Armature|combat_turn_left": "combat_turn_left",
		"Armature|combat_turn_right": "combat_turn_right",
	}
	print("=== Combat.fbx ===")
	_build_from_map("res://art/animations/player/body/source/Combat.fbx", "Armature/", rifle_map,
		["Armature|Driving", "Armature|Entering Car"], rifle_lib)
	_set_looping(rifle_lib, ["combat_ads_idle", "combat_jump"])
	var e2 := ResourceSaver.save(rifle_lib, OUT_DIR + "rifle_lib.res")
	print("saved rifle_lib.res err=", e2, "  (skipped 'Driving' and 'Entering Car' - vehicle clips, not body combat)")

	# ---- death.fbx -> death_lib.res ----
	var death_lib := AnimationLibrary.new()
	var death_map := {
		"Armature|Crouch Death": "death_crouch",
		"Armature|Death Crouching Headshot Front": "death_crouch_headshot_front",
		"Armature|Death From Back Headshot": "death_headshot_back",
		"Armature|Death From Front Headshot": "death_headshot_front",
		"Armature|Death From The Back": "death_back",
		"Armature|Death From The Front": "death_front",
		"Armature|Rifle Run To Dying": "death_rifle_run",
		"Armature|Walking To Dying": "death_walking",
	}
	print("=== death.fbx ===")
	_build_from_map("res://art/animations/player/body/source/death.fbx", "Armature/", death_map, [], death_lib)
	var e3 := ResourceSaver.save(death_lib, OUT_DIR + "death_lib.res")
	print("saved death_lib.res err=", e3)

	# ---- pistol pack (21 single-clip files, all literally named "mixamo_com") -> pistol_lib.res ----
	var pistol_lib := AnimationLibrary.new()
	var pistol_files := {
		"pistol idle.fbx": "pistol_idle",
		"pistol walk.fbx": "pistol_walk",
		"pistol run.fbx": "pistol_run",
		"pistol strafe.fbx": "pistol_strafe",
		"pistol kneeling idle.fbx": "pistol_kneel_idle",
		"pistol kneel to stand.fbx": "pistol_kneel_to_stand",
		"pistol stand to kneel.fbx": "pistol_stand_to_kneel",
		"pistol jump.fbx": "pistol_jump",
		"pistol walk backward.fbx": "pistol_walk_back",
		"pistol run backward.fbx": "pistol_run_back",
		"pistol walk arc.fbx": "pistol_walk_turn",
		"pistol run arc.fbx": "pistol_run_turn",
		"pistol walk backward arc.fbx": "pistol_walk_back_turn",
		"pistol run backward arc.fbx": "pistol_run_back_turn",
	}
	print("=== pistol pack (consolidating 'mixamo_com' x14 files) ===")
	for fname in pistol_files.keys():
		var src = "res://art/animations/player/body/source/pistol/" + fname
		var ap := _load_player(src)
		if ap == null:
			printerr("  !! no AnimationPlayer in ", fname)
			continue
		var list = ap.get_animation_list()
		if list.size() == 0:
			printerr("  !! no animation in ", fname)
			continue
		var a: Animation = ap.get_animation(list[0])
		var rewritten: Animation = _rewrite_track_paths(a, "") # already "Skeleton3D:..." - no prefix to strip
		var rm := _strip_horizontal_root_motion(rewritten)
		var clean: String = pistol_files[fname]
		var err := pistol_lib.add_animation(clean, rewritten)
		if err != OK:
			printerr("  failed ", clean, " err=", err)
		else:
			var tag = "  [root motion removed, was %.2fm]" % rm["drift"] if rm["fixed"] else ""
			print("  + %-24s <- %s (%.2fs, %d tracks)%s" % [clean, fname, rewritten.length, rewritten.get_track_count(), tag])
	_set_looping(pistol_lib, ["pistol_idle", "pistol_kneel_idle", "pistol_run", "pistol_run_back",
		"pistol_run_back_turn", "pistol_run_turn", "pistol_strafe", "pistol_walk", "pistol_walk_back",
		"pistol_walk_back_turn", "pistol_walk_turn"])
	var e4 := ResourceSaver.save(pistol_lib, OUT_DIR + "pistol_lib.res")
	print("saved pistol_lib.res err=", e4)

	print("\n=== DONE building libraries ===")
	quit()
