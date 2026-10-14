using Godot;

/// <summary>Reusable, pooled visual-only muzzle effect. Its parent is always the live muzzle anchor.</summary>
public partial class MuzzleFireEffect : Node3D
{
	private readonly RandomNumberGenerator _random = new();
	private readonly MeshInstance3D[] _flashCards = new MeshInstance3D[3];
	private OmniLight3D? _light;
	private CpuParticles3D? _gas;
	private CpuParticles3D? _smoke;
	private ViewModelWeaponData? _data;
	private float _flashRemaining;
	private Vector3 _lastAnchorPosition;
	private bool _haveLastAnchorPosition;

	public bool ShowGizmo { get; set; }
	/// <summary>Viewmodels deliberately render over world depth; third-person weapon effects do not.</summary>
	public bool NoDepthTest { get; set; } = true;

	public void Configure(ViewModelWeaponData data, uint renderLayers)
	{
		_data = data;
		for (int i = 0; i < _flashCards.Length; i++)
			if (_flashCards[i] != null)
			{
				_flashCards[i].Layers = renderLayers;
				if (_flashCards[i].MaterialOverride is StandardMaterial3D material) material.AlbedoColor = data.MuzzleFlashColor;
			}
		if (_gas != null) _gas.Layers = renderLayers;
		if (_smoke != null) _smoke.Layers = renderLayers;
		if (_light != null) _light.LightColor = data.MuzzleFlashColor;
	}

	public override void _Ready()
	{
		_random.Randomize();
		BuildPool();
		if (_data != null) Configure(_data, 1);
		SetProcess(false);
	}

	public void Fire()
	{
		if (_data == null || !_data.MuzzleFlashEnabled) return;
		Vector3 current = GlobalPosition;
		Vector3 inheritedVelocity = _haveLastAnchorPosition ? (current - _lastAnchorPosition) * 60.0f : Vector3.Zero;
		_lastAnchorPosition = current;
		_haveLastAnchorPosition = true;
		_flashRemaining = Mathf.Clamp(_data.MuzzleFlashDuration, 0.04f, 0.07f);
		for (int i = 0; i < _flashCards.Length; i++)
		{
			MeshInstance3D card = _flashCards[i];
			card.Visible = true;
			card.RotationDegrees = new Vector3(0.0f, 0.0f, _random.RandfRange(0.0f, 360.0f));
			card.Scale = Vector3.One * _data.MuzzleFlashScale * _random.RandfRange(0.8f, 1.2f);
		}
		if (_light != null)
		{
			_light.LightEnergy = _data.MuzzleLightEnergy * _random.RandfRange(0.8f, 1.15f);
			_light.Visible = _data.MuzzleLightEnergy > 0.0f;
		}
		RestartParticles(_gas, _data.MuzzleParticleSpeed, _data.MuzzleSpreadDegrees, inheritedVelocity, false);
		if (_data.MuzzleSmokeEnabled)
			RestartParticles(_smoke, _data.MuzzleParticleSpeed * 0.35f, _data.MuzzleSpreadDegrees * 1.5f, inheritedVelocity, true);
		SetProcess(true);
	}

	public override void _Process(double delta)
	{
		_flashRemaining -= (float)delta;
		if (_light != null && _flashRemaining <= 0.033f) _light.Visible = false;
		if (_flashRemaining > 0.0f) return;
		for (int i = 0; i < _flashCards.Length; i++) _flashCards[i].Visible = false;
		if (_light != null) _light.Visible = false;
		SetProcess(false);
	}

	private void BuildPool()
	{
		QuadMesh quad = new() { Size = new Vector2(0.18f, 0.18f) };
		for (int i = 0; i < _flashCards.Length; i++)
		{
			var material = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				BlendMode = BaseMaterial3D.BlendModeEnum.Add,
				VertexColorUseAsAlbedo = true,
				BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
				NoDepthTest = NoDepthTest,
			};
			var card = new MeshInstance3D
			{
				Name = $"FlashCard{i + 1}", Mesh = quad, MaterialOverride = material,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false,
				ExtraCullMargin = 2.0f,
			};
			card.RotationDegrees = new Vector3(0.0f, 0.0f, i * 60.0f);
			AddChild(card);
			_flashCards[i] = card;
		}
		_light = new OmniLight3D
		{
			Name = "MuzzleLight", ShadowEnabled = false, OmniRange = 3.0f, Visible = false,
			LightColor = _data?.MuzzleFlashColor ?? Colors.White,
		};
		AddChild(_light);
		_gas = MakeParticles("MuzzleGas", 0.12f, 18, new Color(1.0f, 0.65f, 0.2f, 0.55f));
		_smoke = MakeParticles("MuzzleSmoke", 0.45f, 10, new Color(0.35f, 0.35f, 0.35f, 0.28f));
	}

	private CpuParticles3D MakeParticles(string name, float lifetime, int amount, Color color)
	{
		var material = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			BlendMode = BaseMaterial3D.BlendModeEnum.Add,
			AlbedoColor = color, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
			NoDepthTest = NoDepthTest,
		};
		var particles = new CpuParticles3D
		{
			Name = name, Amount = amount, Lifetime = lifetime, OneShot = true, Explosiveness = 1.0f,
			LocalCoords = false, Emitting = false, Direction = Vector3.Back, Spread = 12.0f,
			Mesh = new QuadMesh { Size = new Vector2(0.025f, 0.025f), Material = material },
			Gravity = Vector3.Zero,
		};
		AddChild(particles);
		return particles;
	}

	private void RestartParticles(CpuParticles3D? particles, float speed, float spread, Vector3 inheritedVelocity, bool smoke)
	{
		if (particles == null) return;
		// Direction is expressed in the anchor's local frame. Adding the local velocity keeps a
		// world-space trail from looking detached when recoil/camera motion shifts the weapon.
		Vector3 localVelocity = GlobalTransform.Basis.Inverse() * inheritedVelocity;
		particles.Direction = (Vector3.Back + localVelocity * 0.03f).Normalized();
		particles.Spread = Mathf.Clamp(spread, 0.0f, 85.0f);
		particles.InitialVelocityMin = Mathf.Max(0.01f, speed * (smoke ? 0.5f : 0.75f));
		particles.InitialVelocityMax = Mathf.Max(particles.InitialVelocityMin, speed * (smoke ? 0.8f : 1.25f));
		particles.Emitting = true;
		particles.Restart();
	}
}
