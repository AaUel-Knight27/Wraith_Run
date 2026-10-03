using Godot;
using System;
using System.Collections.Generic;

/// <summary>Runtime viewmodel assembled from an animation rig and independent weapon model.</summary>
public partial class ViewModelBase : Node3D
{
	[Export] public ViewModelWeaponData? Data { get; set; }
	[Export] public Godot.Collections.Dictionary<string, string> ClipMap { get; set; } = new();
	[Export] public Vector3 PositionOffset { get; set; } = Vector3.Zero;
	[Export] public Vector3 RotationOffsetDegrees { get; set; } = Vector3.Zero;
	[Export] public Vector3 ScaleOffset { get; set; } = Vector3.One;
	[Export] public bool NormalizeArms { get; set; } = true;
	[Export] public bool ShowMeshBounds { get; set; }
	[Export] public bool ShowBoneAxes { get; set; }

	private Node3D? _rig;
	private Skeleton3D? _skeleton;
	private AnimationPlayer? _animationPlayer;
	private Node3D? _muzzlePoint;
	private readonly Dictionary<string, string> _resolvedClips = new();
	private string _idleClip = string.Empty;
	private string _activeMovementClip = string.Empty;
	private string _activeMovementGeneric = "idle";
	private float _normalizationScale = 1.0f;
	private Camera3D? _viewCamera;

	public Skeleton3D? Skeleton => _skeleton;
	public Node3D? MuzzlePoint => _muzzlePoint;
	public IReadOnlyDictionary<string, string> ResolvedClips => _resolvedClips;

	public void Configure(ViewModelWeaponData data)
	{
		Data = data;
		ClipMap = data.ClipMap;
		PositionOffset = data.ViewPosition;
		RotationOffsetDegrees = data.ViewRotationDegrees;
		ScaleOffset = data.ViewScale;
	}

	public override void _Ready()
	{
		Position = PositionOffset;
		RotationDegrees = RotationOffsetDegrees;
		Scale = ScaleOffset;
		_viewCamera = GetParent() as Camera3D;
		if (Data?.RigScene == null)
		{
			GD.PushWarning($"{Name}: missing rig scene in ViewModelWeaponData.");
			return;
		}
		_rig = Data.RigScene.Instantiate<Node3D>();
		_rig.Name = "AnimationRig";
		AddChild(_rig);
		_skeleton = FindChild("Skeleton3D", true, false) as Skeleton3D;
		_animationPlayer = FindChild("AnimationPlayer", true, false) as AnimationPlayer;
		if (_skeleton == null || _animationPlayer == null)
		{
			GD.PushWarning($"{Name}: rig '{Data.RigScene.ResourcePath}' is missing Skeleton3D or AnimationPlayer.");
			return;
		}

		// Never let an imported autoplay/default animation leave a weapon halfway through a clip.
		_animationPlayer.Stop();
		_skeleton.ResetBonePoses();
		foreach (Node child in GetDescendants(_rig))
		{
			if (child is Camera3D camera) { camera.Current = false; camera.Visible = false; }
			if (child is Light3D light) light.Visible = false;
			if (child is MeshInstance3D mesh)
			{
				mesh.ExtraCullMargin = Mathf.Max(mesh.ExtraCullMargin, Data.CullMargin);
				// Each imported pack bundles a weapon into its own rig. Keep only the configured
				// arm/sleeve/glove meshes; the selected project weapon is attached below.
				if (Data.ArmMeshNames.Count > 0 && !ContainsName(Data.ArmMeshNames, mesh.Name)) mesh.Visible = false;
				if (Data.HideOriginalWeapon && ContainsName(Data.OriginalWeaponMeshNames, mesh.Name)) mesh.Visible = false;
				if (Data.HideOriginalMagazine && ContainsName(Data.OriginalMagazineMeshNames, mesh.Name)) mesh.Visible = false;
			}
		}

		if (NormalizeArms) NormalizeRig();
		ResolveAndConfigureClips();
		_animationPlayer.AnimationFinished += OnAnimationFinished;
		AttachWeaponModel();
		RefreshDebugGeometry();
		GD.Print($"VIEWMODEL weapon='{Data.WeaponName}' rig='{Data.RigScene.ResourcePath}' attach='{Data.AttachBoneName}' clips={FormatResolvedClips()} arm_extent={GetArmExtent():F4}m normalization={_normalizationScale:F6}");
		ReportMeshBounds();
		if (!string.IsNullOrEmpty(_idleClip)) Play("idle");
	}

	private void NormalizeRig()
	{
		if (Data == null || _rig == null) return;
		float current = MeasureMeshExtent(_rig, Data.ArmMeshNames);
		float reference = current;
		if (Data.NormalizationReferenceRig != null)
		{
			Node referenceRoot = Data.NormalizationReferenceRig.Instantiate();
			AddChild(referenceRoot);
			reference = MeasureMeshExtent(referenceRoot, Data.NormalizationReferenceArmMeshNames);
			referenceRoot.Free();
		}
		if (current <= 0.000001f || reference <= 0.000001f)
		{
			GD.PushWarning($"{Name}: arm normalization bounds unavailable (current={current:F6}, reference={reference:F6}).");
			return;
		}
		_normalizationScale = reference / current;
		_rig.Scale *= Vector3.One * _normalizationScale;
	}

	private void ResolveAndConfigureClips()
	{
		if (_animationPlayer == null || Data == null) return;
		foreach (var mapping in ClipMap)
		{
			string? resolved = ResolveClip(mapping.Value);
			if (resolved == null)
			{
				GD.PushWarning($"{Data.WeaponName}: clip map '{mapping.Key}' -> '{mapping.Value}' did not resolve.");
				continue;
			}
			_resolvedClips[mapping.Key] = resolved;
			Animation animation = _animationPlayer.GetAnimation(resolved);
			if (mapping.Key is "idle" or "walk" or "run") animation.LoopMode = Animation.LoopModeEnum.Linear;
		}
		if (!string.IsNullOrEmpty(Data.AttachBoneName) && _skeleton?.FindBone(Data.AttachBoneName) < 0)
			GD.PushWarning($"{Data.WeaponName}: attach bone '{Data.AttachBoneName}' is missing in rig '{Data.RigScene?.ResourcePath}'.");
		if (!_resolvedClips.ContainsKey("idle")) GD.PushWarning($"{Data.WeaponName}: no idle clip configured.");
	}

	private void AttachWeaponModel()
	{
		if (Data == null || _skeleton == null || Data.WeaponModelScene == null || string.IsNullOrEmpty(Data.AttachBoneName)) return;
		var attachment = MakeAttachment("WeaponAttachment", Data.AttachBoneName);
		if (attachment == null) return;
		var model = Data.WeaponModelScene.Instantiate<Node3D>();
		model.Name = "WeaponModel";
		attachment.AddChild(model);
		model.Position = Data.AttachPosition;
		model.RotationDegrees = Data.AttachRotationDegrees;
		model.Scale = Data.AttachScale;
		GD.Print($"VIEWMODEL ATTACH weapon='{Data.WeaponName}' model='{Data.WeaponModelScene.ResourcePath}' scene_children={model.GetChildCount()} visible={model.Visible}");
		if (!string.IsNullOrEmpty(Data.MagazineNodeName) && !string.IsNullOrEmpty(Data.MagazineBoneName))
		{
			Node? magazineNode = model.FindChild(Data.MagazineNodeName, true, false);
			if (magazineNode is Node3D magazine)
			{
				var magazineAttachment = MakeAttachment("MagazineAttachment", Data.MagazineBoneName);
				if (magazineAttachment != null)
				{
					Transform3D global = magazine.GlobalTransform;
					magazine.GetParent()?.RemoveChild(magazine);
					magazineAttachment.AddChild(magazine);
					magazine.GlobalTransform = global;
					magazine.Position += Data.MagazinePosition;
					magazine.RotationDegrees += Data.MagazineRotationDegrees;
					magazine.Scale *= Data.MagazineScale;
				}
			}
			else GD.PushWarning($"{Data.WeaponName}: magazine node '{Data.MagazineNodeName}' not found in {Data.WeaponModelScene.ResourcePath}.");
		}
		_muzzlePoint = new Node3D { Name = "MuzzlePoint", Position = Data.MuzzlePointOffset };
		model.AddChild(_muzzlePoint);
	}

	private BoneAttachment3D? MakeAttachment(string attachmentName, string boneName)
	{
		if (_skeleton == null || _skeleton.FindBone(boneName) < 0) return null;
		var attachment = new BoneAttachment3D { Name = attachmentName, BoneName = boneName };
		_skeleton.AddChild(attachment);
		return attachment;
	}

	public bool Play(string name, float speed = 1.0f)
	{
		if (_animationPlayer == null) return false;
		if (!_resolvedClips.TryGetValue(name, out string? clip)) { PlayIdleFallback(speed); return false; }
		if (name is "idle" or "walk" or "run") { _activeMovementClip = clip; _activeMovementGeneric = name; }
		float multiplier = Data?.SpeedMultiplier(name) ?? 1.0f;
		_animationPlayer.Play(clip, 0.08, Mathf.Max(0.01f, speed * multiplier));
		if (name == "idle" && Data != null && Data.IdlePoseTime >= 0 && clip.EndsWith("FN_Walk", StringComparison.Ordinal))
		{
			_animationPlayer.Seek(Data.IdlePoseTime, true);
			_animationPlayer.Pause();
		}
		return true;
	}

	private void OnAnimationFinished(StringName animationName)
	{
		if (_animationPlayer == null || string.IsNullOrEmpty(_activeMovementClip) || animationName.ToString() == _activeMovementClip) return;
		Play(_activeMovementGeneric);
	}

	public float GetClipLength(string name) => _animationPlayer != null && _resolvedClips.TryGetValue(name, out string? clip) ? (float)_animationPlayer.GetAnimation(clip).Length : 0;
	public bool HasResolvedClip(string name) => _resolvedClips.ContainsKey(name);
	public float GetArmExtent() => Data == null || _rig == null ? 0 : MeasureMeshExtent(_rig, Data.ArmMeshNames);

	public void SetDebugOptions(bool axes, bool bounds)
	{
		ShowBoneAxes = axes;
		ShowMeshBounds = bounds;
		RefreshDebugGeometry();
	}

	private void PlayIdleFallback(float speed)
	{
		if (_animationPlayer != null && !string.IsNullOrEmpty(_idleClip)) _animationPlayer.Play(_idleClip, 0.08, Mathf.Max(0.01f, speed));
	}

	private string? ResolveClip(string requested)
	{
		if (_animationPlayer == null) return null;
		if (_animationPlayer.HasAnimation(requested)) return requested;
		string[] names = _animationPlayer.GetAnimationList();
		var exactSuffix = new List<string>();
		foreach (string name in names)
			if (name.EndsWith("|" + requested, StringComparison.Ordinal)) exactSuffix.Add(name);
		if (exactSuffix.Count == 1) return exactSuffix[0];
		if (exactSuffix.Count > 1) { GD.PushWarning($"{Data?.WeaponName}: clip suffix '{requested}' is ambiguous: {string.Join(", ", exactSuffix)}."); return null; }
		var suffix = new List<string>();
		foreach (string name in names) if (name.EndsWith(requested, StringComparison.Ordinal)) suffix.Add(name);
		if (suffix.Count == 1) return suffix[0];
		if (suffix.Count > 1) GD.PushWarning($"{Data?.WeaponName}: clip suffix '{requested}' is ambiguous: {string.Join(", ", suffix)}.");
		return null;
	}

	private static float MeasureMeshExtent(Node root, Godot.Collections.Array<string> names)
	{
		Aabb? bounds = null;
		foreach (Node node in GetDescendants(root))
			if (node is MeshInstance3D mesh && ContainsName(names, mesh.Name))
			{
				Aabb transformed = TransformAabb(mesh.GetAabb(), mesh.GlobalTransform);
				bounds = bounds.HasValue ? bounds.Value.Merge(transformed) : transformed;
			}
		if (!bounds.HasValue) return 0.0f;
		Vector3 size = bounds.Value.Size;
		return Math.Max(size.X, Math.Max(size.Y, size.Z));
	}

	private static Aabb TransformAabb(Aabb aabb, Transform3D transform)
	{
		Vector3 min = aabb.Position; Vector3 max = aabb.End;
		Vector3 first = transform * new Vector3(min.X, min.Y, min.Z);
		Vector3 low = first, high = first;
		for (int i = 1; i < 8; i++)
		{
			Vector3 p = transform * new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
			low = low.Min(p); high = high.Max(p);
		}
		return new Aabb(low, high - low);
	}

	private void RefreshDebugGeometry()
	{
		if (_rig == null) return;
		foreach (Node node in GetDescendants(_rig)) if (node.Name.ToString().StartsWith("__Debug", StringComparison.Ordinal)) node.QueueFree();
		if (ShowBoneAxes && _skeleton != null) AddBoneAxes(_skeleton);
		if (ShowMeshBounds)
			foreach (Node node in GetDescendants(_rig)) if (node is MeshInstance3D mesh && !mesh.Name.ToString().StartsWith("__Debug", StringComparison.Ordinal)) AddBounds(mesh);
	}

	private void ReportMeshBounds()
	{
		if (_rig == null) return;
		foreach (Node node in GetDescendants(_rig))
			if (node is MeshInstance3D mesh && mesh.Visible)
			{
				Aabb box = TransformAabb(mesh.GetAabb(), mesh.GlobalTransform);
				float distance = _viewCamera != null ? box.GetCenter().DistanceTo(_viewCamera.GlobalPosition) : box.GetCenter().Length();
				GD.Print($"VIEWMODEL AABB weapon='{Data?.WeaponName}' mesh='{mesh.Name}' size={box.Size} center_distance_from_camera={distance:F4}m");
			}
	}

	private static void AddBounds(MeshInstance3D mesh)
	{
		var visual = new MeshInstance3D { Name = "__DebugBounds", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
		var lines = new ImmediateMesh();
		lines.SurfaceBegin(Mesh.PrimitiveType.Lines);
		lines.SurfaceSetColor(new Color(1f, 0.65f, 0.05f));
		Aabb aabb = mesh.GetAabb();
		AddBoxLines(lines, aabb.Position, aabb.End);
		lines.SurfaceEnd();
		visual.Mesh = lines;
		visual.MaterialOverride = DebugMaterial(new Color(1f, 0.65f, 0.05f));
		mesh.AddChild(visual);
	}

	private static void AddBoneAxes(Skeleton3D skeleton)
	{
		var visual = new MeshInstance3D { Name = "__DebugBoneAxes", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
		var lines = new ImmediateMesh();
		lines.SurfaceBegin(Mesh.PrimitiveType.Lines);
		for (int i = 0; i < skeleton.GetBoneCount(); i++)
		{
			Transform3D pose = skeleton.GetBoneGlobalPose(i);
			Vector3 origin = pose.Origin;
			lines.SurfaceSetColor(Colors.Red); lines.SurfaceAddVertex(origin); lines.SurfaceAddVertex(origin + pose.Basis.X.Normalized() * 0.08f);
			lines.SurfaceSetColor(Colors.Green); lines.SurfaceAddVertex(origin); lines.SurfaceAddVertex(origin + pose.Basis.Y.Normalized() * 0.08f);
			lines.SurfaceSetColor(Colors.Blue); lines.SurfaceAddVertex(origin); lines.SurfaceAddVertex(origin + pose.Basis.Z.Normalized() * 0.08f);
		}
		lines.SurfaceEnd();
		visual.Mesh = lines;
		visual.MaterialOverride = DebugMaterial(Colors.White);
		skeleton.AddChild(visual);
	}

	private static void AddBoxLines(ImmediateMesh lines, Vector3 min, Vector3 max)
	{
		Vector3[] c = { new(min.X,min.Y,min.Z), new(max.X,min.Y,min.Z), new(min.X,max.Y,min.Z), new(max.X,max.Y,min.Z), new(min.X,min.Y,max.Z), new(max.X,min.Y,max.Z), new(min.X,max.Y,max.Z), new(max.X,max.Y,max.Z) };
		int[] edges = { 0,1, 0,2, 1,3, 2,3, 4,5, 4,6, 5,7, 6,7, 0,4, 1,5, 2,6, 3,7 };
		foreach (int i in edges) lines.SurfaceAddVertex(c[i]);
	}

	private static StandardMaterial3D DebugMaterial(Color color) => new() { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = color, NoDepthTest = true };

	private static bool ContainsName(Godot.Collections.Array<string> values, StringName name)
	{ foreach (string value in values) if (string.Equals(value, name.ToString(), StringComparison.OrdinalIgnoreCase)) return true; return false; }
	private string FormatResolvedClips() { var items = new List<string>(); foreach (var pair in _resolvedClips) items.Add($"{pair.Key}={pair.Value}"); return string.Join(",", items); }
	private static IEnumerable<Node> GetDescendants(Node root)
	{ foreach (Node child in root.GetChildren()) { yield return child; foreach (Node descendant in GetDescendants(child)) yield return descendant; } }
}
