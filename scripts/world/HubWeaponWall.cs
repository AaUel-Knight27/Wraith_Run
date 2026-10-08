using System.Collections.Generic;
using Godot;

/// <summary>
/// The weapon wall: every weapon the match can equip, mounted on the north wall of the hub
/// warehouse. Look at one and press E to take it. The wall is the only loadout screen.
///
/// What you can take follows the carry rules in WeaponRules: two primaries, one pistol, one knife,
/// and the heavy weapon (Bazooka) only for a Demolisher. Taking a weapon writes it into the active
/// weapon set (PlayerProfile), so the match starts with exactly what you took.
/// </summary>
public partial class HubWeaponWall : Node3D
{
    private const uint SlotLayer = 1u << 19;
    private const float ReachMetres = 3.2f;
    private const float WallZ = -5.45f;
    private const float SlotSpacing = 1.15f;

    private readonly List<(string Name, Node3D Marker)> _slots = new();
    private Label? _prompt;
    private CanvasLayer? _ui;
    private Control? _picker;
    private string? _lookingAt;
    private HubController _hub = null!;

    public static void Build(Node3D world)
    {
        var wall = new HubWeaponWall { Name = "HubWeaponWall" };
        world.AddChild(wall);
    }

    public override void _Ready()
    {
        _hub = HubController.Instance!;
        BuildBoard();
        BuildUi();
    }

    // ---- the physical wall ---------------------------------------------------------------------

    /// <summary>Weapons this player has already taken. Hidden on this device only, so the same wall
    /// stays full for everyone else until they take those weapons too. Kept for the session, so a
    /// taken weapon does not come back on the wall while the game is running.</summary>
    private static readonly HashSet<string> TakenHere = new();

    /// <summary>The north rack has five labels, so it is spaced wider than the side racks.</summary>
    private const float NorthSpacing = 1.45f;
    private const float SideSpacing = 1.2f;
    private const float WestRackX = -5.85f;
    private const float EastRackX = 5.95f;

    /// <summary>The three racks: the north wall (primaries and the shotgun), a west rack (pistols) and an
    /// east rack (the coming-soon weapons, greyed out until their animations are done).</summary>
    private static readonly (string[] Names, Vector3 Origin, float YawDegrees, float Spacing)[] Racks =
    {
        (new[] { "AK-47", "M4", "P90", "Kriss Vector", "Shotgun", "Sniper" }, new Vector3(0.0f, 0.0f, WallZ), 0.0f, NorthSpacing),
        (new[] { "Glock 19", "Desert Eagle", "FN 502" }, new Vector3(WestRackX, 0.0f, 0.0f), 90.0f, SideSpacing),
        (new[] { "Karambit", "Bazooka" }, new Vector3(EastRackX, 0.0f, 0.0f), -90.0f, SideSpacing),
    };

    private void BuildBoard()
    {
        foreach (var rack in Racks)
        {
            var names = new List<string>();
            foreach (string name in rack.Names)
                if (System.Array.IndexOf(WeaponSwitcher.Loadout, name) >= 0) names.Add(name);
            if (names.Count == 0) continue;
            names.Sort((a, b) => Order(a).CompareTo(Order(b)));

            // Each rack is a root with its board behind the slots. Slots face +Z locally, so the rotation
            // turns the side racks to face into the room.
            var root = new Node3D { Name = "Rack", Position = rack.Origin, RotationDegrees = new Vector3(0.0f, rack.YawDegrees, 0.0f) };
            AddChild(root);

            float spacing = rack.Spacing;
            float width = names.Count * spacing + 0.8f;
            var boardSize = new Vector3(width, 2.4f, 0.12f);
            var boardPos = new Vector3(0.0f, 1.55f, -0.1f);
            root.AddChild(new MeshInstance3D
            {
                Name = "Backboard",
                Mesh = new BoxMesh { Size = boardSize },
                Position = boardPos,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.075f, 0.08f), Roughness = 0.9f },
            });
            // Solid: nobody walks through a rack.
            var rackBody = new StaticBody3D { Name = "RackCollision" };
            rackBody.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = boardSize }, Position = boardPos });
            root.AddChild(rackBody);

            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                float x = (i - (names.Count - 1) * 0.5f) * spacing;
                var marker = new Node3D { Name = $"Slot_{i}", Position = new Vector3(x, 1.55f, 0.05f) };
                root.AddChild(marker);

                AddModel(marker, name);

                bool available = WeaponRules.IsAvailable(name);
                var label = new Label3D
                {
                    Text = available ? name.ToUpperInvariant() : name.ToUpperInvariant() + " - SOON",
                    FontSize = 48, PixelSize = 0.0042f, OutlineSize = 8,
                    Modulate = available ? new Color(0.95f, 0.93f, 0.93f) : new Color(0.42f, 0.42f, 0.42f),
                    Position = new Vector3(0.0f, -0.62f, 0.02f),
                };
                marker.AddChild(label);

                var area = new Area3D { Name = "Pick", CollisionLayer = SlotLayer, CollisionMask = 0, Monitorable = true, Monitoring = false };
                area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(spacing * 0.9f, 1.3f, 0.6f) }, Position = new Vector3(0, -0.1f, 0.15f) });
                marker.AddChild(area);
                marker.SetMeta("weapon", name);
                _slots.Add((name, marker));
                if (TakenHere.Contains(name)) marker.Visible = false;
            }
        }
    }

    private static int Order(string weaponName)
    {
        WeaponData? data = OperatorInfo.FindWeapon(weaponName);
        WeaponCategory? category = data == null ? null : WeaponRules.CategoryOf(data);
        return category == null ? 99 : (int)category.Value;
    }

    /// <summary>Shows the weapon's own model, uniformly scaled so its longest side fits the slot and
    /// turned so that side runs along the wall. Models are authored at different scales and facings,
    /// so this measures instead of trusting WeaponData.ModelScale.</summary>
    private static void AddModel(Node3D marker, string weaponName)
    {
        WeaponData? data = OperatorInfo.FindWeapon(weaponName);
        if (data == null || string.IsNullOrEmpty(data.ModelScenePath)) return;
        var scene = ResourceLoader.Load<PackedScene>(data.ModelScenePath);
        if (scene == null) return;
        var holder = new Node3D { Name = "Display" };
        marker.AddChild(holder);
        var model = scene.Instantiate<Node3D>();
        holder.AddChild(model);

        Aabb box = HubProps.CombinedBounds(model);
        if (box.Size == Vector3.Zero) return;
        Vector3 size = box.Size;
        float longest = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
        bool melee = data.FireMode == WeaponData.FireModeType.Melee;
        float target = melee ? 0.34f : (data.WeaponClass.ToLowerInvariant().Contains("pistol") ? 0.30f : 0.95f);
        holder.Scale = Vector3.One * (target / longest);
        if (longest == size.Y) holder.RotationDegrees = new Vector3(0, 0, 90);
        else if (longest == size.Z) holder.RotationDegrees = new Vector3(0, 90, 0);
        // centre the model on the slot after scaling/turning
        Aabb fitted = HubProps.CombinedBounds(holder);
        holder.Position -= fitted.GetCenter() - marker.GlobalPosition;
    }

    // ---- looking and taking ----------------------------------------------------------------------

    private void BuildUi()
    {
        _ui = new CanvasLayer { Layer = 25 };
        AddChild(_ui);
        _prompt = MenuStyle.Text("", 24, MenuStyle.TextMain, MenuStyle.Bold, HorizontalAlignment.Center);
        MenuStyle.Place(_prompt, 0.5f, 0.62f, 0.5f, 0.62f, -320, 0, 320, 40);
        _ui.AddChild(_prompt);
    }

    private Camera3D? LocalCamera() =>
        GetTree().CurrentScene?.GetNodeOrNull<Camera3D>($"Players/Player_{Multiplayer.GetUniqueId()}/Head/CameraShake/FirstPersonCamera");

    public override void _Process(double delta)
    {
        if (_prompt == null) return;
        _lookingAt = null;
        Camera3D? camera = LocalCamera();
        if (camera != null && _picker == null && !GameUiState.MenuOpen)
        {
            Vector3 from = camera.GlobalPosition;
            Vector3 to = from - camera.GlobalTransform.Basis.Z * ReachMetres;
            var query = PhysicsRayQueryParameters3D.Create(from, to, SlotLayer);
            query.CollideWithAreas = true;
            query.CollideWithBodies = false;
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
            if (hit.Count > 0 && hit["collider"].AsGodotObject() is Node hitNode && hitNode.GetParent() is Node3D slot && slot.HasMeta("weapon"))
                _lookingAt = slot.GetMeta("weapon").AsString();
        }
        _prompt.Text = _lookingAt == null ? string.Empty : $"E  -  {_lookingAt.ToUpperInvariant()}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_picker != null)
        {
            if (@event.IsActionPressed("ui_cancel") || @event.IsActionPressed(GameUiState.PauseAction))
            {
                GetViewport().SetInputAsHandled();
                ClosePicker();
            }
            return;
        }
        if (_lookingAt == null) return;
        if (@event.IsActionPressed("interact") && !@event.IsEcho())
        {
            GetViewport().SetInputAsHandled();
            OpenPicker(_lookingAt);
        }
    }

    // ---- the pick panel ---------------------------------------------------------------------------

    private void OpenPicker(string weaponName)
    {
        WeaponData? data = OperatorInfo.FindWeapon(weaponName);
        if (data == null || _ui == null) return;
        if (!WeaponRules.IsAvailable(weaponName)) return; // greyed out until its animations are finished
        GameUiState.SetMenuOpen(true);
        if (!OS.HasFeature("mobile")) Input.MouseMode = Input.MouseModeEnum.Visible;

        _picker = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        MenuStyle.Fill(_picker);
        _ui.AddChild(_picker);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(dim);
        _picker.AddChild(dim);

        var panel = MenuStyle.Panel(weaponName.ToUpperInvariant(), "rifle", out VBoxContainer body);
        MenuStyle.Place(panel, 0.5f, 0.5f, 0.5f, 0.5f, -300, -230, 300, 230);
        _picker.AddChild(panel);
        BuildPickerBody(body, data);
    }

    private void BuildPickerBody(VBoxContainer body, WeaponData data)
    {
        foreach (Node child in body.GetChildren()) { body.RemoveChild(child); child.QueueFree(); }

        string stats = data.FireMode == WeaponData.FireModeType.Melee
            ? $"{data.BodyDamage:0} dmg  |  melee"
            : $"{data.BodyDamage:0} dmg  |  {data.FireRateRPM:0} rpm  |  {data.MagazineSize} rd";
        body.AddChild(MenuStyle.Text(stats, 18, MenuStyle.TextDim, MenuStyle.Medium));

        WeaponCategory? category = WeaponRules.CategoryOf(data);
        bool demolisher = WeaponRules.IsDemolisher(PlayerProfile.OperatorId);
        int set = PlayerProfile.ActiveWeaponSet;
        bool offered = false;

        foreach (WeaponSlot slot in System.Enum.GetValues<WeaponSlot>())
        {
            if (!WeaponRules.Fits(data, slot)) continue;
            if (!WeaponRules.SlotAvailable(slot, demolisher))
            {
                body.AddChild(MenuStyle.Text("Only a DEMOLISHER can carry this.", 18, MenuStyle.Accent, MenuStyle.SemiBold));
                offered = true;
                continue;
            }
            offered = true;
            string holding = PlayerProfile.GetSetWeapon(set, slot);
            bool already = holding == data.WeaponName;
            WeaponSlot chosen = slot;
            string label = already ? $"{WeaponRules.SlotName(slot)}  -  carrying" : $"TAKE AS {WeaponRules.SlotName(slot)}   (replaces {holding})";
            Button take = MenuStyle.Outline(label, () => Take(data, chosen, body), null, 52);
            take.Disabled = already;
            body.AddChild(take);
        }
        if (!offered) body.AddChild(MenuStyle.Text("This weapon cannot be carried yet.", 18, MenuStyle.TextMuted, MenuStyle.Medium));
        _ = category;

        body.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
        body.AddChild(MenuStyle.Text("YOUR LOADOUT", 14, MenuStyle.TextMuted, MenuStyle.Bold));
        foreach (WeaponSlot slot in System.Enum.GetValues<WeaponSlot>())
        {
            if (!WeaponRules.SlotAvailable(slot, demolisher)) continue;
            body.AddChild(MenuStyle.Text($"{WeaponRules.SlotName(slot)}:  {PlayerProfile.GetSetWeapon(set, slot)}", 17, MenuStyle.TextMain, MenuStyle.Medium));
        }
        body.AddChild(MenuStyle.Outline("CLOSE", ClosePicker, null, 46));
    }

    /// <summary>Put a weapon into one carry slot of the active set. A primary that is already in the
    /// other primary slot swaps places with the one being replaced, so a set never holds the same gun twice.</summary>
    private void Take(WeaponData data, WeaponSlot slot, VBoxContainer body)
    {
        int set = PlayerProfile.ActiveWeaponSet;
        string name = data.WeaponName;
        if (WeaponRules.IsPrimary(slot))
        {
            WeaponSlot other = slot == WeaponSlot.Primary1 ? WeaponSlot.Primary2 : WeaponSlot.Primary1;
            // Already carried in the other primary slot: the two guns trade places.
            if (PlayerProfile.GetSetWeapon(set, other) == name) PlayerProfile.SwapPrimaries(set);
            else if (!PlayerProfile.SetSetWeapon(set, slot, name)) GD.PushWarning($"Weapon wall: {name} was refused for {slot}.");
        }
        else if (!PlayerProfile.SetSetWeapon(set, slot, name)) GD.PushWarning($"Weapon wall: {name} was refused for {slot}.");

        // This player has it now: it leaves this player's wall, and stays on everyone else's.
        TakenHere.Add(name);
        foreach ((string slotName, Node3D marker) in _slots)
            if (slotName == name) marker.Visible = false;

        // hold it right away
        Node? player = GetTree().CurrentScene?.GetNodeOrNull($"Players/Player_{Multiplayer.GetUniqueId()}/WeaponSwitcher");
        if (player is WeaponSwitcher switcher)
        {
            int index = System.Array.IndexOf(WeaponSwitcher.Loadout, name);
            if (index >= 0) switcher.TryEquipFromHub(index);
        }
        BuildPickerBody(body, data);
    }

    private void ClosePicker()
    {
        _picker?.QueueFree();
        _picker = null;
        GameUiState.SetMenuOpen(false);
        if (!OS.HasFeature("mobile")) Input.MouseMode = Input.MouseModeEnum.Captured;
    }
}
