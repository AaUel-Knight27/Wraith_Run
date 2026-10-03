using System;
using Godot;

/// <summary>
/// A small self-contained 3D viewport for the menus: shows a static operator model (optionally
/// holding a weapon) or a single weapon model turning slowly. It draws onto
/// a transparent background so the red-smoke menu backdrop shows through around the figure.
///
/// The operator uses the static Manny mesh + skeleton with animation import disabled, painted
/// and tinted by CharacterAppearance exactly like the in-match body. The weapon is held by the
/// same WeaponAttachment the match uses, so the grip in the lobby is the grip in the game.
/// </summary>
public partial class OperatorStage : SubViewportContainer
{
	private const string CharacterScenePath = "res://art/characters/manny.glb";
	private const float OperatorBaseYaw = 16.0f;
	private const float WeaponFitLength = 1.5f;

	private SubViewport _viewport = null!;
	private Node3D _pivot = null!;
	private Camera3D _camera = null!;
	private MeshInstance3D _pool = null!;

	private Node3D? _model;
	private ShaderMaterial? _tint;
	private WeaponAttachment? _attachment;
	private Action? _pending;

	private bool _built;
	private bool _weaponMode;
	private float _yaw;
	private float _targetYaw;
	private float _time;
	private Vector2I _lastSize = Vector2I.Zero;
	private Vector3 _lookAt = new(0.0f, 0.92f, 0.0f);
	private float _frameHeight = 2.1f;
	private float _frameWidth = 1.5f;

	// Hands-on controls: drag to spin, wheel / pinch to zoom, double-click or double-tap to reset.
	private const float MinZoom = 0.6f;
	private const float MaxZoom = 2.4f;
	private bool _dragging;
	private bool _magnifySeen;
	private float _zoom = 1.0f, _zoomTarget = 1.0f;
	private float _pan, _panTarget;
	private float _pitch, _pitchTarget;
	private float _velocity;
	private float _idle = 10.0f;
	private float _swayBlend = 1.0f;
	private float _baseDistance = 4.0f;
	private float _lastPinch;
	private readonly System.Collections.Generic.Dictionary<int, Vector2> _touches = new();

	public OperatorStage()
	{
		Stretch = true;
		MouseFilter = MouseFilterEnum.Stop;
		FocusMode = FocusModeEnum.None;
		MouseDefaultCursorShape = CursorShape.Move;
	}

	public override void _Ready()
	{
		Build();
		_built = true;
		Reframe();
		if (_pending != null)
		{
			Action run = _pending;
			_pending = null;
			run();
		}
	}

	// -------------------------------------------------------------------------------------------
	// Input
	// -------------------------------------------------------------------------------------------

	public override void _GuiInput(InputEvent @event)
	{
		switch (@event)
		{
			case InputEventMouseButton button:
				if (button.ButtonIndex == MouseButton.Left)
				{
					_dragging = button.Pressed;
					if (button.Pressed)
					{
						_velocity = 0.0f;
						_idle = 0.0f;
						if (button.DoubleClick) ResetView();
					}
					AcceptEvent();
				}
				else if (button.Pressed && button.ButtonIndex == MouseButton.WheelUp) { ZoomBy(1.12f); AcceptEvent(); }
				else if (button.Pressed && button.ButtonIndex == MouseButton.WheelDown) { ZoomBy(1.0f / 1.12f); AcceptEvent(); }
				break;

			case InputEventMouseMotion motion:
				// One finger arrives as an emulated mouse drag; with two fingers down it is a pinch.
				if (_dragging && _touches.Count < 2) DragBy(motion.Relative);
				break;

			case InputEventMagnifyGesture magnify:
				_magnifySeen = true;
				ZoomBy(magnify.Factor);
				AcceptEvent();
				break;

			case InputEventScreenTouch touch:
				if (touch.Pressed) _touches[touch.Index] = touch.Position;
				else _touches.Remove(touch.Index);
				_lastPinch = PinchDistance();
				break;

			case InputEventScreenDrag drag:
				_touches[drag.Index] = drag.Position;
				if (_touches.Count == 2 && !_magnifySeen)
				{
					float now = PinchDistance();
					if (_lastPinch > 1.0f && now > 1.0f) ZoomBy(now / _lastPinch);
					_lastPinch = now;
				}
				break;
		}
	}

	private float PinchDistance()
	{
		if (_touches.Count < 2) return 0.0f;
		Vector2 a = Vector2.Zero, b = Vector2.Zero;
		int i = 0;
		foreach (Vector2 p in _touches.Values)
		{
			if (i == 0) a = p; else if (i == 1) b = p;
			i++;
		}
		return a.DistanceTo(b);
	}

	private void DragBy(Vector2 relative)
	{
		_idle = 0.0f;
		float yaw = relative.X * 0.55f;
		_targetYaw += yaw;
		_velocity = yaw;
		if (_weaponMode)
			_pitchTarget = Mathf.Clamp(_pitchTarget + relative.Y * 0.35f, -50.0f, 60.0f);
		else
			_panTarget += relative.Y * 0.004f; // clamped per frame: only possible once zoomed in
	}

	private void ZoomBy(float factor)
	{
		_idle = 0.0f;
		_zoomTarget = Mathf.Clamp(_zoomTarget * factor, MinZoom, MaxZoom);
	}

	/// <summary>Back to the default framing (double-click / double-tap).</summary>
	public void ResetView()
	{
		_zoomTarget = 1.0f;
		_panTarget = 0.0f;
		_pitchTarget = 0.0f;
		_targetYaw = 0.0f;
		_velocity = 0.0f;
	}

	// -------------------------------------------------------------------------------------------
	// Public API
	// -------------------------------------------------------------------------------------------

	/// <summary>Shows an operator (null = standard issue) in their colours, holding weapon.</summary>
	public void ShowOperator(CharacterData? data, WeaponData? weapon)
	{
		if (!_built) { _pending = () => ShowOperator(data, weapon); return; }

		OperatorInfo.ResolveColors(data, out Color jacket, out Color pants, out Color accent);

		// Same body for every operator, so switching only needs a recolour, not a reload.
		if (_model != null && !_weaponMode && _tint != null)
		{
			Recolor(jacket, pants, accent);
			SetHeldWeapon(weapon);
			return;
		}

		ClearModel();
		_weaponMode = false;
		_pool.Visible = true;

		var root = ResourceLoader.Load<PackedScene>(CharacterScenePath)?.Instantiate<Node3D>();
		if (root == null)
		{
			GD.PushWarning($"OperatorStage: could not load {CharacterScenePath}.");
			return;
		}
		_model = root;
		_pivot.AddChild(root);

		_tint = CharacterAppearance.Apply(root, jacket, pants, accent);
		SetHeldWeapon(weapon);

		_lookAt = new Vector3(0.0f, 0.92f, 0.0f);
		_frameHeight = 2.1f;
		_frameWidth = 1.5f;
		_targetYaw = _yaw = 0.0f;
		Reframe();
	}

	/// <summary>Changes the colours of the operator currently on stage, live.</summary>
	public void Recolor(Color jacket, Color pants, Color accent)
	{
		if (_tint == null) return;
		_tint.SetShaderParameter("jacket_color", jacket);
		_tint.SetShaderParameter("pants_color", pants);
		_tint.SetShaderParameter("accent_color", accent);
	}

	/// <summary>Swaps the weapon in the operator's hands (null = empty hands).</summary>
	public void SetHeldWeapon(WeaponData? weapon)
	{
		if (_model == null || _weaponMode) return;

		Node? old = _model.FindChild("WeaponBoneAttachment", true, false);
		if (old != null)
		{
			old.GetParent()?.RemoveChild(old);
			old.QueueFree();
		}
		if (weapon == null || string.IsNullOrEmpty(weapon.ModelScenePath)) return;

		if (_attachment == null || !IsInstanceValid(_attachment))
		{
			_attachment = new WeaponAttachment { Name = "PreviewWeaponAttachment", SkipAudio = true };
			_model.AddChild(_attachment);
		}
		_attachment.Equip(weapon);
	}

	/// <summary>Shows one weapon model on its own, scaled to fit and turning slowly.</summary>
	public void ShowWeapon(WeaponData weapon)
	{
		if (!_built) { _pending = () => ShowWeapon(weapon); return; }

		ClearModel();
		_weaponMode = true;
		_pool.Visible = false;

		if (string.IsNullOrEmpty(weapon.ModelScenePath))
		{
			GD.PushWarning($"OperatorStage: {weapon.WeaponName} has no model assigned.");
			return;
		}
		var model = ResourceLoader.Load<PackedScene>(weapon.ModelScenePath)?.Instantiate<Node3D>();
		if (model == null)
		{
			GD.PushWarning($"OperatorStage: could not load {weapon.ModelScenePath}.");
			return;
		}

		// The source models disagree wildly on size and on which axis the barrel lies along, so
		// measure the model, turn its longest side to run left-right and scale that to a fixed
		// length. Centred on the origin so the slow turn pivots around the middle of the gun.
		var holder = new Node3D { Name = "WeaponHolder" };
		holder.AddChild(model);
		var bounds = new Aabb();
		bool has = false;
		AccumulateBounds(model, Transform3D.Identity, ref bounds, ref has);

		Basis orientation = Basis.Identity;
		float scale = 1.0f;
		if (has)
		{
			Vector3 size = bounds.Size;
			float longest = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
			if (longest > 0.0001f)
			{
				scale = WeaponFitLength / longest;
				if (size.X >= size.Y && size.X >= size.Z) orientation = Basis.Identity;
				else if (size.Z >= size.Y) orientation = Basis.FromEuler(new Vector3(0.0f, Mathf.Pi * 0.5f, 0.0f));
				else orientation = Basis.FromEuler(new Vector3(0.0f, 0.0f, -Mathf.Pi * 0.5f));
			}
			model.Position = -bounds.GetCenter();
		}
		holder.Transform = new Transform3D(orientation.Scaled(Vector3.One * scale), Vector3.Zero);

		_model = holder;
		_pivot.AddChild(holder);

		_lookAt = Vector3.Zero;
		_frameWidth = WeaponFitLength * 1.35f;
		_frameHeight = 0.95f;
		_targetYaw = _yaw = 0.0f;
		Reframe();
	}

	/// <summary>Turns the figure by a number of degrees (the menu's rotate buttons).</summary>
	public void Nudge(float degrees) => _targetYaw += degrees;

	// -------------------------------------------------------------------------------------------
	// Scene construction
	// -------------------------------------------------------------------------------------------

	private void Build()
	{
		_viewport = new SubViewport
		{
			TransparentBg = true,
			OwnWorld3D = true,
			Msaa3D = Viewport.Msaa.Msaa2X,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			Size = new Vector2I(256, 256),
		};
		AddChild(_viewport);

		var environment = new Godot.Environment
		{
			AmbientLightSource = Godot.Environment.AmbientSource.Color,
			AmbientLightColor = new Color(0.62f, 0.5f, 0.5f),
			AmbientLightEnergy = 0.6f,
		};
		_camera = new Camera3D { Fov = 30.0f, Near = 0.05f, Far = 40.0f, Environment = environment };
		_viewport.AddChild(_camera);
		_camera.MakeCurrent();

		// Warm key light from the camera side, hard red rim from behind - the menu's palette.
		var key = new DirectionalLight3D
		{
			LightColor = new Color(1.0f, 0.94f, 0.88f),
			LightEnergy = 1.15f,
			RotationDegrees = new Vector3(-32.0f, -28.0f, 0.0f),
		};
		_viewport.AddChild(key);
		var rim = new DirectionalLight3D
		{
			LightColor = new Color(1.0f, 0.16f, 0.1f),
			LightEnergy = 1.7f,
			RotationDegrees = new Vector3(-18.0f, 152.0f, 0.0f),
		};
		_viewport.AddChild(rim);

		// A dull red pool of light on the ground under the figure.
		var poolTexture = new GradientTexture2D
		{
			Gradient = new Gradient
			{
				Offsets = new float[] { 0.0f, 0.55f, 1.0f },
				Colors = new Color[]
				{
					new Color(0.85f, 0.12f, 0.10f, 0.55f),
					new Color(0.60f, 0.06f, 0.05f, 0.18f),
					new Color(0.50f, 0.05f, 0.05f, 0.0f),
				},
			},
			Width = 256,
			Height = 256,
			Fill = GradientTexture2D.FillEnum.Radial,
			FillFrom = new Vector2(0.5f, 0.5f),
			FillTo = new Vector2(1.0f, 0.5f),
		};
		_pool = new MeshInstance3D
		{
			Mesh = new QuadMesh { Size = new Vector2(2.6f, 2.6f) },
			Position = new Vector3(0.0f, 0.004f, 0.0f),
			RotationDegrees = new Vector3(-90.0f, 0.0f, 0.0f),
			MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				AlbedoTexture = poolTexture,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			},
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		_viewport.AddChild(_pool);

		_pivot = new Node3D { Name = "Pivot" };
		_viewport.AddChild(_pivot);
	}

	private void Reframe()
	{
		if (!_built) return;
		Vector2I size = _viewport.Size;
		float aspect = size.Y > 0 ? (float)size.X / size.Y : 1.0f;
		float tanHalf = Mathf.Tan(Mathf.DegToRad(_camera.Fov * 0.5f));
		float byHeight = (_frameHeight * 0.5f) / tanHalf;
		float byWidth = (_frameWidth * 0.5f) / (tanHalf * aspect);
		_baseDistance = Mathf.Max(byHeight, byWidth);
		ApplyCamera();
	}

	private void ApplyCamera()
	{
		Vector3 target = _lookAt + new Vector3(0.0f, _pan, 0.0f);
		_camera.LookAtFromPosition(target + new Vector3(0.0f, 0.1f, _baseDistance / _zoom), target, Vector3.Up);
	}

	public override void _Process(double delta)
	{
		if (!_built) return;

		Vector2I size = _viewport.Size;
		if (size != _lastSize)
		{
			_lastSize = size;
			Reframe();
		}

		float step = (float)delta;
		float ease = 1.0f - Mathf.Exp(-10.0f * step);
		_time += step;

		// Coast after a flick, then settle.
		if (!_dragging)
		{
			_targetYaw += _velocity * step * 60.0f;
			_velocity *= Mathf.Pow(0.90f, step * 60.0f);
			if (Mathf.Abs(_velocity) < 0.02f) _velocity = 0.0f;
		}
		if (_dragging || _velocity != 0.0f) _idle = 0.0f; else _idle += step;

		_zoom = Mathf.Lerp(_zoom, _zoomTarget, ease);
		float panLimit = _weaponMode ? 0.0f : 0.9f * Mathf.Max(0.0f, 1.0f - 1.0f / _zoom);
		_panTarget = Mathf.Clamp(_panTarget, -panLimit, panLimit);
		_pan = Mathf.Lerp(_pan, _panTarget, ease);
		_pitch = Mathf.Lerp(_pitch, _pitchTarget, ease);
		_yaw = Mathf.Lerp(_yaw, _targetYaw, 1.0f - Mathf.Exp(-9.0f * step));
		ApplyCamera();

		// The idle sway fades out while you are handling the figure and back in after a moment.
		_swayBlend = Mathf.Lerp(_swayBlend, _idle > 1.5f ? 1.0f : 0.0f, 1.0f - Mathf.Exp(-3.0f * step));
		if (_weaponMode)
			_pivot.RotationDegrees = new Vector3(6.0f + _pitch, _yaw + Mathf.Sin(_time * 0.8f) * 26.0f * _swayBlend, 0.0f);
		else
			_pivot.RotationDegrees = new Vector3(0.0f, OperatorBaseYaw + _yaw + Mathf.Sin(_time * 0.45f) * 6.0f * _swayBlend, 0.0f);
	}

	// -------------------------------------------------------------------------------------------
	// Helpers
	// -------------------------------------------------------------------------------------------

	private void ClearModel()
	{
		if (_model != null && IsInstanceValid(_model))
		{
			_model.GetParent()?.RemoveChild(_model);
			_model.QueueFree();
		}
		_model = null;
		_tint = null;
		_attachment = null;
	}

	/// <summary>Merges the bounds of every mesh under node into one box, in the space of the node
	/// that was first passed in (its own transform excluded).</summary>
	private static void AccumulateBounds(Node3D node, Transform3D accumulated, ref Aabb result, ref bool has)
	{
		if (node is MeshInstance3D instance && instance.Mesh != null)
		{
			Aabb box = accumulated * instance.GetAabb();
			result = has ? result.Merge(box) : box;
			has = true;
		}
		foreach (Node child in node.GetChildren())
			if (child is Node3D child3D)
				AccumulateBounds(child3D, accumulated * child3D.Transform, ref result, ref has);
	}
}
