using Godot;

/// <summary>
/// The tablet as a real object held in front of the camera, with the menu drawn ON its screen.
///
/// The menu (HubTablet) lives in <see cref="Screen"/>, a fixed-size SubViewport whose aspect ratio is
/// the aspect of the tablet's glass, so the layout always fits the screen of the device instead of the
/// game window. A quad sits on the glass of rugged_tactical_tablet.glb and shows that viewport.
/// Mouse/touch are mapped back onto the viewport through the glass (<see cref="ScreenPoint"/>).
///
/// The glass numbers below were measured on the model itself (ray hits against its mesh plus a render
/// of the screen face), in the model's own metres: the screen faces +X, its right is -Z, its up is +Y.
/// </summary>
public partial class TabletDevice : Node3D
{
    public const string ModelPath = "res://art/ui/tablet/rugged_tactical_tablet.glb";

    private const float GlassX = 0.0265f;        // just in front of the dished glass, behind the bezel's lip
    private const float GlassCentreY = -0.011f;
    private const float GlassCentreZ = 0.0012f;
    private const float GlassWidth = 0.172f;     // along -Z
    private const float GlassHeight = 0.122f;    // along +Y
    private const float BodyWidth = 0.302f;      // the whole device, bumpers included
    private const float HoldDistance = 0.30f;    // camera to glass, metres

    /// <summary>Pixel size of the tablet's screen (same aspect as the glass).</summary>
    public static readonly Vector2I ScreenSize = new(1000, 709);

    public SubViewport Screen { get; }
    private MeshInstance3D? _glass;
    private Tween? _tween;
    private Vector3 _rest;

    public TabletDevice()
    {
        Name = "TabletDevice";
        Visible = false;
        Screen = new SubViewport
        {
            Name = "TabletScreen",
            Size = ScreenSize,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            TransparentBg = false,
            Disable3D = true,
            HandleInputLocally = false,
        };
        AddChild(Screen);
    }

    public override void _Ready()
    {
        var scene = ResourceLoader.Load<PackedScene>(ModelPath);
        if (scene != null)
        {
            Node3D model = scene.Instantiate<Node3D>();
            model.Name = "Body";
            AddChild(model);
        }
        else GD.PushWarning($"Tablet model missing ({ModelPath}); only the screen will show.");

        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoTexture = Screen.GetTexture(),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
            DisableFog = true,
            DisableReceiveShadows = true,
        };
        _glass = new MeshInstance3D
        {
            Name = "Glass",
            Mesh = new QuadMesh { Size = new Vector2(GlassWidth, GlassHeight) },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(GlassX, GlassCentreY, GlassCentreZ),
            // a quad faces +Z with +X to its right; turning it 90 degrees about Y makes it face +X
            // with its right along -Z, which is how the screen reads on the model
            RotationDegrees = new Vector3(0.0f, 90.0f, 0.0f),
        };
        AddChild(_glass);

        // a little light so the bezel is readable in the dark warehouse
        AddChild(new OmniLight3D { Name = "Lamp", Position = new Vector3(0.45f, 0.02f, 0.0f), OmniRange = 1.4f, LightEnergy = 0.9f, ShadowEnabled = false });
    }

    /// <summary>Size and place the device for this camera: the screen takes about three quarters of the
    /// view height, and the whole tablet stays inside the window.</summary>
    public void Fit(Camera3D camera)
    {
        Vector2 view = GetViewport().GetVisibleRect().Size;
        float aspect = view.X / Mathf.Max(1.0f, view.Y);
        float tanHalf = Mathf.Tan(Mathf.DegToRad(camera.Fov) * 0.5f);
        float visibleW, visibleH;
        if (camera.KeepAspect == Camera3D.KeepAspectEnum.Width)
        {
            visibleW = 2.0f * HoldDistance * tanHalf;
            visibleH = visibleW / aspect;
        }
        else
        {
            visibleH = 2.0f * HoldDistance * tanHalf;
            visibleW = visibleH * aspect;
        }
        // held up close: the glass takes most of the view and the bumpers run off the sides
        float s = Mathf.Min(0.90f * visibleH / GlassHeight, 0.92f * visibleW / GlassWidth);
        Scale = Vector3.One * s;
        // the model's screen normal (+X) must point back at the camera (+Z), so turn it -90 degrees about Y
        RotationDegrees = new Vector3(0.0f, -90.0f, 0.0f);
        // put the middle of the glass on the camera's axis, HoldDistance away
        _rest = new Vector3(GlassCentreZ * s, -GlassCentreY * s, -HoldDistance - GlassX * s);
        Position = _rest;
    }

    public void Raise()
    {
        _tween?.Kill();
        Screen.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        Visible = true;
        Position = _rest + new Vector3(0.0f, -0.18f, 0.05f);
        _tween = CreateTween();
        _tween.TweenProperty(this, "position", _rest, 0.2).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }

    public void Lower()
    {
        _tween?.Kill();
        Visible = false;
        Screen.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    /// <summary>Where on the tablet's screen (in its pixels) a point of the game window lands, if it
    /// lands on the glass at all.</summary>
    public bool ScreenPoint(Camera3D camera, Vector2 windowPoint, out Vector2 pixel)
    {
        pixel = default;
        if (_glass == null) return false;
        Vector3 origin = camera.ProjectRayOrigin(windowPoint);
        Vector3 direction = camera.ProjectRayNormal(windowPoint);
        Transform3D glass = _glass.GlobalTransform;
        Vector3 normal = glass.Basis.Z.Normalized();
        float denominator = direction.Dot(normal);
        if (Mathf.Abs(denominator) < 1e-5f) return false;
        float distance = (glass.Origin - origin).Dot(normal) / denominator;
        if (distance <= 0.0f) return false;
        Vector3 local = glass.AffineInverse() * (origin + direction * distance);
        float u = local.X / GlassWidth + 0.5f;
        float v = 0.5f - local.Y / GlassHeight;
        if (u < 0.0f || u > 1.0f || v < 0.0f || v > 1.0f) return false;
        pixel = new Vector2(u * ScreenSize.X, v * ScreenSize.Y);
        return true;
    }
}
