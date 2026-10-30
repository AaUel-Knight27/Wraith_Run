#nullable enable
using Godot;

namespace WraithRun.MapKit;

/// <summary>
/// One physical surface (asphalt, dirt, concrete, metal...). This is the only "material" type the
/// map kit asks you to fill in: the ambientCG importer writes one .tres per downloaded material,
/// and you drag it into the slots of OutdoorMapBuilder / IndoorMapBuilder / ScatterLayer.
///
/// Meshes made by the kit carry UVs in METRES, so one SurfaceLayer looks the same size on a wall,
/// a floor or a terrain chunk; TileMeters says how many metres one texture repeat covers.
///
/// A layer with no textures still works (it is just a tinted, rough surface), so a map can be
/// blocked out before any texture has been imported.
/// </summary>
[Tool]
[GlobalClass]
public partial class SurfaceLayer : Resource
{
    [Export] public Texture2D? Albedo { get; set; }

    /// <summary>OpenGL-style (Y+) normal map. ambientCG's "NormalGL" file.</summary>
    [Export] public Texture2D? Normal { get; set; }

    /// <summary>Packed texture: R = ambient occlusion, G = roughness, B = metalness. One sample instead of three.</summary>
    [Export] public Texture2D? Orm { get; set; }

    [Export] public Color Tint { get; set; } = Colors.White;

    [Export(PropertyHint.Range, "0.25,32,0.05,or_greater")]
    public float TileMeters { get; set; } = 2.0f;

    [Export(PropertyHint.Range, "0,4,0.05")]
    public float NormalStrength { get; set; } = 1.0f;

    /// <summary>Used only when there is no ORM texture.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float Roughness { get; set; } = 0.9f;

    /// <summary>Used only when there is no ORM texture.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float Metallic { get; set; }

    /// <summary>Projects the texture from 3 axes instead of using UVs. Good for rocks; costs 3x the samples.</summary>
    [Export] public bool Triplanar { get; set; }

    /// <summary>Multiplies the albedo by the mesh's vertex colours (used to tint parts of one prop mesh).</summary>
    [Export] public bool UseVertexColor { get; set; }

    /// <summary>Alpha-blended (glass). Tint's alpha is the opacity.</summary>
    [Export] public bool Transparent { get; set; }

    /// <summary>Self-illumination colour. Black = none.</summary>
    [Export] public Color Emission { get; set; } = Colors.Black;

    [Export(PropertyHint.Range, "0,8,0.05")]
    public float EmissionEnergy { get; set; } = 1.0f;

    // ------------------------------------------------------------ terrain blend (optional)

    /// <summary>
    /// Second ground look, painted in where the mesh's vertex colour RED is high (e.g. burnt black soil).
    /// Leave both blend layers empty and this layer behaves exactly as before.
    /// </summary>
    [ExportGroup("Terrain blend")]
    [Export] public SurfaceLayer? BlendLayerB { get; set; }

    /// <summary>Third ground look, painted in where vertex colour GREEN is high (e.g. moss). This layer itself is what shows where both are zero.</summary>
    [Export] public SurfaceLayer? BlendLayerC { get; set; }

    /// <summary>How strongly a slow, big-scale brightness wobble breaks up texture repetition. 0 = off.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float MacroVariation { get; set; } = 0.3f;

    /// <summary>Size in metres of that brightness wobble.</summary>
    [Export(PropertyHint.Range, "4,120,0.5,or_greater")] public float MacroScaleMeters { get; set; } = 28.0f;

    /// <summary>Higher = sharper, more broken edges between the blended looks.</summary>
    [Export(PropertyHint.Range, "0.05,0.8,0.01")] public float BlendEdgeSoftness { get; set; } = 0.22f;

    public bool HasBlend => BlendLayerB != null || BlendLayerC != null;

    private const string BlendShaderPath = "res://art/shaders/terrain_blend.gdshader";

    /// <summary>
    /// Builds a 3-look terrain material. The mesh must carry the weights in its vertex colours
    /// (R = look B, G = look C). Falls back to the plain material if the shader cannot be loaded.
    /// </summary>
    public Material CreateBlendMaterial()
    {
        var shader = GD.Load<Shader>(BlendShaderPath);
        if (shader == null)
        {
            GD.PushWarning($"SurfaceLayer: {BlendShaderPath} is missing; using the plain ground material instead.");
            return CreateMaterial();
        }

        var m = new ShaderMaterial { Shader = shader };
        SetBlendSlot(m, "a", this);
        SetBlendSlot(m, "b", BlendLayerB ?? this);
        SetBlendSlot(m, "c", BlendLayerC ?? this);
        m.SetShaderParameter("macro_variation", MacroVariation);
        m.SetShaderParameter("macro_tile", 1.0f / Mathf.Max(MacroScaleMeters, 0.01f));
        m.SetShaderParameter("edge_softness", BlendEdgeSoftness);
        return m;
    }

    private static void SetBlendSlot(ShaderMaterial m, string slot, SurfaceLayer layer)
    {
        m.SetShaderParameter(slot + "_albedo", layer.Albedo);
        m.SetShaderParameter(slot + "_normal", layer.Normal);
        m.SetShaderParameter(slot + "_has_normal", layer.Normal != null);
        m.SetShaderParameter(slot + "_tint", layer.Tint);
        m.SetShaderParameter(slot + "_tile", 1.0f / Mathf.Max(layer.TileMeters, 0.01f));
        m.SetShaderParameter(slot + "_normal_strength", layer.NormalStrength);
        m.SetShaderParameter(slot + "_rough", layer.Roughness);
    }

    public static SurfaceLayer Solid(Color colour, float roughness = 0.9f, float metallic = 0.0f, float tileMeters = 2.0f)
    {
        return new SurfaceLayer { Tint = colour, Roughness = roughness, Metallic = metallic, TileMeters = tileMeters };
    }

    /// <summary>Builds the engine material for this layer. Cheap; callers cache the result per build.</summary>
    public BaseMaterial3D CreateMaterial()
    {
        BaseMaterial3D m = Orm != null ? new OrmMaterial3D() : new StandardMaterial3D();
        m.AlbedoColor = Tint;
        if (Albedo != null) m.AlbedoTexture = Albedo;

        if (Normal != null)
        {
            m.NormalEnabled = true;
            m.NormalTexture = Normal;
            m.NormalScale = NormalStrength;
        }

        if (Orm != null)
        {
            m.OrmTexture = Orm;
            m.AOEnabled = true;
            m.AOLightAffect = 0.35f;
            // With a packed texture these act as multipliers on the texture, so 1 = "use the map as is".
            m.Roughness = 1.0f;
            m.Metallic = 1.0f;
        }
        else
        {
            m.Roughness = Roughness;
            m.Metallic = Metallic;
        }

        float s = 1.0f / Mathf.Max(TileMeters, 0.01f);
        m.Uv1Scale = new Vector3(s, s, s);
        m.Uv1Triplanar = Triplanar;
        m.TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
        m.VertexColorUseAsAlbedo = UseVertexColor;

        if (Transparent)
        {
            m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }

        if (EmissionEnergy > 0.0f && (Emission.R > 0.0f || Emission.G > 0.0f || Emission.B > 0.0f))
        {
            m.EmissionEnabled = true;
            m.Emission = Emission;
            m.EmissionEnergyMultiplier = EmissionEnergy;
        }

        return m;
    }
}
