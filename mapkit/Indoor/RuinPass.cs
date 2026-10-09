#nullable enable
using System;
using Godot;

namespace WraithRun.MapKit;

/// <summary>
/// Adds a removable, deterministic layer of ruin dressing to a map. The pass only creates
/// children beneath a node named Ruin; it never changes the source meshes or collisions.
/// </summary>
[Tool]
[GlobalClass]
public partial class RuinPass : Node3D
{
    private bool _enabled = true;

    [Export(PropertyHint.Range, "0,1,0.01")] public float DamageAmount { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float DebrisDensity { get; set; } = 0.8f;
    [Export] public int Seed { get; set; } = 47291;
    [Export]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!IsInsideTree() || GetParent() is not Node3D parent) return;
            if (value) ApplyTo(parent);
            else
            {
                _built = false;
                DeferRemoveRuin(parent);
            }
        }
    }

    /// <summary>Map-specific intensity multiplier. Prison uses 1.25; cleaner maps can use less.</summary>
    [Export(PropertyHint.Range, "0,2,0.05")] public float MapDamageMultiplier { get; set; } = 1.25f;

    private bool _built;
    private RandomNumberGenerator _rng = new();
    private StandardMaterial3D _concrete = null!;
    private StandardMaterial3D _darkConcrete = null!;
    private StandardMaterial3D _rebarMaterial = null!;
    private StandardMaterial3D _woodMaterial = null!;
    private StandardMaterial3D _sootMaterial = null!;
    private double _flickerTimer;
    private int _debrisCount;
    private int _decalCount;
    private Node3D? _targetRoot;
    private bool _buildPending;

    public override void _Ready()
    {
        if (GetParent() is Node3D parent)
            ApplyTo(parent);
    }

    public override void _Process(double delta)
    {
        if (_buildPending)
        {
            _buildPending = false;
            if (GodotObject.IsInstanceValid(_targetRoot))
                BuildDeferred(_targetRoot!);
            return;
        }

        if (!_built || !Enabled || !IsInstanceValid(this)) return;
        _flickerTimer -= delta;
        if (_flickerTimer > 0 || !GodotObject.IsInstanceValid(_targetRoot)) return;
        _flickerTimer = _rng.RandfRange(0.07f, 0.26f);
        Node3D? ruin = _targetRoot!.GetNodeOrNull<Node3D>("Ruin");
        if (ruin == null) return;
        int index = 0;
        foreach (Node child in ruin.GetChildren())
        {
            if (child is not OmniLight3D light || !light.Name.ToString().StartsWith("FailingLight", StringComparison.Ordinal)) continue;
            light.LightEnergy = index++ == 0 ? 0.0f : (_rng.Randf() < 0.12f ? 0.0f : _rng.RandfRange(0.18f, 0.62f));
        }
    }

    /// <summary>Called by a builder after its generated geometry has been refreshed.</summary>
    public void ApplyTo(Node3D targetRoot)
    {
        if (!Enabled || !GodotObject.IsInstanceValid(targetRoot))
        {
            DeferRemoveRuin(targetRoot);
            return;
        }

        _built = false;
        _targetRoot = targetRoot;
        _buildPending = true;
        SetProcess(true);
    }

    public void Rebuild()
    {
        if (GetParent() is Node3D parent)
            ApplyTo(parent);
    }

    [ExportToolButton("Rebuild Ruin")]
    public Callable RebuildButton => Callable.From(Rebuild);

    private void BuildDeferred(Node3D targetRoot)
    {
        if (!Enabled || !GodotObject.IsInstanceValid(targetRoot)) return;

        // Detach before QueueFree so the replacement can safely use the same child name.
        Node? existing = targetRoot.GetNodeOrNull<Node>("Ruin");
        if (existing != null)
        {
            existing.QueueFree();
            targetRoot.RemoveChild(existing);
        }

        var ruin = new Node3D { Name = "Ruin" };
        targetRoot.AddChild(ruin);

        // Ruin is runtime-generated and intentionally has no Owner, so it stays out of the .tscn.
        _rng = new RandomNumberGenerator { Seed = (ulong)Math.Max(1, Seed) };
        _debrisCount = 0;
        _decalCount = 0;
        float damage = Mathf.Clamp(DamageAmount * MapDamageMultiplier, 0.0f, 1.0f);
        CreateMaterials(damage);
        BuildPrisonStyleDressing(ruin, damage);
        _built = true;
        GD.Print($"RuinPass: built {_debrisCount} debris, {_decalCount} decals (damage {DamageAmount:0.00} × map {MapDamageMultiplier:0.00} = {damage:0.00}).");
    }

    private static void DeferRemoveRuin(Node3D targetRoot)
    {
        if (!GodotObject.IsInstanceValid(targetRoot)) return;
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(targetRoot)) return;
            Node? existing = targetRoot.GetNodeOrNull<Node>("Ruin");
            if (existing == null) return;
            existing.QueueFree();
            targetRoot.RemoveChild(existing);
        }).CallDeferred();
    }

    private void CreateMaterials(float damage)
    {
        _concrete = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.29f, 0.30f, 0.29f).Lerp(new Color(0.17f, 0.17f, 0.16f), damage * 0.55f),
            Roughness = 0.98f
        };
        _darkConcrete = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.075f, 0.078f, 0.075f),
            Roughness = 1.0f
        };
        _rebarMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.20f, 0.13f, 0.095f),
            Metallic = 0.35f,
            Roughness = 0.82f
        };
        _woodMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.22f, 0.15f, 0.095f),
            Roughness = 0.94f
        };
        _sootMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.10f, 0.095f, 0.085f, 0.62f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 1.0f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
    }

    private void BuildPrisonStyleDressing(Node3D ruin, float damage)
    {
        // Prison's 62 m square footprint has 8 m perimeter walls and a ceiling at y=8.2.
        // Fracture overlays deliberately leave irregular gaps but sit in front of the source
        // wall, so the source geometry/collision remains intact and the original is reversible.
        AddWallFractures(ruin, damage, "North", new Vector3(0, 4, -29.98f), 62, true);
        AddWallFractures(ruin, damage, "South", new Vector3(0, 4, 29.98f), 62, true);
        AddWallFractures(ruin, damage, "West", new Vector3(-29.98f, 4, 0), 62, false);
        AddWallFractures(ruin, damage, "East", new Vector3(29.98f, 4, 0), 62, false);

        AddCollapsedSlabs(ruin, damage);
        AddRubble(ruin, damage);
        AddBrokenFurniture(ruin, damage);
        AddCracksAndSoot(ruin, damage);
        AddDust(ruin, damage);
        AddRuinLights(ruin, damage);
        AddFogEnvironment(ruin, damage);
    }

    private void AddWallFractures(Node3D ruin, float damage, string label, Vector3 center, float length, bool alongX)
    {
        if (damage <= 0.01f) return;
        int count = Mathf.RoundToInt(3 + damage * 8);
        var mesh = new BoxMesh { Size = new Vector3(1, 1, 0.13f) };
        var multi = MakeMultiMesh(ruin, $"{label}WallFractures", mesh, _darkConcrete, count);
        var edgeMulti = MakeMultiMesh(ruin, $"{label}BrokenEdges", new BoxMesh { Size = new Vector3(1, 1, 0.24f) }, _concrete, count * 2);
        int edgeIndex = 0;

        for (int i = 0; i < count; i++)
        {
            // Reserve the middle corridor and keep damage toward the outer wall thirds.
            float half = length * 0.5f;
            float along = _rng.RandfRange(-half + 3.0f, half - 3.0f);
            if (Mathf.Abs(along) < 9.0f) along = Mathf.Sign(along == 0 ? 1 : along) * _rng.RandfRange(9.0f, 15.0f);
            float y = _rng.RandfRange(0.9f, 6.3f);
            float w = _rng.RandfRange(1.6f, 4.4f) * (0.6f + damage * 0.6f);
            float h = _rng.RandfRange(1.1f, 3.1f) * (0.6f + damage * 0.6f);
            Vector3 p = alongX ? center + new Vector3(along, y, 0) : center + new Vector3(0, y, along);
            Transform3D basis = TransformForBox(p, new Vector3(w, h, 1), alongX ? 0 : Mathf.Pi * 0.5f);
            multi.Multimesh.SetInstanceTransform(i, basis);

            // Split battered edges into offset concrete strips around each dark opening.
            Transform3D upper = TransformForBox(p + new Vector3(0, h * 0.53f, 0), new Vector3(w * 1.08f, 0.12f, 1), alongX ? 0 : Mathf.Pi * 0.5f);
            Transform3D lower = TransformForBox(p - new Vector3(0, h * 0.53f, 0), new Vector3(w * 0.82f, 0.10f, 1), alongX ? 0 : Mathf.Pi * 0.5f);
            if (edgeIndex + 1 < edgeMulti.Multimesh.InstanceCount)
            {
                edgeMulti.Multimesh.SetInstanceTransform(edgeIndex++, upper);
                edgeMulti.Multimesh.SetInstanceTransform(edgeIndex++, lower);
            }
        }
    }

    private void AddCollapsedSlabs(Node3D ruin, float damage)
    {
        int slabs = Mathf.RoundToInt(damage * 5);
        if (slabs < 1) return;
        var slabMesh = new BoxMesh { Size = new Vector3(1, 1, 1) };
        var batch = MakeMultiMesh(ruin, "FallenCeilingSlabs", slabMesh, _concrete, slabs);
        for (int i = 0; i < slabs; i++)
        {
            bool west = i % 2 == 0;
            float z = (i % 2 == 0 ? -14.0f : 14.0f) + _rng.RandfRange(-1.5f, 1.5f);
            float x = west ? _rng.RandfRange(-29.2f, -27.5f) : _rng.RandfRange(27.5f, 29.2f);
            Vector3 size = new(_rng.RandfRange(2.0f, 4.4f), _rng.RandfRange(0.22f, 0.5f), _rng.RandfRange(1.7f, 3.2f));
            batch.Multimesh.SetInstanceTransform(i, TransformForBox(new Vector3(x, 0.0f + size.Y * 0.5f, z), size,
                _rng.RandfRange(-0.4f, 0.4f)));
        }

        // A few heavy chunks get simple box collision. They stay in the outer service band,
        // away from the center lane, PrisonStart, and the marked team spawn areas.
        int colliders = Mathf.Min(2, slabs);
        _debrisCount += slabs + colliders;
        for (int i = 0; i < colliders; i++)
        {
            float x = i == 0 ? -27.5f : 27.5f;
            Vector3 size = new(3.0f, 0.42f, 2.4f);
            var body = new StaticBody3D
            {
                Name = $"HeavySlabCollision_{i}",
                Position = new Vector3(x, size.Y * 0.5f, i == 0 ? -14.0f : 14.0f),
                Rotation = new Vector3(0, _rng.RandfRange(-0.25f, 0.25f), _rng.RandfRange(-0.08f, 0.08f))
            };
            body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = _concrete });
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            ruin.AddChild(body);
        }
    }

    private void AddRubble(Node3D ruin, float damage)
    {
        int count = Mathf.Clamp(Mathf.RoundToInt(DebrisDensity * (25 + damage * 42)), 0, 180);
        if (count == 0) return;
        var rubbleMesh = new BoxMesh { Size = Vector3.One };
        var rubble = MakeMultiMesh(ruin, "Rubble", rubbleMesh, _concrete, count);
        var rebar = MakeMultiMesh(ruin, "TwistedRebar", new CylinderMesh { TopRadius = 0.045f, BottomRadius = 0.045f, Height = 1.0f, RadialSegments = 6 }, _rebarMaterial, Mathf.Max(4, count / 7));

        for (int i = 0; i < count; i++)
        {
            Vector3 p = ChoosePerimeterPoint();
            Vector3 size = new(_rng.RandfRange(0.18f, 0.82f), _rng.RandfRange(0.12f, 0.58f), _rng.RandfRange(0.18f, 0.76f));
            rubble.Multimesh.SetInstanceTransform(i, TransformForBox(new Vector3(p.X, size.Y * 0.5f, p.Z), size,
                _rng.RandfRange(0, Mathf.Tau)));
        }

        for (int i = 0; i < rebar.Multimesh.InstanceCount; i++)
        {
            Vector3 p = ChoosePerimeterPoint();
            float length = _rng.RandfRange(0.8f, 2.3f);
            var xform = new Transform3D(Basis.FromEuler(new Vector3(_rng.RandfRange(-0.55f, 0.55f), _rng.RandfRange(0, Mathf.Tau), _rng.RandfRange(-0.3f, 0.3f))).Scaled(new Vector3(1, length, 1)),
                new Vector3(p.X, 0.12f, p.Z));
            rebar.Multimesh.SetInstanceTransform(i, xform);
        }
        _debrisCount += count + rebar.Multimesh.InstanceCount;
    }

    private Vector3 ChoosePerimeterPoint()
    {
        Vector3 point = Vector3.Zero;
        for (int attempt = 0; attempt < 32; attempt++)
        {
            float side = _rng.Randf();
            float t = _rng.RandfRange(-29.2f, 29.2f);
            if (side < 0.25f) point = new Vector3(t, 0, _rng.RandfRange(-29.4f, -25.5f));
            else if (side < 0.50f) point = new Vector3(t, 0, _rng.RandfRange(25.5f, 29.4f));
            else if (side < 0.75f) point = new Vector3(_rng.RandfRange(-29.4f, -25.5f), 0, t);
            else point = new Vector3(_rng.RandfRange(25.5f, 29.4f), 0, t);

            // Prison's eight perimeter spawns and PrisonStart retain a five metre buffer.
            Vector2 flat = new(point.X, point.Z);
            Vector2[] protectedPoints =
            {
                Vector2.Zero, new(0, -26), new(25, -25), new(-25, -25), new(-26, 0),
                new(26, 0), new(-25, 25), new(0, 26), new(25, 25)
            };
            bool clear = true;
            foreach (Vector2 spawn in protectedPoints)
            {
                if (flat.DistanceTo(spawn) < 5.0f) { clear = false; break; }
            }
            if (clear) return point;
        }
        return new Vector3(-28.0f, 0, 14.0f);
    }

    private void AddBrokenFurniture(Node3D ruin, float damage)
    {
        int count = Mathf.RoundToInt(DebrisDensity * damage * 9);
        if (count <= 0) return;
        var furniture = MakeMultiMesh(ruin, "BrokenFurniture", new BoxMesh { Size = Vector3.One }, _woodMaterial, count);
        for (int i = 0; i < count; i++)
        {
            Vector3 point = ChoosePerimeterPoint();
            Vector3 size = new(_rng.RandfRange(0.65f, 1.5f), _rng.RandfRange(0.08f, 0.16f), _rng.RandfRange(0.16f, 0.3f));
            furniture.Multimesh.SetInstanceTransform(i, TransformForBox(
                new Vector3(point.X, size.Y * 0.5f, point.Z), size, _rng.RandfRange(0, Mathf.Tau)));
        }
        _debrisCount += count;
    }

    private void AddCracksAndSoot(Node3D ruin, float damage)
    {
        // Procedural monochrome image lets Decal nodes stay self-contained and avoids external assets.
        Image image = Image.CreateEmpty(128, 128, false, Image.Format.Rgba8);
        Vector2 center = new(63.5f, 63.5f);
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            float radius = center.DistanceTo(new Vector2(x, y)) / 90.0f;
            float alpha = Mathf.Clamp(1.0f - radius, 0.0f, 1.0f) * 0.44f;
            image.SetPixel(x, y, new Color(0.05f, 0.045f, 0.04f, alpha));
        }
        var sootTexture = ImageTexture.CreateFromImage(image);
        var crackTexture = CreateCrackTexture();
        int decals = Mathf.RoundToInt(3 + damage * 8);
        for (int i = 0; i < decals; i++)
        {
            Vector3 p = ChoosePerimeterPoint();
            var decal = new Decal
            {
                Name = $"{(i % 2 == 0 ? "Soot" : "Stain")}Decal_{i}",
                TextureAlbedo = sootTexture,
                Modulate = i % 2 == 0
                    ? new Color(0.72f, 0.75f, 0.72f, _rng.RandfRange(0.45f, 0.85f))
                    : new Color(0.82f, 0.67f, 0.48f, _rng.RandfRange(0.35f, 0.72f)),
                Size = new Vector3(_rng.RandfRange(2.0f, 4.2f), 4.0f, 0.08f),
                Position = new Vector3(p.X, _rng.RandfRange(1.2f, 4.8f), p.Z),
                Rotation = WallDecalRotation(p)
            };
            ruin.AddChild(decal);

            var crack = new Decal
            {
                Name = $"CrackDecal_{i}",
                TextureAlbedo = crackTexture,
                Modulate = new Color(0.65f, 0.64f, 0.59f, _rng.RandfRange(0.65f, 0.95f)),
                Size = new Vector3(_rng.RandfRange(1.0f, 2.4f), _rng.RandfRange(1.5f, 3.0f), 0.09f),
                Position = new Vector3(p.X, _rng.RandfRange(0.9f, 6.4f), p.Z),
                Rotation = WallDecalRotation(p)
            };
            ruin.AddChild(crack);
            _decalCount += 2;
        }

        // Thin jagged marks read as cracks on the ground and add surface wear without touching floor material.
        int crackCount = Mathf.RoundToInt(damage * 8);
        if (crackCount == 0) return;
        var crackMesh = new BoxMesh { Size = new Vector3(1, 1, 1) };
        var cracks = MakeMultiMesh(ruin, "FloorCracks", crackMesh, _darkConcrete, Mathf.Max(1, crackCount * 4));
        int index = 0;
        for (int c = 0; c < crackCount; c++)
        {
            Vector3 origin = ChoosePerimeterPoint();
            float angle = _rng.RandfRange(0, Mathf.Tau);
            for (int s = 0; s < 4 && index < cracks.Multimesh.InstanceCount; s++, index++)
            {
                float length = _rng.RandfRange(0.45f, 1.4f);
                Vector3 size = new(length, 0.018f, 0.035f);
                Vector3 pos = new(origin.X + Mathf.Cos(angle) * s * 0.38f, 0.012f, origin.Z + Mathf.Sin(angle) * s * 0.38f);
                cracks.Multimesh.SetInstanceTransform(index, TransformForBox(pos, size, angle + _rng.RandfRange(-0.45f, 0.45f)));
            }
        }
    }

    private ImageTexture CreateCrackTexture()
    {
        const int resolution = 128;
        Vector2 center = new(63.5f, 63.5f);
        Vector2[] ends =
        {
            new(20, 16), new(108, 24), new(15, 62), new(111, 68),
            new(28, 114), new(95, 111), new(57, 8), new(67, 120)
        };
        var image = Image.CreateEmpty(resolution, resolution, false, Image.Format.Rgba8);
        for (int y = 0; y < resolution; y++)
        for (int x = 0; x < resolution; x++)
        {
            Vector2 pixel = new(x, y);
            float nearest = 1000.0f;
            for (int i = 0; i < ends.Length; i++)
                nearest = Mathf.Min(nearest, DistanceToSegment(pixel, center, ends[i]));
            float alpha = nearest < 0.9f ? 0.9f : (nearest < 1.7f ? 0.35f : 0.0f);
            image.SetPixel(x, y, new Color(0.06f, 0.055f, 0.05f, alpha));
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        Vector2 segment = end - start;
        float denominator = segment.LengthSquared();
        float t = denominator > 0.0001f ? Mathf.Clamp((point - start).Dot(segment) / denominator, 0, 1) : 0;
        return point.DistanceTo(start + segment * t);
    }

    private static Vector3 WallDecalRotation(Vector3 position)
    {
        if (Mathf.Abs(position.Z) >= Mathf.Abs(position.X))
            return new Vector3(0, position.Z > 0 ? Mathf.Pi : 0, 0);
        return new Vector3(0, position.X > 0 ? -Mathf.Pi * 0.5f : Mathf.Pi * 0.5f, 0);
    }

    private void AddDust(Node3D ruin, float damage)
    {
        var particles = new GpuParticles3D
        {
            Name = "SuspendedDust",
            Amount = 120,
            Lifetime = 10.0,
            Emitting = true,
            Position = new Vector3(0, 3.6f, 0),
            VisibilityAabb = new Aabb(new Vector3(-31, -4, -31), new Vector3(62, 12, 62)),
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(29, 3.2f, 29),
                Direction = new Vector3(0.2f, 0.45f, 0.0f),
                Spread = 180,
                InitialVelocityMin = 0.02f,
                InitialVelocityMax = 0.14f,
                Gravity = new Vector3(0, -0.015f, 0),
                ScaleMin = 0.025f,
                ScaleMax = 0.09f,
                Color = new Color(0.72f, 0.68f, 0.56f, 0.22f)
            },
            DrawPass1 = new SphereMesh { Radius = 0.06f, Height = 0.12f, RadialSegments = 6, Rings = 3 }
        };
        particles.Amount = Mathf.RoundToInt(50 + 110 * damage);
        ruin.AddChild(particles);
    }

    private void AddRuinLights(Node3D ruin, float damage)
    {
        int lights = Mathf.RoundToInt(2 + damage * 3);
        for (int i = 0; i < lights; i++)
        {
            var light = new OmniLight3D
            {
                Name = $"FailingLight_{i}",
                Position = new Vector3(_rng.RandfRange(-22, 22), 6.9f, _rng.RandfRange(-22, 22)),
                LightColor = new Color(0.64f, 0.57f, 0.41f),
                LightEnergy = i == 0 ? 0.0f : 0.42f,
                OmniRange = 8.0f,
                ShadowEnabled = false
            };
            ruin.AddChild(light);
        }
    }

    private void AddFogEnvironment(Node3D ruin, float damage)
    {
        Godot.Environment? current = GetParent()?.GetNodeOrNull<WorldEnvironment>("WorldEnvironment")?.Environment;
        // Prison already has its own fog Environment. Keep it as the single scene environment
        // rather than installing a second WorldEnvironment under this removable layer.
        if (current?.FogEnabled == true) return;
        var env = current != null ? (Godot.Environment)current.Duplicate(true) : new Godot.Environment();
        env.FogEnabled = true;
        env.FogLightColor = env.FogLightColor.Lerp(new Color(0.27f, 0.28f, 0.25f), 0.35f);
        env.FogDensity = Mathf.Max(env.FogDensity, 0.0012f + damage * 0.0013f);
        // The original scene environment is preserved; this additive environment supplies a
        // subtle ruin haze on maps that do not already have fog configured.
        var world = new WorldEnvironment { Name = "RuinAtmosphere", Environment = env };
        ruin.AddChild(world);
    }

    private MultiMeshInstance3D MakeMultiMesh(Node3D parent, string name, Mesh mesh, Material material, int count)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = Mathf.Max(1, count),
            VisibleInstanceCount = Mathf.Max(1, count)
        };
        var instance = new MultiMeshInstance3D { Name = name, Multimesh = mm, MaterialOverride = material };
        parent.AddChild(instance);
        return instance;
    }

    private static Transform3D TransformForBox(Vector3 position, Vector3 scale, float yaw)
    {
        Basis basis = Basis.FromEuler(new Vector3(0, yaw, 0)).Scaled(scale);
        return new Transform3D(basis, position);
    }
}
