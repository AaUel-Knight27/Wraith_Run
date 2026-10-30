using Godot;

/// <summary>
/// Drifting ash and dust for a map's atmosphere. Drop it as a child of the map root.
///
/// Why it follows the camera: a fixed cloud over a 100 m map would be so sparse you would almost never
/// see a flake. A small volume that rides on the local camera - with particles left behind in WORLD
/// space - gives a steady, believable fall everywhere the player actually looks, for a few hundred
/// flakes in total. Everything is built in code so there is no particle scene to keep in sync.
///
/// Cost: one GPU particle system, no shadows, no lights. Turn Amount down first if a weak GPU struggles.
/// </summary>
public partial class AmbientAsh : Node3D
{
	[Export(PropertyHint.Range, "20,1500,10")] public int Amount { get; set; } = 380;
	[Export] public Vector3 HalfExtents { get; set; } = new(18.0f, 7.0f, 18.0f);
	/// <summary>Height of the volume's centre above the camera.</summary>
	[Export] public float HeightAboveCamera { get; set; } = 2.0f;
	[Export(PropertyHint.Range, "0.01,0.3,0.005")] public float FlakeSize { get; set; } = 0.045f;
	[Export] public Color FlakeColor { get; set; } = new(0.8f, 0.76f, 0.7f, 0.5f);
	/// <summary>Sideways drift (the wind) plus a slow fall, in metres per second.</summary>
	[Export] public Vector3 Drift { get; set; } = new(0.45f, -0.3f, 0.15f);
	[Export(PropertyHint.Range, "2,30,0.5")] public float Lifetime { get; set; } = 11.0f;

	private GpuParticles3D? _particles;

	public override void _Ready()
	{
		// A dedicated server or headless run has nothing to draw; do not build GPU resources for it.
		if (OS.HasFeature("headless") || OS.HasFeature("dedicated_server") || DisplayServer.GetName() == "headless")
		{
			SetProcess(false);
			return;
		}

		var gradient = new Gradient();
		gradient.SetColor(0, new Color(1, 1, 1, 1));
		gradient.SetColor(1, new Color(1, 1, 1, 0));
		var dot = new GradientTexture2D
		{
			Gradient = gradient,
			Fill = GradientTexture2D.FillEnum.Radial,
			FillFrom = new Vector2(0.5f, 0.5f),
			FillTo = new Vector2(1.0f, 0.5f),
			Width = 32,
			Height = 32,
		};

		var flakeMaterial = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
			VertexColorUseAsAlbedo = true,
			AlbedoTexture = dot,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
		};

		var process = new ParticleProcessMaterial
		{
			EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
			EmissionBoxExtents = HalfExtents,
			Direction = Drift.Normalized(),
			Spread = 25.0f,
			InitialVelocityMin = Drift.Length() * 0.6f,
			InitialVelocityMax = Drift.Length() * 1.3f,
			Gravity = Vector3.Zero,
			ScaleMin = 0.5f,
			ScaleMax = 1.4f,
			Color = FlakeColor,
		};

		_particles = new GpuParticles3D
		{
			Name = "Flakes",
			Amount = Amount,
			Lifetime = Lifetime,
			Preprocess = Lifetime,
			LocalCoords = false,
			ProcessMaterial = process,
			DrawPass1 = new QuadMesh { Size = new Vector2(FlakeSize, FlakeSize), Material = flakeMaterial },
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			// Flakes wander, so the bounding box has to be generous or they pop out of view at its edge.
			VisibilityAabb = new Aabb(-HalfExtents * 2.0f, HalfExtents * 4.0f),
		};
		AddChild(_particles);
	}

	public override void _Process(double delta)
	{
		if (_particles == null) return;
		Camera3D? cam = GetViewport().GetCamera3D();
		if (cam == null) return;
		_particles.GlobalPosition = cam.GlobalPosition + new Vector3(0.0f, HeightAboveCamera, 0.0f);
	}
}
