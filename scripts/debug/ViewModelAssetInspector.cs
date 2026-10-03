using Godot;
using System;

/// <summary>Headless geometry and animation diagnostics for the imported viewmodel assets.</summary>
public partial class ViewModelAssetInspector : Node
{
	[Export] public Godot.Collections.Array<string> ScenePaths { get; set; } = new()
	{
		"res://art/animations/player/combat/AK/source/AK FPS Anims.fbx",
		"res://art/animations/player/combat/fn 502/source/Pistol FPS Anims.fbx",
		"res://art/animations/player/combat/benelli m4/source/Shot Gun FPS Anims.fbx",
	};
	public override void _Ready()
	{
		foreach (string path in ScenePaths) InspectPackedScene(path, inspectAnimations: true);
		GetTree().Quit();
	}

	private void InspectPackedScene(string path, bool inspectAnimations)
	{
		GD.Print($"VIEWMODEL INSPECT {path}");
		var packed = GD.Load<PackedScene>(path);
		if (packed == null)
		{
			GD.PushWarning($"ViewModelAssetInspector could not load {path}.");
			return;
		}

		Node instance = packed.Instantiate();
		AddChild(instance);
		Skeleton3D? skeleton = FindFirst<Skeleton3D>(instance);
		AnimationPlayer? animationPlayer = FindFirst<AnimationPlayer>(instance);
		Camera3D? sourceCamera = FindFirst<Camera3D>(instance);
		GD.Print($"  ROOT {instance.Name} transform={(instance is Node3D root ? root.GlobalTransform : Transform3D.Identity)}");
		if (sourceCamera != null)
			GD.Print($"  SOURCE_CAMERA {sourceCamera.GetPath()} world={sourceCamera.GlobalTransform} near={sourceCamera.Near}");
		if (skeleton != null) InspectSkeleton(skeleton, inspectAnimations ? animationPlayer : null);
		InspectMeshes(instance, sourceCamera?.GlobalPosition ?? Vector3.Zero, skeleton);
		if (inspectAnimations && animationPlayer != null && skeleton != null) InspectAnimations(animationPlayer, skeleton);
		instance.QueueFree();
	}

	private static void InspectSkeleton(Skeleton3D skeleton, AnimationPlayer? animationPlayer)
	{
		GD.Print($"  SKELETON {skeleton.GetPath()} global={skeleton.GlobalTransform} autoplay='{animationPlayer?.Autoplay}' current='{animationPlayer?.CurrentAnimation}'");
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
		{
			string name = skeleton.GetBoneName(i);
			if (animationPlayer == null || !IsArmBone(name)) continue;
			Transform3D rest = skeleton.GetBoneRest(i);
			GD.Print($"  REST_BONE {name} pos={rest.Origin} rot={rest.Basis.GetRotationQuaternion()} scale={rest.Basis.Scale}");
		}
	}

	private static void InspectMeshes(Node root, Vector3 cameraPosition, Skeleton3D? skeleton)
	{
		foreach (MeshInstance3D mesh in FindAll<MeshInstance3D>(root))
		{
			Aabb local = mesh.Mesh?.GetAabb() ?? mesh.GetAabb();
			Aabb world = TransformAabb(local, mesh.GlobalTransform);
			Vector3 size = world.Size;
			float distance = world.GetCenter().DistanceTo(cameraPosition);
			string skin = mesh.Skin == null ? "none" : $"binds={mesh.Skin.GetBindCount()}";
			GD.Print($"  MESH {mesh.GetPath()} world_aabb={world} size={size} center_distance={distance:0.###} skeleton='{mesh.Skeleton}' skin={skin}");
			if (mesh.Skin != null && skeleton != null && mesh.Name.ToString() is "sleeve" or "hardknuckle")
				for (int bind = 0; bind < mesh.Skin.GetBindCount(); bind++)
				{
					string bindName = mesh.Skin.GetBindName(bind).ToString();
					int boneByName = skeleton.FindBone(bindName);
					int bindBone = mesh.Skin.GetBindBone(bind);
					GD.Print($"  SKIN_BIND mesh={mesh.Name} slot={bind} name='{bindName}' bind_bone={bindBone} skeleton_name_index={boneByName}");
				}
		}
	}

	private static void InspectAnimations(AnimationPlayer player, Skeleton3D skeleton)
	{
		foreach (string clipName in player.GetAnimationList())
		{
			Animation animation = player.GetAnimation(clipName);
			GD.Print($"  ANIMATION {clipName} loop={animation.LoopMode} length={animation.Length:0.###} tracks={animation.GetTrackCount()}");
			InspectScaleTracks(animation);
			if (clipName.Contains("FN_", StringComparison.OrdinalIgnoreCase))
			{
				SamplePose(player, animation, skeleton, clipName, 0.0);
				SamplePose(player, animation, skeleton, clipName, Math.Max(0.0, animation.Length - 0.0001));
			}
		}
		player.Stop();
		skeleton.ResetBonePoses();
		GD.Print($"  RESET_COMPLETE current='{player.CurrentAnimation}'");
	}

	private static void SamplePose(AnimationPlayer player, Animation animation, Skeleton3D skeleton, string clipName, double time)
	{
		skeleton.ResetBonePoses();
		player.Play(clipName, 0.0, 0.0f);
		player.Seek(time, true);
		string edge = time < 0.001 ? "FIRST" : "LAST";
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
		{
			string bone = skeleton.GetBoneName(i);
			if (!IsArmBone(bone)) continue;
			Vector3 position = skeleton.GetBonePosePosition(i);
			Quaternion rotation = skeleton.GetBonePoseRotation(i);
			Vector3 scale = skeleton.GetBonePoseScale(i);
			GD.Print($"  {edge}_POSE {clipName} bone={bone} t={time:0.###} pos={position} rot={rotation} scale={scale}");
		}
	}

	private static Aabb TransformAabb(Aabb box, Transform3D transform)
	{
		Vector3 min = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
		Vector3 max = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
		for (int x = 0; x < 2; x++)
		for (int y = 0; y < 2; y++)
		for (int z = 0; z < 2; z++)
		{
			Vector3 corner = box.Position + new Vector3(box.Size.X * x, box.Size.Y * y, box.Size.Z * z);
			Vector3 point = transform * corner;
			min = min.Min(point);
			max = max.Max(point);
		}
		return new Aabb(min, max - min);
	}

	private static void InspectScaleTracks(Animation animation)
	{
		for (int track = 0; track < animation.GetTrackCount(); track++)
		{
			string path = animation.TrackGetPath(track).ToString();
			bool safetyTrack = path.Contains("Safety", StringComparison.OrdinalIgnoreCase);
			if (animation.TrackGetType(track) != Animation.TrackType.Scale3D && !safetyTrack) continue;
			GD.Print($"  SCALE_TRACK {path} type={animation.TrackGetType(track)}");
			for (int key = 0; key < animation.TrackGetKeyCount(track); key++)
				GD.Print($"    key{key} t={animation.TrackGetKeyTime(track, key):0.###} value={animation.TrackGetKeyValue(track, key)}");
		}
	}

	private static bool IsArmBone(string name)
	{
		string lower = name.ToLowerInvariant();
		return lower.Contains("arm") || lower.Contains("forearm") || lower.Contains("hand")
			|| lower.Contains("sleeve") || lower.Contains("glove");
	}

	private static T? FindFirst<T>(Node node) where T : Node
	{
		if (node is T match) return match;
		foreach (Node child in node.GetChildren())
		{
			T? found = FindFirst<T>(child);
			if (found != null) return found;
		}
		return null;
	}

	private static System.Collections.Generic.IEnumerable<T> FindAll<T>(Node node) where T : Node
	{
		if (node is T match) yield return match;
		foreach (Node child in node.GetChildren())
			foreach (T found in FindAll<T>(child)) yield return found;
	}
}
