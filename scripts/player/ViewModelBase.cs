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
	private Node3D? _weaponGrip;
	private Node3D? _magazineModel;
	private MuzzleFireEffect? _muzzleFire;
	private MeshInstance3D? _muzzleGizmo;
	private bool _showMuzzleGizmo;
	private readonly Dictionary<string, string> _resolvedClips = new();
	private string _idleClip = string.Empty;
	private string _activeMovementClip = string.Empty;
	private string _activeMovementGeneric = "idle";
	private float _normalizationScale = 1.0f;
	private Camera3D? _viewCamera;
	private Texture2D? _neutralAlbedo;
	// ViewScale is intentionally applied to the imported rig rather than this camera child. This
	// lets us scale about the visible weapon/hand centre instead of the camera origin.
	private Transform3D _rigBaseTransform = Transform3D.Identity;
	private Vector3 _rigBaseScale = Vector3.One;
	private Vector3 _viewScalePivot = Vector3.Zero;
	private float _aimBlend;
	private bool _aiming;

	public enum ViewModelAction { None, Draw, Fire, Reload }
	private ViewModelAction _currentAction = ViewModelAction.None;
	private string _currentActionClip = string.Empty;
	private string _queuedMovementGeneric = "idle";
	private float _fireActionTimer;
	private readonly RandomNumberGenerator _rng = new();

	public struct SpringVector3
	{
		public Vector3 Current;
		public Vector3 Target;
		public Vector3 Velocity;

		/// <summary>
		/// Unconditionally stable analytical damped harmonic oscillator.
		/// Guaranteed to never explode or produce NaN regardless of delta or frequency.
		/// </summary>
		public void Update(float delta, float frequency, float dampingRatio = 1.0f)
		{
			if (delta <= 0.0f) return;
			if (!Current.IsFinite() || !Velocity.IsFinite() || !Target.IsFinite())
			{
				Reset();
				return;
			}

			float omega = Mathf.Max(frequency * Mathf.Tau, 0.001f);
			float dt = Mathf.Min(delta, 0.2f);
			Vector3 x0 = Current - Target;
			Vector3 v0 = Velocity;

			if (dampingRatio >= 1.0f)
			{
				// Critically damped (exact analytical closed-form)
				float decay = Mathf.Exp(-omega * dt);
				Vector3 c2 = v0 + omega * x0;
				Current = Target + (x0 + c2 * dt) * decay;
				Velocity = (v0 - omega * c2 * dt) * decay;
			}
			else
			{
				// Underdamped (exact analytical closed-form with natural oscillation)
				float dampedFreq = omega * Mathf.Sqrt(1.0f - dampingRatio * dampingRatio);
				float decay = Mathf.Exp(-dampingRatio * omega * dt);
				float sinVal = Mathf.Sin(dampedFreq * dt);
				float cosVal = Mathf.Cos(dampedFreq * dt);

				Vector3 c2 = (v0 + x0 * (dampingRatio * omega)) / dampedFreq;
				Current = Target + (x0 * cosVal + c2 * sinVal) * decay;
				Velocity = (v0 * (cosVal - dampingRatio * omega / dampedFreq * sinVal) - x0 * (omega * omega / dampedFreq * sinVal)) * decay;
			}

			if (!Current.IsFinite() || !Velocity.IsFinite())
			{
				Reset();
			}
		}

		public void Reset()
		{
			Current = Vector3.Zero;
			Target = Vector3.Zero;
			Velocity = Vector3.Zero;
		}
	}

	private SpringVector3 _swayRotSpring;
	private SpringVector3 _swayPosSpring;
	private SpringVector3 _recoilKickSpring;
	private Vector2 _smoothLookDelta;

	public bool IsAiming => _aiming;
	public bool IsActionBusy => _currentAction is ViewModelAction.Reload or ViewModelAction.Draw;

	public Skeleton3D? Skeleton => _skeleton;
	public Node3D? MuzzlePoint => _muzzlePoint;
	public IReadOnlyDictionary<string, string> ResolvedClips => _resolvedClips;
	public Node3D? WeaponModel => _weaponModel;

	public void Configure(ViewModelWeaponData data)
	{
		Data = data;
		ClipMap = data.ClipMap;
		PositionOffset = data.ViewPosition;
		RotationOffsetDegrees = data.ViewRotationDegrees;
		ScaleOffset = data.ViewScale;
	}

	/// <summary>Applies safe visual tuning values without recreating the animation rig.</summary>
	public void ApplyData()
	{
		if (Data == null) return;
		if (Data.UseRigCameraTransform)
		{
			AlignRigToDefaultCamera();
			PositionOffset = Data.ViewPosition;
			RotationOffsetDegrees = Data.ViewRotationDegrees;
			ScaleOffset = Data.ViewScale;
		}
		else
		{
			PositionOffset = Data.ViewPosition;
			RotationOffsetDegrees = Data.ViewRotationDegrees;
			ScaleOffset = Data.ViewScale;
		}
		ApplyAimPose();
		Scale = Vector3.One;
		ApplyViewScale();
		ApplyWeaponPlacement();
		ApplyMagazinePlacement();
		ConfigureMuzzleAnchor();
		foreach (Node node in GetDescendants(_rig ?? this))
			if (node is MeshInstance3D mesh) mesh.ExtraCullMargin = Mathf.Max(mesh.ExtraCullMargin, Data.CullMargin);
	}

	private void AlignRigToDefaultCamera()
	{
		if (_rig == null) return;
		Camera3D? rigCamera = null;
		foreach (Node child in GetDescendants(_rig))
		{
			if (child is Camera3D cam)
			{
				rigCamera = cam;
				break;
			}
		}

		if (rigCamera != null)
		{
			rigCamera.ForceUpdateTransform();
			_rig.ForceUpdateTransform();
			Transform3D camInRig = _rig.GlobalTransform.AffineInverse() * rigCamera.GlobalTransform;
			Transform3D rigWanted = camInRig.AffineInverse();
			_rig.Transform = rigWanted;
			_rigBaseTransform = rigWanted;
			_rigBaseScale = rigWanted.Basis.Scale;
			GD.Print($"VIEWMODEL aligned to rig camera: wanted_origin={rigWanted.Origin} wanted_rot={rigWanted.Basis.GetEuler() * (180.0f / Mathf.Pi)}");
		}
		else
		{
			// Check if a CameraBoneName was provided and exists in the skeleton
			if (!string.IsNullOrEmpty(Data?.CameraBoneName) && _skeleton != null)
			{
				int boneIdx = _skeleton.FindBone(Data.CameraBoneName);
				if (boneIdx >= 0)
				{
					_skeleton.ForceUpdateTransform();
					_rig.ForceUpdateTransform();
					Transform3D bonePose = _skeleton.GetBoneGlobalPose(boneIdx);
					Transform3D boneInRig = _rig.GlobalTransform.AffineInverse() * (_skeleton.GlobalTransform * bonePose);
					Transform3D rigWanted = boneInRig.AffineInverse();
					_rig.Transform = rigWanted;
					_rigBaseTransform = rigWanted;
					_rigBaseScale = rigWanted.Basis.Scale;
					GD.Print($"VIEWMODEL aligned to camera bone '{Data.CameraBoneName}': wanted_origin={rigWanted.Origin} wanted_rot={rigWanted.Basis.GetEuler() * (180.0f / Mathf.Pi)}");
					return;
				}
			}

			// Standard authored first-person FPS armature fallback (e.g. Benelli M4 sharing the standard FPS rig setup):
			Transform3D defaultRigWanted = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), new Vector3(0.0f, -34.000217f, 0.0f));
			_rig.Transform = defaultRigWanted;
			_rigBaseTransform = defaultRigWanted;
			_rigBaseScale = Vector3.One;
			GD.Print($"VIEWMODEL aligned to standard FPS rig camera: wanted_origin={defaultRigWanted.Origin} wanted_rot={defaultRigWanted.Basis.GetEuler() * (180.0f / Mathf.Pi)}");
		}
	}

	public void PlayMuzzleFire() => _muzzleFire?.Fire();

	/// <summary>Called by the weapon manager every frame. The weapon itself moves to ADS; FOV is
	/// still controlled by PlayerMovement, so both parts of the transition begin and end together.</summary>
	public void SetAiming(bool aiming) => _aiming = aiming;

	public override void _Process(double delta)
	{
		if (Data == null) return;
		float dt = (float)delta;
		bool canAim = Data.HasScope || Data.AimPositionOffset != Vector3.Zero || Data.WeaponName.Equals("Sniper", StringComparison.OrdinalIgnoreCase);
		bool effectiveAiming = _aiming && canAim;

		float duration = Mathf.Max(Data.AimTransitionSeconds, 0.001f);
		float weight = 1.0f - Mathf.Exp(-dt / duration);
		float prevAimBlend = _aimBlend;
		_aimBlend = Mathf.Lerp(_aimBlend, effectiveAiming ? 1.0f : 0.0f, weight);

		// Transition to/from dedicated aim_idle when aim blend crosses threshold and no priority action is running
		if (_resolvedClips.TryGetValue("aim_idle", out string? aimIdleClip))
		{
			if (_aimBlend >= 0.5f && _currentAction == ViewModelAction.None && _activeMovementClip != aimIdleClip)
			{
				_activeMovementClip = aimIdleClip;
				_animationPlayer?.Play(aimIdleClip, 0.0, 0.0f);
				_animationPlayer?.Seek(0.0, true);
			}
			else if (_aimBlend < 0.5f && _currentAction == ViewModelAction.None && _activeMovementClip == aimIdleClip)
			{
				Play(_queuedMovementGeneric);
			}
		}

		if (_fireActionTimer > 0.0f) _fireActionTimer -= dt;

		ApplyAimPose();
	}

	private void ApplyAimPose()
	{
		if (Data == null) return;
		Vector3 basePos = PositionOffset + Data.AimPositionOffset * _aimBlend;
		Vector3 baseRot = RotationOffsetDegrees + Data.AimRotationOffsetDegrees * _aimBlend;

		Vector3 finalPos = basePos + _swayPosSpring.Current + _recoilKickSpring.Current;
		Vector3 finalRot = baseRot + _swayRotSpring.Current;

		if (finalPos.IsFinite()) Position = finalPos;
		if (finalRot.IsFinite()) RotationDegrees = finalRot;
	}

	public void AddRecoilKick()
	{
		float adsFactor = Mathf.Lerp(1.0f, 0.25f, _aimBlend);

		// Local +Z is toward the player camera (recoil kickback), +Y is upward barrel lift
		_recoilKickSpring.Velocity += new Vector3(
			_rng.RandfRange(-0.0008f, 0.0008f),
			_rng.RandfRange(0.002f, 0.004f),
			0.016f
		) * adsFactor;

		// Rotational recoil kick: pitch up, slight random roll and yaw
		_swayRotSpring.Velocity += new Vector3(
			_rng.RandfRange(0.9f, 1.3f),
			_rng.RandfRange(-0.25f, 0.25f),
			_rng.RandfRange(-0.15f, 0.15f)
		) * adsFactor;
	}

	public void AddLandingImpulse(float strength)
	{
		float adsFactor = Mathf.Lerp(1.0f, 0.10f, _aimBlend);

		// Gun compresses downward subtly on hard landing
		_swayPosSpring.Velocity += new Vector3(0.0f, -strength * 0.05f, 0.0f) * adsFactor;
		_swayRotSpring.Velocity += new Vector3(-strength * 2.0f, 0.0f, 0.0f) * adsFactor;
	}

	public void UpdateProceduralMotion(float delta, Vector2 lookDelta, Vector3 localVelocity, bool isAiming)
	{
		// Cut sway completely during ADS so the sight picture remains rock-steady on target
		float adsFactor = Mathf.Clamp(1.0f - _aimBlend * 1.5f, 0.0f, 1.0f);

		// Smooth mouse look delta with faster tracking to eliminate floaty input lag
		_smoothLookDelta = _smoothLookDelta.Lerp(lookDelta, 1.0f - Mathf.Exp(-delta * 30.0f));

		// Subtle, realistic mouse sway (tight clamps, minimal roll tilt, no wild banking)
		float targetPitch = Mathf.Clamp(-_smoothLookDelta.Y * 0.35f, -0.4f, 0.4f) * adsFactor;
		float targetYaw = Mathf.Clamp(-_smoothLookDelta.X * 0.40f, -0.5f, 0.5f) * adsFactor;
		float targetRoll = Mathf.Clamp(_smoothLookDelta.X * 0.15f, -0.2f, 0.2f) * adsFactor;

		_swayRotSpring.Target = new Vector3(targetPitch, targetYaw, targetRoll);

		// Movement inertia sway: subtle tactile weight during movement without excessive wobble
		float targetPosX = Mathf.Clamp(-localVelocity.X * 0.0003f, -0.003f, 0.003f) * adsFactor;
		float targetPosY = Mathf.Clamp(localVelocity.Y * 0.0002f, -0.002f, 0.002f) * adsFactor;
		float targetPosZ = Mathf.Clamp(localVelocity.Z * 0.0003f, -0.003f, 0.003f) * adsFactor;

		_swayPosSpring.Target = new Vector3(targetPosX, targetPosY, targetPosZ);

		// Snappy, critically damped springs for immediate, crisp response
		_recoilKickSpring.Update(delta, 18.0f, 1.0f);
		_swayRotSpring.Update(delta, 16.0f, 1.0f);
		_swayPosSpring.Update(delta, 14.0f, 1.0f);
	}

	public void CancelReload()
	{
		if (_currentAction == ViewModelAction.Reload)
		{
			_currentAction = ViewModelAction.None;
			_currentActionClip = string.Empty;
			Play(_queuedMovementGeneric);
		}
	}

	public void SetMuzzleGizmoVisible(bool visible)
	{
		_showMuzzleGizmo = visible;
		if (_muzzleGizmo != null) _muzzleGizmo.Visible = visible;
	}

	public override void _Ready()
	{
		_rng.Randomize();
		ApplyAimPose();
		Scale = Vector3.One;
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
				// Several of these imported rigs carry a skinned mesh whose bind-pose AABB sits offset
				// from the animated pose. Instead of an unbounded box (-1000..1000) which breaks frustum
				// culling and shadow maps, we set a tight bounded box covering the first-person viewmodel volume
				// (approx 1.5m wide/high, 2.5m deep around the camera space) to maintain proper frustum culling.
				if (isArm || keepRigWeapon || keepRigMagazine)
				{
					if (Data.UseRigCameraTransform)
						mesh.CustomAabb = new Aabb(new Vector3(-50.0f, -50.0f, -80.0f), new Vector3(100.0f, 100.0f, 160.0f));
					else
						mesh.CustomAabb = new Aabb(new Vector3(-0.75f, -0.75f, -1.25f), new Vector3(1.5f, 1.5f, 2.5f));
				}
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

		if (NormalizeArms && (Data == null || !Data.UseRigCameraTransform)) NormalizeRig();
		ResolveAndConfigureClips();
		_animationPlayer.AnimationFinished += OnAnimationFinished;
		AttachWeaponModel();
		if (_muzzlePoint == null) CreateMuzzleAnchor();
		CaptureViewScalePivot();
		ApplyData();
		ProcessPriority = 100;
		RefreshDebugGeometry();
		GD.Print($"VIEWMODEL weapon='{Data.WeaponName}' rig='{Data.RigScene.ResourcePath}' attach='{Data.AttachBoneName}' clips={FormatResolvedClips()} arm_extent={GetArmExtent():F4}m normalization={_normalizationScale:F6}");
		ReportMeshBounds();
		if (!string.IsNullOrEmpty(_idleClip)) Play("idle");
	}

	private void NormalizeRig()
	{
		if (Data == null || _rig == null || Data.UseRigCameraTransform) return;
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

	/// <summary>Captures a stable scale pivot from the visible hands and weapon. Imported FPS rigs
	/// commonly place their meshes away from their root, so scaling the camera child would otherwise
	/// make a weapon look as though ViewPosition had changed.</summary>
	private void CaptureViewScalePivot()
	{
		if (_rig == null) return;
		_rigBaseTransform = _rig.Transform;
		_rigBaseScale = _rig.Scale;
		Transform3D rigInverse = _rig.GlobalTransform.AffineInverse();
		Aabb? bounds = null;
		foreach (Node node in GetDescendants(_rig))
			if (node is MeshInstance3D mesh && mesh.Visible && mesh.Mesh != null)
			{
				// Use the source mesh AABB, not CustomAabb: visible skinned meshes deliberately
				// receive a huge custom box to prevent culling, which is not a useful scale pivot.
				Aabb local = TransformAabb(mesh.Mesh.GetAabb(), rigInverse * mesh.GlobalTransform);
				bounds = bounds.HasValue ? bounds.Value.Merge(local) : local;
			}
		_viewScalePivot = bounds?.GetCenter() ?? Vector3.Zero;
	}

	private void ApplyViewScale()
	{
		if (_rig == null) return;
		if (Data?.UseRigCameraTransform == true)
		{
			// When rig is aligned to camera, scaling about camera keeps sight alignment perfectly intact:
			// In camera space, the camera is at Vector3.Zero. Scaling about the camera preserves the sight line.
			_rig.Transform = _rigBaseTransform;
			_rig.Scale = _rigBaseScale * ScaleOffset;
			_rig.Position = _rigBaseTransform.Origin * ScaleOffset;
			return;
		}
		_rig.Transform = _rigBaseTransform;
		Vector3 basePivot = _rigBaseTransform.Basis * _viewScalePivot;
		_rig.Scale = _rigBaseScale * ScaleOffset;
		Vector3 scaledPivot = _rig.Transform.Basis * _viewScalePivot;
		_rig.Position = _rigBaseTransform.Origin + basePivot - scaledPivot;
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
		if (_resolvedClips.TryGetValue("idle", out string? idle))
		{
			_idleClip = idle;
		}
		else
		{
			GD.PushWarning($"{Data.WeaponName}: no idle clip configured.");
		}

		string[] loopClipKeys = { "idle", "walk", "run" };
		foreach (string key in loopClipKeys)
		{
			if (_resolvedClips.TryGetValue(key, out string? clipName) && _animationPlayer.HasAnimation(clipName))
			{
				Animation? anim = _animationPlayer.GetAnimation(clipName);
				if (anim != null && anim.LoopMode == Animation.LoopModeEnum.None)
				{
					anim.LoopMode = Animation.LoopModeEnum.Linear;
				}
			}
		}
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
		_weaponGrip = grip;
		Node3D model = Data.WeaponModelScene.Instantiate<Node3D>();
		model.Name = "WeaponModel";
		grip.AddChild(model);
		_weaponModel = model;

		// Where the rig's own gun sits in this viewmodel. Every rig was designed around the hands, so the new
		// model is centred on that same spot: the hands then hold it the way they hold the rig's gun. The
		// AttachPosition/AttachRotation/AttachScale values are fine adjustments on top of that alignment.
		model.Transform = Transform3D.Identity;
		ApplyWeaponPlacement();
		_magazineModel = string.IsNullOrEmpty(Data.MagazineNodeName) ? null : model.FindChild(Data.MagazineNodeName, true, false) as Node3D;
		ApplyMagazinePlacement();
		CreateMuzzleAnchor();

		ReportWeaponLocalBounds(model);
		GD.Print($"VIEWMODEL ATTACH weapon='{Data.WeaponName}' model='{Data.WeaponModelScene.ResourcePath}' bone='{Data.AttachBoneName}'");
	}

	private void ApplyWeaponPlacement()
	{
		if (Data == null || _skeleton == null || _weaponModel == null || _weaponGrip == null) return;
		Basis rotation = Basis.FromEuler(Data.AttachRotationDegrees * (Mathf.Pi / 180.0f), EulerOrder.Yxz);
		Basis scaled = rotation.Scaled(Data.AttachScale);
		Vector3 modelCenter = LocalAabb(_weaponModel).GetCenter();
		Vector3 rigCentre = RigGunCentreInRoot();
		Vector3 anchor = (rigCentre != Vector3.Inf ? rigCentre : Vector3.Zero) + Data.AttachPosition;
		Transform3D wantedInRoot = new Transform3D(scaled, anchor - scaled * modelCenter);
		int boneIndex = _skeleton.FindBone(Data.AttachBoneName);
		if (boneIndex < 0) return;
		Transform3D boneGlobal = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(boneIndex);
		_weaponGrip.Transform = boneGlobal.AffineInverse() * GlobalTransform * wantedInRoot;
	}

	private void CreateMuzzleAnchor()
	{
		if (Data == null) return;
		Node3D parent = _weaponModel ?? (Node3D)this;
		if (!string.IsNullOrEmpty(Data.MuzzleNodeName))
		{
			Node3D? socket = parent.FindChild(Data.MuzzleNodeName, true, false) as Node3D;
			if (socket != null) parent = socket;
		}
		_muzzlePoint = new Node3D { Name = "MuzzleAnchor" };
		parent.AddChild(_muzzlePoint);
		_muzzleFire = new MuzzleFireEffect { Name = "MuzzleFireEffect" };
		_muzzlePoint.AddChild(_muzzleFire);
		CreateMuzzleGizmo();
		ConfigureMuzzleAnchor();
	}

	private void ApplyMagazinePlacement()
	{
		if (Data == null || _magazineModel == null) return;
		_magazineModel.Position = Data.MagazinePosition;
		_magazineModel.RotationDegrees = Data.MagazineRotationDegrees;
		_magazineModel.Scale = Data.MagazineScale;
	}

	private void ConfigureMuzzleAnchor()
	{
		if (Data == null || _muzzlePoint == null) return;
		bool isCustomSocket = !string.IsNullOrEmpty(Data.MuzzleNodeName) && _muzzlePoint.GetParent()?.Name == Data.MuzzleNodeName;
		_muzzlePoint.Position = isCustomSocket ? Vector3.Zero : Data.MuzzlePointOffset;
		_muzzlePoint.RotationDegrees = Data.MuzzleDirectionOffsetDegrees;
		_muzzleFire?.Configure(Data, 1);
		if (_muzzleGizmo != null) _muzzleGizmo.Visible = _showMuzzleGizmo;
	}

	private void CreateMuzzleGizmo()
	{
		if (_muzzlePoint == null) return;
		var lines = new ImmediateMesh();
		lines.SurfaceBegin(Mesh.PrimitiveType.Lines);
		lines.SurfaceSetColor(Colors.Red); lines.SurfaceAddVertex(Vector3.Zero); lines.SurfaceAddVertex(Vector3.Right * 0.12f);
		lines.SurfaceSetColor(Colors.Green); lines.SurfaceAddVertex(Vector3.Zero); lines.SurfaceAddVertex(Vector3.Up * 0.12f);
		lines.SurfaceSetColor(Colors.Blue); lines.SurfaceAddVertex(Vector3.Zero); lines.SurfaceAddVertex(Vector3.Back * 0.24f);
		lines.SurfaceEnd();
		_muzzleGizmo = new MeshInstance3D { Name = "MuzzleAnchorGizmo", Mesh = lines, MaterialOverride = DebugMaterial(Colors.White), Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 2.0f };
		_muzzlePoint.AddChild(_muzzleGizmo);
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
			"M4" when meshName.ToString().StartsWith("BARREL", StringComparison.OrdinalIgnoreCase) => "res://art/animations/player/combat/M4/textures/m16_M16A3_BaseColor.png",
			"Sniper" when meshName.ToString().StartsWith("Rifle", StringComparison.OrdinalIgnoreCase) => "res://art/animations/player/combat/Sniper/textures/Rifle_albedo.jpg",
			"Kriss Vector" when meshName.ToString() is "main" or "mag" => "res://art/animations/player/combat/Vector/textures/Vector_D.png",
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
		float multiplier = Data?.SpeedMultiplier(name) ?? 1.0f;

		if (name is "idle" or "walk" or "run")
		{
			_queuedMovementGeneric = name;
			// Never let walking/idling cancel an in-progress reload or draw
			if (_currentAction is ViewModelAction.Reload or ViewModelAction.Draw) return true;
			// If recently fired, let the recoil peak finish before switching locomotion
			if (_currentAction == ViewModelAction.Fire && _fireActionTimer > 0.0f) return true;

			if (_aimBlend >= 0.5f && _resolvedClips.TryGetValue("aim_idle", out string? aimIdleClip))
			{
				_activeMovementClip = aimIdleClip;
				_activeMovementGeneric = name;
				_currentAction = ViewModelAction.None;
				_currentActionClip = string.Empty;
				_animationPlayer.Play(aimIdleClip, 0.0, 0.0f);
				_animationPlayer.Seek(0.0, true);
				return true;
			}

			_activeMovementClip = clip;
			_activeMovementGeneric = name;
			_currentAction = ViewModelAction.None;
			_currentActionClip = string.Empty;

			_animationPlayer.Play(clip, 0.12, Mathf.Max(0.01f, speed * multiplier));
			if (name == "idle" && Data != null && Data.IdlePoseTime >= 0 && clip.EndsWith("FN_Walk", StringComparison.Ordinal))
			{
				_animationPlayer.Seek(Data.IdlePoseTime, true);
				_animationPlayer.Pause();
			}
			return true;
		}

		if (name is "reload" or "reload_full")
		{
			_currentAction = ViewModelAction.Reload;
			_currentActionClip = clip;
			_animationPlayer.Play(clip, 0.08, Mathf.Max(0.01f, speed * multiplier));
			return true;
		}

		if (name == "draw")
		{
			_currentAction = ViewModelAction.Draw;
			_currentActionClip = clip;
			_animationPlayer.Play(clip, 0.05, Mathf.Max(0.01f, speed * multiplier));
			return true;
		}

		if (name == "fire")
		{
			string fireClip = clip;
			if (_aimBlend > 0.5f && _resolvedClips.TryGetValue("fire_aim", out string? scopedClip))
			{
				fireClip = scopedClip;
			}
			_currentAction = ViewModelAction.Fire;
			_currentActionClip = fireClip;
			_fireActionTimer = 0.12f;
			if (_animationPlayer.IsPlaying() && _animationPlayer.CurrentAnimation == fireClip && _animationPlayer.SpeedScale > 0.0f)
			{
				_animationPlayer.Seek(0.0, true);
			}
			else
			{
				_animationPlayer.Play(fireClip, 0.02, Mathf.Max(0.01f, speed * multiplier));
				_animationPlayer.Seek(0.0, true);
			}
			AddRecoilKick();
			return true;
		}

		_animationPlayer.Play(clip, 0.08, Mathf.Max(0.01f, speed * multiplier));
		return true;
	}

	private void OnAnimationFinished(StringName animationName)
	{
		string animStr = animationName.ToString();
		if (_currentAction != ViewModelAction.None && animStr == _currentActionClip)
		{
			_currentAction = ViewModelAction.None;
			_currentActionClip = string.Empty;
			if (_aimBlend >= 0.5f && _resolvedClips.TryGetValue("aim_idle", out string? aimIdleClip))
			{
				_activeMovementClip = aimIdleClip;
				_animationPlayer?.Play(aimIdleClip, 0.0, 0.0f);
				_animationPlayer?.Seek(0.0, true);
			}
			else
			{
				Play(_queuedMovementGeneric);
			}
			return;
		}

		if (_animationPlayer == null || string.IsNullOrEmpty(_activeMovementClip) || animStr == _activeMovementClip) return;
		Play(_queuedMovementGeneric);
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
	{
		var stack = new Stack<Node>();
		foreach (Node child in root.GetChildren())
			stack.Push(child);

		while (stack.Count > 0)
		{
			Node current = stack.Pop();
			yield return current;
			foreach (Node child in current.GetChildren())
				stack.Push(child);
		}
	}
}
