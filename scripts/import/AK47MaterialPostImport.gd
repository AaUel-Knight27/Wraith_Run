@tool
extends EditorScenePostImport

const MATERIAL := preload("res://art/weapons/materials/AK47_Material.tres")

func _post_import(scene: Node) -> Object:
	_apply_material(scene)
	return scene

func _apply_material(node: Node) -> void:
	if node is MeshInstance3D:
		node.material_override = MATERIAL
	for child in node.get_children():
		_apply_material(child)
