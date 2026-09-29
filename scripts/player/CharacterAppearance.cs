using System;
using Godot;

/// <summary>
/// Turns an operator's Jacket/Pants/Accent colours into an actual material on screen.
/// Static rather than a node: the only state is the shader (loaded once) and the material
/// instance it hands back, and the caller (CharacterLoadout, or the lobby's OperatorStage)
/// already owns the character's lifetime.
///
/// The tint shader wants a mask in the mesh's vertex colours (R = jacket, G = pants,
/// B = boots/gloves, black = skin). manny.glb ships with plain white vertex colours, so before
/// tinting, PaintRegions writes that mask onto a copy of the mesh from the skin weights: every
/// vertex is coloured by how much of it is driven by arm/spine bones (jacket), leg/hip bones
/// (pants) or hand/foot bones (accent). Head and neck stay unpainted, i.e. skin. The imported
/// file on disk is never touched.
/// </summary>
public static class CharacterAppearance
{
	private const string ShaderPath = "res://art/shaders/character_tint.gdshader";
	private const string PaintedMeta = "wr_regions_painted";

	/// <summary>Skin tone used for the unpainted head/neck.</summary>
	public static readonly Color SkinTone = new(0.66f, 0.52f, 0.42f);

	private static Shader? _shader;

	/// <summary>Kept for existing callers: applies the operator's own stored colours.</summary>
	public static void Apply(Node root, CharacterData data) =>
		Apply(root, data.JacketColor, data.PantsColor, data.AccentColor);

	/// <summary>Finds the first MeshInstance3D under root (depth-first) and applies a tint
	/// material as a surface override. Searches rather than assumes a fixed path, since the
	/// internal node layout under an imported glb (Armature/Skeleton3D/...) varies by how the
	/// source file was rigged and exported. Returns the material so a caller can recolour it
	/// live with SetShaderParameter, or null when nothing could be applied.</summary>
	public static ShaderMaterial? Apply(Node root, Color jacket, Color pants, Color accent)
	{
		var mesh = FindFirstMeshInstance(root);
		if (mesh == null)
		{
			GD.PushWarning($"CharacterAppearance: no MeshInstance3D found under {root.Name}.");
			return null;
		}

		_shader ??= ResourceLoader.Load<Shader>(ShaderPath);
		if (_shader == null)
		{
			GD.PushWarning($"CharacterAppearance: could not load {ShaderPath}.");
			return null;
		}

		bool painted = PaintRegions(mesh);

		var material = new ShaderMaterial { Shader = _shader };
		material.SetShaderParameter("jacket_color", jacket);
		material.SetShaderParameter("pants_color", pants);
		material.SetShaderParameter("accent_color", accent);
		material.SetShaderParameter("skin_color", SkinTone);
		// If the region mask could not be written the whole body wears the jacket colour rather
		// than the mask-sum blow-out white vertex colours would cause.
		material.SetShaderParameter("use_regions", painted);

		int surfaces = mesh.Mesh?.GetSurfaceCount() ?? 0;
		for (int surface = 0; surface < surfaces; surface++)
			mesh.SetSurfaceOverrideMaterial(surface, material);
		return material;
	}

	// ---------------------------------------------------------------------------------------
	// Region painting
	// ---------------------------------------------------------------------------------------

	private const int RegionNone = 0, RegionJacket = 1, RegionPants = 2, RegionAccent = 3;

	private static int Classify(string boneName)
	{
		string n = boneName.ToLowerInvariant();
		if (n.Contains("head") || n.Contains("neck") || n.Contains("eye") || n.Contains("jaw") || n.Contains("hair"))
			return RegionNone;
		if (n.Contains("hand") || n.Contains("foot") || n.Contains("toe")) return RegionAccent; // fingers are LeftHandIndex1 etc.
		if (n.Contains("leg") || n.Contains("hips") || n.Contains("pelvis")) return RegionPants;
		if (n.Contains("spine") || n.Contains("shoulder") || n.Contains("arm") || n.Contains("chest")) return RegionJacket;
		return RegionNone;
	}

	/// <summary>Replaces the instance's mesh with a copy whose vertex colours hold the region
	/// mask. Returns false (and leaves the mesh alone) if anything about the imported mesh is not
	/// what this expects - the caller then falls back to a single flat colour.</summary>
	private static bool PaintRegions(MeshInstance3D instance)
	{
		if (instance.HasMeta(PaintedMeta)) return true;
		try
		{
			Mesh? source = instance.Mesh;
			if (source == null || source.GetSurfaceCount() == 0) return false;

			int[] regionOfBind = BuildRegionTable(instance);
			if (regionOfBind.Length == 0) return false;

			var painted = new ArrayMesh();
			long paintedVertices = 0, totalVertices = 0;
			for (int surface = 0; surface < source.GetSurfaceCount(); surface++)
			{
				var arrays = source.SurfaceGetArrays(surface);
				int[] bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
				float[] weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
				Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
				if (vertices.Length == 0 || bones.Length == 0 || bones.Length != weights.Length) return false;
				int perVertex = bones.Length / vertices.Length;
				if (perVertex <= 0 || perVertex * vertices.Length != bones.Length) return false;

				var colors = new Color[vertices.Length];
				for (int v = 0; v < vertices.Length; v++)
				{
					float jacket = 0f, pants = 0f, accent = 0f, total = 0f;
					for (int k = 0; k < perVertex; k++)
					{
						float w = weights[v * perVertex + k];
						int bind = bones[v * perVertex + k];
						if (w <= 0f) continue;
						total += w;
						if (bind < 0 || bind >= regionOfBind.Length) continue;
						switch (regionOfBind[bind])
						{
							case RegionJacket: jacket += w; break;
							case RegionPants: pants += w; break;
							case RegionAccent: accent += w; break;
						}
					}
					if (total > 0f) { jacket /= total; pants /= total; accent /= total; }
					colors[v] = new Color(jacket, pants, accent, 1.0f);
					if (jacket + pants + accent > 0.5f) paintedVertices++;
					totalVertices++;
				}
				arrays[(int)Mesh.ArrayType.Color] = colors;
				// SurfaceGetPrimitiveType lives on ArrayMesh, not the Mesh base class. Imported meshes are
				// ArrayMeshes; anything else is treated as triangles.
				var primitive = (source as ArrayMesh)?.SurfaceGetPrimitiveType(surface) ?? Mesh.PrimitiveType.Triangles;
				painted.AddSurfaceFromArrays(primitive, arrays);
			}

			// A mask that covers almost nothing means the bone names were not recognised.
			if (totalVertices == 0 || paintedVertices < totalVertices / 10)
			{
				GD.PushWarning("CharacterAppearance: bone names did not match any clothing region; using one flat colour.");
				return false;
			}

			instance.Mesh = painted;
			instance.SetMeta(PaintedMeta, true);
			return true;
		}
		catch (Exception e)
		{
			GD.PushWarning($"CharacterAppearance: could not paint clothing regions ({e.Message}); using one flat colour.");
			return false;
		}
	}

	/// <summary>Region for every bone index the mesh's weights can refer to. With a Skin
	/// resource those indices are skin bind indices (named by use_named_skins on import);
	/// without one they are skeleton bone indices.</summary>
	private static int[] BuildRegionTable(MeshInstance3D instance)
	{
		Skin? skin = instance.Skin;
		Skeleton3D? skeleton = instance.GetNodeOrNull<Skeleton3D>(instance.Skeleton);
		int count = skin != null ? skin.GetBindCount() : (skeleton?.GetBoneCount() ?? 0);
		var table = new int[count];
		for (int i = 0; i < count; i++)
		{
			string name = string.Empty;
			if (skin != null)
			{
				name = skin.GetBindName(i).ToString();
				if (string.IsNullOrEmpty(name) && skeleton != null)
				{
					int bone = skin.GetBindBone(i);
					if (bone >= 0 && bone < skeleton.GetBoneCount()) name = skeleton.GetBoneName(bone);
				}
			}
			else if (skeleton != null) name = skeleton.GetBoneName(i);
			table[i] = Classify(name);
		}
		return table;
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
