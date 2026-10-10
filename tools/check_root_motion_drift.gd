extends SceneTree

func _init():
	var res = load("res://art/characters/AshenCorvus.fbx")
	var inst: Node3D = res.instantiate()
	var skel: Skeleton3D = inst.find_child("Skeleton3D", true, false)
	var ap := AnimationPlayer.new()
	ap.root_node = NodePath("..")
	inst.add_child(ap)
	for libname in ["locomotion_lib", "pistol_lib", "rifle_lib", "death_lib"]:
		var lib: AnimationLibrary = load("res://art/animations/player/body/%s.res" % libname)
		ap.add_animation_library(libname, lib)

	var hips_idx = skel.find_bone("mixamorig_Hips")

	for libname in ["locomotion_lib", "pistol_lib", "rifle_lib", "death_lib"]:
		var lib: AnimationLibrary = load("res://art/animations/player/body/%s.res" % libname)
		for clip in lib.get_animation_list():
			var full = libname + "/" + clip
			var anim: Animation = ap.get_animation(full)
			ap.play(full)
			ap.seek(0.0, true)
			var p0: Vector3 = skel.get_bone_pose_position(hips_idx)
			ap.seek(anim.length, true)
			var p1: Vector3 = skel.get_bone_pose_position(hips_idx)
			ap.stop()
			var drift_xz = Vector2(p1.x - p0.x, p1.z - p0.z).length()
			var flag = "  <-- ROOT MOTION (needs in-place fix)" if drift_xz > 0.05 else ""
			print("%-45s drift_xz=%.3fm%s" % [full, drift_xz, flag])

	quit()
