using Godot;

/// <summary>
/// Turns CharacterData's JacketColor/PantsColor/AccentColor into an actual material on screen.
/// Static rather than a node: the only state is the shader (loaded once) and the material
/// instance it hands back, and the caller (CharacterLoadout) already owns the character's
/// lifetime.
/// </summary>
public static class CharacterAppearance
{
	private const string ShaderPath = "res://art/shaders/character_tint.gdshader";
	private static Shader? _shader;

	/// <summary>Finds the first MeshInstance3D under root (depth-first) and applies a tint
	/// material built from data's colors as a surface override. Searches rather than assumes a
	/// fixed path, since the internal node layout under an imported glb (Armature/Skeleton3D/...)
	/// varies by how the source file was rigged and exported - true for manny.glb today and will
	/// stay true for every character the Blender addon produces later.</summary>
	public static void Apply(Node root, CharacterData data)
	{
		var mesh = FindFirstMeshInstance(root);
		if (mesh == null)
		{
			GD.PushWarning($"CharacterAppearance: no MeshInstance3D found under {root.Name} for '{data.CharacterId}'.");
			return;
		}

		_shader ??= ResourceLoader.Load<Shader>(ShaderPath);
		if (_shader == null)
		{
			GD.PushWarning($"CharacterAppearance: could not load {ShaderPath}.");
			return;
		}

		var material = new ShaderMaterial { Shader = _shader };
		material.SetShaderParameter("jacket_color", data.JacketColor);
		material.SetShaderParameter("pants_color", data.PantsColor);
		material.SetShaderParameter("accent_color", data.AccentColor);

		for (int surface = 0; surface < mesh.GetSurfaceOverrideMaterialCount(); surface++)
			mesh.SetSurfaceOverrideMaterial(surface, material);
		// A mesh with zero surfaces listed still has at least one to paint in practice (manny.glb
		// has exactly one, "Manny") - GetSurfaceOverrideMaterialCount mirrors the imported mesh's
		// own surface count, so this loop already covers it; the guard below only matters for an
		// unusual 0-surface mesh.
		if (mesh.GetSurfaceOverrideMaterialCount() == 0 && mesh.Mesh != null)
			for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
				mesh.SetSurfaceOverrideMaterial(surface, material);
	}

	private static MeshInstance3D? FindFirstMeshInstance(Node node)
	{
		if (node is MeshInstance3D mesh) return mesh;
		foreach (Node child in node.GetChildren())
		{
			var found = FindFirstMeshInstance(child);
			if (found != null) return found;
		}
		return null;
	}
}
