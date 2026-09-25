#nullable enable
using Godot;

namespace WraithRun.MapKit;

/// <summary>
/// Converts ambientCG texture folders into ready-to-use SurfaceLayer resources.
///
/// IMPORTANT — which ambientCG download to pick: ambientCG offers several download types per material
/// (JPG, 1K/2K/4K/8K-PNG, EXR, and a "Blend" package that is a ready-made Blender node setup). Always
/// download the plain **1K-JPG** (or 2K-PNG) package, never "Blend" — this tool reads the individual
/// image files directly and has no use for a Blender file, and Godot cannot open one either. 1K is
/// plenty for this hardware; see the reply for why.
///
/// Setup: unzip each material's download into its own folder under SourceFolder, e.g.
///   res://art/_ambientcg_source/Asphalt026/Asphalt026_1K-JPG_Color.jpg
///   res://art/_ambientcg_source/Asphalt026/Asphalt026_1K-JPG_NormalGL.jpg
///   res://art/_ambientcg_source/Asphalt026/Asphalt026_1K-JPG_Roughness.jpg
///   res://art/_ambientcg_source/Asphalt026/Asphalt026_1K-JPG_AmbientOcclusion.jpg   (optional)
///   res://art/_ambientcg_source/Asphalt026/Asphalt026_1K-JPG_Metalness.jpg          (optional, rare)
/// Then click "Import All Materials". One SurfaceLayer .tres appears per subfolder in OutputFolder,
/// named after the folder, ready to drop into a builder's surface slots.
///
/// AmbientOcclusion + Roughness + Metalness are packed into a single ORM texture (red/green/blue
/// channels) instead of kept as three textures — one texture sample instead of three, which matters
/// more on an integrated GPU than almost anything else in this kit.
/// </summary>
[Tool]
[GlobalClass]
public partial class MaterialImporter : Node
{
    [Export] public string SourceFolder { get; set; } = "res://art/_ambientcg_source/";
    [Export] public string OutputFolder { get; set; } = "res://art/materials/";

    [Export(PropertyHint.Range, "0.25,16,0.05,or_greater")]
    public float DefaultTileMeters { get; set; } = 2.0f;

    [Export] public bool OverwriteExisting { get; set; }

    [ExportToolButton("Import All Materials")]
    public Callable ImportAllButton => Callable.From(ImportAll);

    public void ImportAll()
    {
        if (!Engine.IsEditorHint())
        {
            MapKitUtil.Warn("MaterialImporter is an editor-time tool; it does not run in an exported game.");
            return;
        }

        if (!DirAccess.DirExistsAbsolute(SourceFolder))
        {
            MapKitUtil.Warn($"Source folder not found: {SourceFolder}");
            return;
        }
        DirAccess.MakeDirRecursiveAbsolute(OutputFolder);

        using DirAccess? root = DirAccess.Open(SourceFolder);
        if (root == null)
        {
            MapKitUtil.Warn($"Could not open {SourceFolder}");
            return;
        }

        int imported = 0, skipped = 0;
        foreach (string sub in root.GetDirectories())
        {
            string folder = SourceFolder.TrimSuffix("/") + "/" + sub + "/";
            string outPath = OutputFolder.TrimSuffix("/") + "/" + sub + ".tres";
            if (!OverwriteExisting && FileAccess.FileExists(outPath))
            {
                skipped++;
                continue;
            }
            if (ImportOne(folder, sub, outPath)) imported++;
            else skipped++;
        }

        MapKitUtil.Log($"MaterialImporter: {imported} material(s) imported, {skipped} skipped. See individual warnings above for anything missing.");
    }

    private bool ImportOne(string folder, string materialName, string outPath)
    {
        using DirAccess? dir = DirAccess.Open(folder);
        if (dir == null)
        {
            MapKitUtil.Warn($"'{materialName}': could not open its folder.");
            return false;
        }

        string[] files = dir.GetFiles();
        string? colorFile = FindMap(files, "color");
        string? normalFile = FindMap(files, "normalgl") ?? FindMap(files, "normal_gl");
        string? roughFile = FindMap(files, "roughness");
        string? aoFile = FindMap(files, "ambientocclusion") ?? FindMap(files, "ambient_occlusion");
        string? metalFile = FindMap(files, "metalness");

        if (colorFile == null)
        {
            MapKitUtil.Warn($"'{materialName}': no *_Color.* file found — skipped. (Did you download the 'Blend' package by mistake? You need the plain JPG/PNG package instead.)");
            return false;
        }

        Image colorImg = Image.LoadFromFile(folder + colorFile);
        var layer = new SurfaceLayer
        {
            Albedo = SaveTexture(colorImg, materialName + "_Color"),
            TileMeters = DefaultTileMeters,
        };

        if (normalFile != null)
        {
            layer.Normal = SaveTexture(Image.LoadFromFile(folder + normalFile), materialName + "_Normal");
        }
        else
        {
            MapKitUtil.Warn($"'{materialName}': no NormalGL map found; surface will shade flat. (If only a NormalDX file exists, it will look inverted — ambientCG's GL variant is the one this kit needs.)");
        }

        if (roughFile != null || aoFile != null || metalFile != null)
        {
            Image orm = ComposeOrm(colorImg.GetSize(), folder, aoFile, roughFile, metalFile);
            layer.Orm = SaveTexture(orm, materialName + "_ORM");
        }
        else
        {
            MapKitUtil.Warn($"'{materialName}': no Roughness/AO/Metalness maps found; using flat fallback values.");
        }

        Error err = ResourceSaver.Save(layer, outPath);
        if (err != Error.Ok)
        {
            MapKitUtil.Warn($"'{materialName}': failed to save {outPath} ({err}).");
            return false;
        }
        return true;
    }

    private static string? FindMap(string[] files, string token)
    {
        foreach (string f in files)
        {
            string lower = f.ToLowerInvariant();
            if (lower.EndsWith(".import")) continue;
            if (lower.Contains("normaldx") || lower.Contains("normal_dx")) continue; // never the DX variant
            if (lower.Contains(token)) return f;
        }
        return null;
    }

    private Texture2D SaveTexture(Image image, string name)
    {
        var texture = ImageTexture.CreateFromImage(image);
        string path = OutputFolder.TrimSuffix("/") + "/" + name + ".res";
        Error err = ResourceSaver.Save(texture, path);
        if (err == Error.Ok) texture.TakeOverPath(path);
        else MapKitUtil.Warn($"Could not save {path} ({err}); texture stays embedded in the SurfaceLayer.");
        return texture;
    }

    /// <summary>R = AO (default white/no occlusion), G = Roughness (default 0.9), B = Metalness (default 0, non-metal).</summary>
    private static Image ComposeOrm(Vector2I size, string folder, string? aoFile, string? roughFile, string? metalFile)
    {
        Image? ao = LoadAndResize(folder, aoFile, size);
        Image? rough = LoadAndResize(folder, roughFile, size);
        Image? metal = LoadAndResize(folder, metalFile, size);

        var orm = Image.CreateEmpty(size.X, size.Y, false, Image.Format.Rgb8);
        for (int y = 0; y < size.Y; y++)
        {
            for (int x = 0; x < size.X; x++)
            {
                float r = ao?.GetPixel(x, y).R ?? 1.0f;
                float g = rough?.GetPixel(x, y).R ?? 0.9f;
                float b = metal?.GetPixel(x, y).R ?? 0.0f;
                orm.SetPixel(x, y, new Color(r, g, b));
            }
        }
        return orm;
    }

    private static Image? LoadAndResize(string folder, string? file, Vector2I size)
    {
        if (file == null) return null;
        Image img = Image.LoadFromFile(folder + file);
        if (img.GetSize() != size) img.Resize(size.X, size.Y, Image.Interpolation.Lanczos);
        return img;
    }
}
