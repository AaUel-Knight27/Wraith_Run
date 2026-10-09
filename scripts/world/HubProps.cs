using Godot;

/// <summary>The parked vehicles in the hub's motor-pool bays. Visual only plus a box collider each.</summary>
public static class HubProps
{
    private const string CarPath = "res://art/vehicles/Low_Poly_Car_-_Toyota_Land_Cruiser_70_GXL_2024.glb";
    private const string BuggyPath = "res://art/vehicles/raider_buggy.glb";

    public static void Build(Node3D world)
    {
        var root = new Node3D { Name = "HubProps" };
        world.AddChild(root);
        AddCar(root, CarPath, 4.9f, new Vector3(-9.4f, 0.0f, 9.4f), "ParkedCar");
        AddCar(root, BuggyPath, 3.6f, new Vector3(8.2f, 0.0f, 9.4f), "ParkedBuggy");
    }

    private static void AddCar(Node3D root, string path, float lengthMetres, Vector3 spot, string name)
    {
        var scene = ResourceLoader.Load<PackedScene>(path);
        if (scene == null) { GD.PushWarning($"Hub: car model missing ({path})."); return; }
        var car = scene.Instantiate<Node3D>();
        car.Name = name;
        root.AddChild(car);

        // Each GLB's own units are not metres (CAD-style exports), so measure it and scale to
        // lengthMetres instead of trusting a fixed factor per model.
        Aabb raw = CombinedBounds(car);
        if (raw.Size == Vector3.Zero) { GD.PushWarning("Hub: car model has no meshes."); return; }
        bool lengthOnX = raw.Size.X >= raw.Size.Z;
        float scale = lengthMetres / Mathf.Max(raw.Size.X, raw.Size.Z);
        car.Scale = Vector3.One * scale;
        // Parked along the south wall: its long side runs along X, so the box collider below is tight.
        car.RotationDegrees = new Vector3(0.0f, lengthOnX ? 0.0f : 90.0f, 0.0f);

        // Sit it on the floor in the south-west corner.
        Aabb fitted = CombinedBounds(car);
        car.Position += new Vector3(spot.X - fitted.GetCenter().X, -fitted.Position.Y, spot.Z - fitted.GetCenter().Z);

        Aabb bounds = CombinedBounds(car);
        var body = new StaticBody3D { Name = name + "Collision" };
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

}
