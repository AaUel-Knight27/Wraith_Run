using Godot;

/// <summary>The things standing in the hub warehouse besides the weapon wall: the player's car in the
/// corner and the armory corner's sign. Visual only plus a box collider each, built at runtime so
/// the warehouse art stays untouched.</summary>
public static class HubProps
{
    private const string CarPath = "res://art/vehicles/Low_Poly_Car_-_Toyota_Land_Cruiser_70_GXL_2024.glb";

    public static void Build(Node3D world)
    {
        var root = new Node3D { Name = "HubProps" };
        world.AddChild(root);
        AddCar(root);
        AddArmorySign(root);
    }

    private static void AddCar(Node3D root)
    {
        var scene = ResourceLoader.Load<PackedScene>(CarPath);
        if (scene == null) { GD.PushWarning($"Hub: car model missing ({CarPath})."); return; }
        var car = scene.Instantiate<Node3D>();
        car.Name = "ParkedCar";
        root.AddChild(car);

        // The GLB's own units are not metres (it came out of a CAD-style export), so measure it
        // and scale it to a real 4.9 m long car instead of trusting a fixed factor.
        Aabb raw = CombinedBounds(car);
        if (raw.Size == Vector3.Zero) { GD.PushWarning("Hub: car model has no meshes."); return; }
        bool lengthOnX = raw.Size.X >= raw.Size.Z;
        float scale = 4.9f / Mathf.Max(raw.Size.X, raw.Size.Z);
        car.Scale = Vector3.One * scale;
        // Parked along the south wall: its long side runs along X, so the box collider below is tight.
        car.RotationDegrees = new Vector3(0.0f, lengthOnX ? 0.0f : 90.0f, 0.0f);

        // Sit it on the floor in the south-west corner.
        Aabb fitted = CombinedBounds(car);
        car.Position += new Vector3(-3.3f - fitted.GetCenter().X, -fitted.Position.Y, 4.3f - fitted.GetCenter().Z);

        Aabb bounds = CombinedBounds(car);
        var body = new StaticBody3D { Name = "CarCollision" };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = bounds.Size }, Position = bounds.GetCenter() });
        root.AddChild(body);
    }

    /// <summary>World-space bounds of every mesh under a node.</summary>
    public static Aabb CombinedBounds(Node3D node)
    {
        bool any = false;
        Aabb total = default;
        foreach (Node child in node.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (child is not MeshInstance3D mesh || mesh.Mesh == null) continue;
            Aabb local = mesh.GetAabb();
            Aabb world = mesh.GlobalTransform * local;
            total = any ? total.Merge(world) : world;
            any = true;
        }
        return any ? total : default;
    }

    private static void AddArmorySign(Node3D root)
    {
        var sign = new Label3D
        {
            Name = "ArmorySign",
            Text = "ARMORY",
            FontSize = 96,
            PixelSize = 0.006f,
            Modulate = new Color(1.0f, 0.25f, 0.2f),
            OutlineSize = 12,
            Position = new Vector3(0.0f, 3.6f, -5.55f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
            DoubleSided = false,
        };
        root.AddChild(sign);
    }
}
