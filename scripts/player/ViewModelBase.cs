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
	private Node3D? _weaponModel;
	private readonly Dictionary<string, string> _resolvedClips = new();
	private string _idleClip = string.Empty;
	private string _activeMovementClip = string.Empty;
	private string _activeMovementGeneric = "idle";
	private float _normalizationScale = 1.0f;
	private Camera3D? _viewCamera;
	private Texture2D? _neutralAlbedo;

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
		_neutralAlbedo = CreateNeutralAlbedoTexture();
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
		// Keep the FBX-authored scale tracks. The bundled rifle/pistol/shotgun meshes are
		// animated with the rig; stripping all Skeleton3D scale tracks also removed valid
		// twist-bone tracks (and the included weapon's own keyed scale).
		foreach (Node child in GetDescendants(_rig))
		{
			if (child is Camera3D camera) { camera.Current = false; camera.Visible = false; }
			if (child is Light3D light) light.Visible = false;
			if (child is MeshInstance3D mesh)
			{
				bool isArm = ContainsName(Data.ArmMeshNames, mesh.Name);
				mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
				mesh.ExtraCullMargin = Mathf.Max(mesh.ExtraCullMargin, Data.CullMargin);
				if (isArm && Data.MeshMaterials.TryGetValue(mesh.Name.ToString(), out Material? material))
					mesh.MaterialOverride = material;
				// Keep the configured arm/sleeve/glove meshes and the FBX's own hand-bound weapon.
				bool keepRigWeapon = !Data.HideOriginalWeapon && ContainsName(Data.OriginalWeaponMeshNames, mesh.Name);
				bool keepRigMagazine = !Data.HideOriginalMagazine && ContainsName(Data.OriginalMagazineMeshNames, mesh.Name);
				if (Data.ArmMeshNames.Count > 0 && !isArm && !keepRigWeapon && !keepRigMagazine) mesh.Visible = false;
				// Several of these imported rigs (the Benelli arms, now also the P90's gun parts) carry a
				// skinned mesh whose bind-pose AABB, baked in at import, sits far from the animated pose -
				// large, and offset by metres. Godot culls a skinned mesh by that stale box, not by where the
				// skin deformation actually puts it, so the gun can vanish from view even though it is being
				// drawn in the right place. A generous local box on every mesh we keep visible avoids that.
				if (isArm || keepRigWeapon || keepRigMagazine)
					mesh.CustomAabb = new Aabb(new Vector3(-1000, -1000, -1000), new Vector3(2000, 2000, 2000));
				if (isArm)
				{
					mesh.Visible = true;
					mesh.Layers = 1;
				}
				// Every surface of a mesh we keep (arm, or the rig's own weapon/magazine) is forced double-sided.
				// Some imported rigs ship single-sided materials that back-face cull at odd viewing angles in
				// first person; this costs nothing noticeable on a handful of small meshes and avoids that.
				if ((isArm || keepRigWeapon || keepRigMagazine) && mesh.Mesh != null)
				{
					for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
					{
						Material? active = mesh.MaterialOverride ?? mesh.GetSurfaceOverrideMaterial(surface) ?? mesh.Mesh.SurfaceGetMaterial(surface);
						if (active is not BaseMaterial3D source) continue; // leave shader materials alone
						BaseMaterial3D twoSided = (BaseMaterial3D)source.Duplicate(true);
						twoSided.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
						mesh.SetSurfaceOverrideMaterial(surface, twoSided);
					}
				}
				if (Data.HideOriginalWeapon && ContainsName(Data.OriginalWeaponMeshNames, mesh.Name)) mesh.Visible = false;
				if (Data.HideOriginalMagazine && ContainsName(Data.OriginalMagazineMeshNames, mesh.Name)) mesh.Visible = false;
				if (isArm)
				{
					// Imported Benelli meshes carry a non-default render layer in the FBX scene.
					// Viewmodels use the player's camera layer so camera culling cannot drop them.
					mesh.Layers = 1;
				}
				// Any mesh we're actually keeping visible - arm, or the rig's own bundled weapon/magazine -
				// needs its embedded texture checked. This used to run for arm meshes only, which is why the
				// AK-47's "Ak"/"Mag_2" meshes (kept via keepRigWeapon/keepRigMagazine, not isArm) never got
				// ResolveEmbeddedAlbedo's fix and rendered with no albedo at all.
				if (isArm || keepRigWeapon || keepRigMagazine)
					EnsureAlbedoTexture(mesh);
			}
		}

		if (NormalizeArms) NormalizeRig();
		ResolveAndConfigureClips();
		_animationPlayer.AnimationFinished += OnAnimationFinished;
		AttachWeaponModel();
		ProcessPriority = 100;
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
			if (referenceRoot is Node3D referenceNode) referenceNode.Visible = false;
			referenceRoot.QueueFree();
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
		}
		if (!string.IsNullOrEmpty(Data.AttachBoneName) && _skeleton?.FindBone(Data.AttachBoneName) < 0)
			GD.PushWarning($"{Data.WeaponName}: attach bone '{Data.AttachBoneName}' is missing in rig '{Data.RigScene?.ResourcePath}'.");
		if (!_resolvedClips.ContainsKey("idle")) GD.PushWarning($"{Data.WeaponName}: no idle clip configured.");
	}

	private void AttachWeaponModel()
	{
		if (Data == null || _skeleton == null || Data.WeaponModelScene == null || string.IsNullOrEmpty(Data.AttachBoneName)) return;
		if (_skeleton.FindBone(Data.AttachBoneName) < 0) return;

		// The weapon is glued to a bone of the rig, so it moves with the hand/body exactly like the
		// rig's own gun does. (It used to sit on the viewmodel root at a fixed offset, which is why every
		// model other than the rig's own gun floated away from the hands.)
		BoneAttachment3D? attachment = MakeAttachment("WeaponBoneAttachment", Data.AttachBoneName);
		if (attachment == null) return;

		// AttachPosition/Rotation/Scale are in that bone's frame, in metres (the rig skeletons are at
		// scale 1, so no unit correction is needed here).
		// The skeleton is authored in centimetres AND in a different axis convention, and everything under a
		// bone inherits that. So the grip is solved from the full chain, in this viewmodel's own frame, and then
		// moves with the bone, so it still follows the animation.
		var grip = new Node3D { Name = "WeaponGrip" };
		attachment.AddChild(grip);
		Node3D model = Data.WeaponModelScene.Instantiate<Node3D>();
		model.Name = "WeaponModel";
		grip.AddChild(model);
		_weaponModel = model;

		// Where the rig's own gun sits in this viewmodel. Every rig was designed around the hands, so the new
		// model is centred on that same spot: the hands then hold it the way they hold the rig's gun. The
		// AttachPosition/AttachRotation/AttachScale values are fine adjustments on top of that alignment.
		Basis rotation = Basis.FromEuler(Data.AttachRotationDegrees * (Mathf.Pi / 180.0f), EulerOrder.Yxz);
		Basis scaled = rotation.Scaled(Data.AttachScale);
		Vector3 modelCenter = LocalAabb(model).GetCenter();
		Vector3 rigCentre = RigGunCentreInRoot();
		bool haveRigGun = rigCentre != Vector3.Inf;
		Vector3 anchor = (haveRigGun ? rigCentre : Vector3.Zero) + Data.AttachPosition;
		Transform3D wantedInRoot = new Transform3D(scaled, anchor - scaled * modelCenter);
		int boneIndex = _skeleton.FindBone(Data.AttachBoneName);
		Transform3D boneGlobal = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(boneIndex);
		grip.Transform = boneGlobal.AffineInverse() * GlobalTransform * wantedInRoot;
		model.Transform = Transform3D.Identity;

		ReportWeaponLocalBounds(model);
		GD.Print($"VIEWMODEL ATTACH weapon='{Data.WeaponName}' model='{Data.WeaponModelScene.ResourcePath}' bone='{Data.AttachBoneName}'");
	}

	private static Vector3 InverseScale(Vector3 scale) => new(
		Mathf.IsZeroApprox(scale.X) ? 1f : 1f / scale.X,
		Mathf.IsZeroApprox(scale.Y) ? 1f : 1f / scale.Y,
		Mathf.IsZeroApprox(scale.Z) ? 1f : 1f / scale.Z);

	private static Aabb LocalAabb(Node3D root)
	{
		Aabb? bounds = null;
		Transform3D rootInverse = root.GlobalTransform.AffineInverse();
		foreach (Node node in GetDescendants(root))
			if (node is MeshInstance3D mesh)
			{
				Aabb local = TransformAabb(mesh.GetAabb(), rootInverse * mesh.GlobalTransform);
				bounds = bounds.HasValue ? bounds.Value.Merge(local) : local;
			}
		return bounds ?? new Aabb(Vector3.Zero, Vector3.Zero);
	}

	/// <summary>Centre of the rig's own gun meshes, in this viewmodel's root space; Vector3.Inf if the rig
	/// has none. A skinned mesh is drawn at skeleton-space coordinates, so its bind AABB is mapped through
	/// the skeleton's transform rather than the mesh node's.</summary>
	private Vector3 RigGunCentreInRoot()
	{
		if (_rig == null || _skeleton == null || Data == null || Data.OriginalWeaponMeshNames.Count == 0) return Vector3.Inf;
		Aabb? bounds = null;
		foreach (Node node in GetDescendants(_rig))
			if (node is MeshInstance3D mesh && ContainsName(Data.OriginalWeaponMeshNames, mesh.Name))
			{
				Aabb a = mesh.GetAabb();
				bounds = bounds.HasValue ? bounds.Value.Merge(a) : a;
			}
		if (!bounds.HasValue) return Vector3.Inf;
		Transform3D rootInverse = GlobalTransform.AffineInverse();
		Vector3 viaSkeleton = rootInverse * _skeleton.GlobalTransform * bounds.Value.GetCenter();
		Vector3 viaMeshNode = Vector3.Zero;
		int n = 0;
		foreach (Node node in GetDescendants(_rig))
			if (node is MeshInstance3D m && ContainsName(Data.OriginalWeaponMeshNames, m.Name))
			{
				viaMeshNode += rootInverse * m.GlobalTransform * m.GetAabb().GetCenter();
				n++;
			}
		if (n > 0) viaMeshNode /= n;
		return viaMeshNode;
	}

	private void EnsureAlbedoTexture(MeshInstance3D mesh)
	{
		if (_neutralAlbedo == null || mesh.Mesh == null) return;
		if (mesh.MaterialOverride is BaseMaterial3D wholeMaterial && wholeMaterial.AlbedoTexture == null)
		{
			BaseMaterial3D copy = (BaseMaterial3D)wholeMaterial.Duplicate(true);
			copy.AlbedoTexture = _neutralAlbedo;
			mesh.MaterialOverride = copy;
		}
		for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
		{
			Material? active = mesh.MaterialOverride ?? mesh.GetSurfaceOverrideMaterial(surface) ?? mesh.Mesh.SurfaceGetMaterial(surface);
			if (active is BaseMaterial3D baseMaterial && baseMaterial.AlbedoTexture != null) continue;
			BaseMaterial3D copy = active is BaseMaterial3D source
				? (BaseMaterial3D)source.Duplicate(true)
				: new StandardMaterial3D();
			copy.AlbedoTexture = ResolveEmbeddedAlbedo(mesh.Name) ?? _neutralAlbedo;
			if (mesh.MaterialOverride == null) mesh.SetSurfaceOverrideMaterial(surface, copy);
		}
	}

	private Texture2D? ResolveEmbeddedAlbedo(StringName meshName)
	{
		if (Data == null) return null;
		string path = Data.WeaponName switch
		{
			"AK-47" when meshName.ToString() is "Ak" or "Mag_2" => "res://art/animations/player/combat/AK/textures/ak74m_AlbedoTransparency.png",
			"Glock 19" or "Desert Eagle" or "FN 502" when meshName.ToString() == "502_1_low_001" => "res://art/animations/player/combat/fn 502/textures/material_diffuse.png",
			"Shotgun" when meshName.ToString().StartsWith("12g_", StringComparison.Ordinal) => "res://art/animations/player/combat/benelli m4/textures/12gauge_BaseColor_red.png",
			"Shotgun" when meshName.ToString().StartsWith("matchsaverz", StringComparison.OrdinalIgnoreCase) => "res://art/animations/player/combat/benelli m4/textures/matchsaverz_BaseColor_black_brand_friendly.png",
			"Shotgun" when meshName.ToString().StartsWith("benelli_m4_", StringComparison.OrdinalIgnoreCase) => "res://art/animations/player/combat/benelli m4/textures/TTI_Benelli_M4_BaseColor_brand_friendly.png",
			_ => string.Empty
		};
		return string.IsNullOrEmpty(path) ? null : GD.Load<Texture2D>(path);
	}

	private static Texture2D CreateNeutralAlbedoTexture()
	{
		Image image = Image.CreateEmpty(2, 2, false, Image.Format.Rgba8);
		image.Fill(Colors.White);
		return ImageTexture.CreateFromImage(image);
	}

	private void ReportWeaponLocalBounds(Node3D model)
	{
		Aabb? bounds = null;
		Transform3D rootInverse = model.GlobalTransform.AffineInverse();
		foreach (Node node in GetDescendants(model))
			if (node is MeshInstance3D mesh)
			{
				Aabb local = TransformAabb(mesh.GetAabb(), rootInverse * mesh.GlobalTransform);
				bounds = bounds.HasValue ? bounds.Value.Merge(local) : local;
			}
		if (bounds.HasValue)
		{
			Vector3 size = bounds.Value.Size;
			Vector3 axis = size.X >= size.Y && size.X >= size.Z ? Vector3.Right : size.Y >= size.Z ? Vector3.Up : Vector3.Back;
			GD.Print($"VIEWMODEL MODEL_LOCAL weapon='{Data?.WeaponName}' size={size} longest_axis={axis}");
		}
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