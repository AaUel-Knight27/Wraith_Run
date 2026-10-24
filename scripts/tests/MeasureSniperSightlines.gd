@tool
extends SceneTree

func _init():
	var scene = load("res://art/animations/player/combat/Sniper/source/BoltActionRifle.fbx")
	var root = Node3D.new()
	get_root().add_child(root)
	var cam = Camera3D.new()
	root.add_child(cam)
	var inst: Node3D = scene.instantiate()
	cam.add_child(inst)
	
	# Align rig to camera
	var rig_cam: Camera3D = inst.find_children("*", "Camera3D", true, false)[0]
	rig_cam.force_update_transform()
	inst.force_update_transform()
	var cam_in_rig = inst.global_transform.affine_inverse() * rig_cam.global_transform
	var rig_wanted = cam_in_rig.affine_inverse()
	inst.transform = rig_wanted
	
	var ap: AnimationPlayer = inst.find_child("AnimationPlayer", true, false)
	var rifle_mesh: MeshInstance3D = inst.find_child("Rifle", true, false)
	
	print("--- MEASURING RIG CAMERA ALIGNED SIGHTLINE ---")
	for anim_name in ["Rig|SRifle_Idle", "Rig|SRifle_Shot_nosight", "Rig|SRifle_Shot_sight"]:
		ap.play(anim_name)
		ap.seek(0.0, true)
		inst.force_update_transform()
		rifle_mesh.force_update_transform()
		var aabb = rifle_mesh.get_aabb()
		var rifle_center_cam = cam.global_transform.affine_inverse() * (rifle_mesh.global_transform * aabb.get_center())
		print("Anim '", anim_name, "': Rifle center relative to cam = ", rifle_center_cam)
		
	quit()
