using System;
using Godot;

/// <summary>Shared scaffolding for the lobby sub-screens: a left list/controls panel, a 3D stage in
/// the middle, a details panel on the right. (The weapon screens live in WeaponScreens.cs.)</summary>
internal static class LoadoutLayout
{
    public static HBoxContainer Root(Control host)
    {
        var layout = new HBoxContainer();
        layout.AddThemeConstantOverride("separation", 20);
        MenuStyle.Place(layout, 0, 0, 1, 1, 64, 124, -64, -60);
        host.AddChild(layout);
        return layout;
    }

    /// <summary>Same area as <see cref="Root"/>, but with a row of tabs across the top and the three
    /// columns underneath it.</summary>
    public static HBoxContainer RootWithTabs(Control host, out HBoxContainer tabs)
    {
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 10);
        MenuStyle.Place(outer, 0, 0, 1, 1, 64, 124, -64, -60);
        host.AddChild(outer);

        tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 6);
        outer.AddChild(tabs);

        var columns = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 20);
        outer.AddChild(columns);
        return columns;
    }

    /// <summary>Framed 3D stage with rotate buttons underneath, sized to take the leftover width.</summary>
    public static OperatorStage StageColumn(HBoxContainer layout)
    {
        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 8);
        layout.AddChild(column);

        var frame = MenuStyle.Frame();
        frame.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        column.AddChild(frame);

        var stage = new OperatorStage
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        frame.AddChild(stage);

        var controls = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        controls.AddThemeConstantOverride("separation", 14);
        controls.AddChild(MenuStyle.IconButton("chevron-left", 22, MenuStyle.TextMain, () => stage.Nudge(-35.0f)));
        controls.AddChild(MenuStyle.IconButton("chevron-right", 22, MenuStyle.TextMain, () => stage.Nudge(35.0f)));
        column.AddChild(controls);
        return stage;
    }

    /// <summary>Scrolling column filling a panel body; the caller adds rows to the returned box.</summary>
    public static VBoxContainer ScrollColumn(VBoxContainer body, int separation)
    {
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        body.AddChild(scroll);
        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", separation);
        scroll.AddChild(column);
        return column;
    }

    public static void Clear(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    public static Label SectionLabel(string text, Color color) => MenuStyle.Text(text, 15, color, MenuStyle.Bold);
}

// ==============================================================================================
// CHOOSE OPERATOR
// ==============================================================================================

/// <summary>Every operator on the roster, grouped by class, with the selected one standing in the
/// middle and its full kit on the right - including kit lines no system reads yet (tagged PLANNED).</summary>
public sealed class OperatorScreen
{
    private string _previewId;
    private readonly Action<string> _openCustomize;

    /// <param name="previewId">Operator to have selected when the screen opens (null = the equipped one).</param>
    /// <param name="openCustomize">Opens the colour screen for the given operator id.</param>
    public OperatorScreen(string? previewId, Action<string> openCustomize)
    {
        _previewId = previewId ?? PlayerProfile.OperatorId;
        _openCustomize = openCustomize;
    }

    private int _filter = -1;
    private HBoxContainer _tabs = null!;
    private VBoxContainer _list = null!;
    private VBoxContainer _details = null!;
    private VBoxContainer _equipHost = null!;
    private OperatorStage _stage = null!;
    private Button? _previewRow;

    public void Build(Control host)
    {
        HBoxContainer layout = LoadoutLayout.Root(host);

        PanelContainer listPanel = MenuStyle.Panel("OPERATORS", "users", out VBoxContainer listBody);
        listPanel.CustomMinimumSize = new Vector2(330, 0);
        layout.AddChild(listPanel);
        _tabs = new HBoxContainer();
        _tabs.AddThemeConstantOverride("separation", 4);
        listBody.AddChild(_tabs);
        _list = LoadoutLayout.ScrollColumn(listBody, 6);

        _stage = LoadoutLayout.StageColumn(layout);

        PanelContainer detailPanel = MenuStyle.Panel("KIT", "helmet-gear", out VBoxContainer detailBody);
        detailPanel.CustomMinimumSize = new Vector2(350, 0);
        layout.AddChild(detailPanel);
        _details = LoadoutLayout.ScrollColumn(detailBody, 8);
        _equipHost = new VBoxContainer();
        detailBody.AddChild(_equipHost);

        RebuildTabs();
        RebuildList();
        ShowPreview();
        _previewRow?.CallDeferred(Control.MethodName.GrabFocus);
    }

    private CharacterData? PreviewData => OperatorInfo.Find(_previewId);

    private void SetFilter(int filter)
    {
        _filter = filter;
        RebuildTabs();
        RebuildList();
    }

    private void Preview(string id)
    {
        _previewId = id;
        RebuildList();
        ShowPreview();
    }

    private void Equip()
    {
        PlayerProfile.OperatorId = _previewId;
        RebuildList();
        RebuildDetails();
    }

    private void ShowPreview()
    {
        _stage.ShowOperator(PreviewData, OperatorInfo.FindWeapon(PlayerProfile.StartingWeaponName));
        RebuildDetails();
    }

    private void RebuildTabs()
    {
        LoadoutLayout.Clear(_tabs);
        _tabs.AddChild(MenuStyle.Tab(null, "ALL", _filter == -1, "All operators", () => SetFilter(-1)));
        foreach (CharacterData.CharacterClass cls in Enum.GetValues<CharacterData.CharacterClass>())
        {
            int value = (int)cls;
            _tabs.AddChild(MenuStyle.Tab(OperatorInfo.ClassIcon(cls), null, _filter == value,
                OperatorInfo.ClassName(cls), () => SetFilter(value)));
        }
    }

    private void RebuildList()
    {
        LoadoutLayout.Clear(_list);
        _previewRow = null;

        if (_filter == -1) AddRow(null);

        CharacterData.CharacterClass? lastClass = null;
        foreach (CharacterData op in OperatorInfo.Operators)
        {
            if (_filter != -1 && (int)op.Class != _filter) continue;
            if (_filter == -1 && lastClass != op.Class)
            {
                lastClass = op.Class;
                var header = new HBoxContainer();
                header.AddThemeConstantOverride("separation", 8);
                header.AddChild(MenuStyle.Icon(OperatorInfo.ClassIcon(op.Class), 16, 0, MenuStyle.TextMuted));
                header.AddChild(MenuStyle.Text(OperatorInfo.ClassName(op.Class), 14, MenuStyle.TextMuted, MenuStyle.Bold));
                var pad = new MarginContainer();
                pad.AddThemeConstantOverride("margin_top", 6);
                pad.AddChild(header);
                _list.AddChild(pad);
            }
            AddRow(op);
        }
    }

    private void AddRow(CharacterData? op)
    {
        string id = op?.CharacterId ?? string.Empty;
        bool previewed = id == _previewId;
        OperatorInfo.ResolveColors(op, out Color jacket, out Color pants, out Color accent);
        Button row = MenuStyle.ListRow(
            op == null ? "user" : OperatorInfo.ClassIcon(op.Class), 30, 30,
            OperatorInfo.Callsign(op),
            op == null ? "No perks, no drawbacks" : op.SkillName,
            previewed, () => Preview(id),
            new[] { jacket, pants, accent },
            id == PlayerProfile.OperatorId ? "EQUIPPED" : null, MenuStyle.Accent);
        _list.AddChild(row);
        if (previewed) _previewRow = row;
    }

    private void RebuildDetails()
    {
        LoadoutLayout.Clear(_details);
        LoadoutLayout.Clear(_equipHost);
        CharacterData? op = PreviewData;

        var classRow = new HBoxContainer();
        classRow.AddThemeConstantOverride("separation", 8);
        classRow.AddChild(MenuStyle.Icon(op == null ? "user" : OperatorInfo.ClassIcon(op.Class), 22, 0, MenuStyle.Accent));
        classRow.AddChild(MenuStyle.Text(op == null ? "NO CLASS" : OperatorInfo.ClassName(op.Class), 17, MenuStyle.Accent, MenuStyle.Bold));
        _details.AddChild(classRow);
        _details.AddChild(MenuStyle.Text(OperatorInfo.Callsign(op), 40, MenuStyle.TextMain, MenuStyle.Bold));
        _details.AddChild(MenuStyle.Text(OperatorInfo.RealName(op), 17, MenuStyle.TextDim));
        _details.AddChild(MenuStyle.Wrapped(
            op == null ? "Plain kit. Plays exactly like the game did before operators." : OperatorInfo.ClassBlurb(op.Class),
            16, MenuStyle.TextMuted));
        if (op != null && op.Class == CharacterData.CharacterClass.Demolisher)
        {
            var heavy = new HBoxContainer();
            heavy.AddChild(MenuStyle.Chip("HEAVY WEAPON SLOT", MenuStyle.Accent, 12));
            _details.AddChild(heavy);
        }
        _details.AddChild(MenuStyle.Divider());

        if (op != null)
        {
            _details.AddChild(LoadoutLayout.SectionLabel("SKILL", MenuStyle.TextMuted));
            _details.AddChild(MenuStyle.Text(op.SkillName, 24, MenuStyle.TextMain, MenuStyle.SemiBold));
            AddKit("BUFFS", op.Buffs, "chevron-up", new Color(0.38f, 0.77f, 0.43f));
            AddKit("DRAWBACKS", op.Drawbacks, "chevron-down", MenuStyle.Accent);
        }

        // EQUIP and CUSTOMIZE side by side.
        bool equipped = _previewId == PlayerProfile.OperatorId;
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 10);
        Button equip = MenuStyle.Primary(equipped ? "EQUIPPED" : "EQUIP", new Vector2(0, 58), Equip, UiSounds.Sound.Select);
        equip.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        equip.Disabled = equipped;
        buttons.AddChild(equip);
        string id = _previewId;
        Button customize = MenuStyle.Outline("CUSTOMIZE", () => _openCustomize(id), "helmet-gear", 58);
        customize.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        buttons.AddChild(customize);
        _equipHost.AddChild(buttons);
    }

    private void AddKit(string header, StatModifier[] mods, string icon, Color tint)
    {
        if (mods == null || mods.Length == 0) return;
        var label = LoadoutLayout.SectionLabel(header, tint);
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_top", 8);
        pad.AddChild(label);
        _details.AddChild(pad);

        foreach (StatModifier mod in mods)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var glyph = MenuStyle.Icon(icon, 16, 0, tint);
            glyph.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
            row.AddChild(glyph);
            string text = string.IsNullOrWhiteSpace(mod.Note) ? mod.EffectId : mod.Note;
            row.AddChild(MenuStyle.Wrapped(text, 16, MenuStyle.TextMain));
            bool live = OperatorInfo.IsWired(mod.EffectId);
            row.AddChild(MenuStyle.Chip(live ? "IN GAME" : "PLANNED", live ? MenuStyle.TextMain : MenuStyle.TextMuted, 11));
            _details.AddChild(row);
        }
    }
}

// ==============================================================================================
// CUSTOMIZE PLAYER
// ==============================================================================================

/// <summary>Pick jacket / pants / boots-and-gloves colours for the current operator and watch them
/// change on the model. Colours are saved per operator.</summary>
public sealed class CustomizeScreen
{
    private static readonly uint[] Presets =
    {
        0x1b1b1d, 0x3a3d35, 0x5a5f44, 0x7a6a4f, 0x8c7b5c, 0x4a3a2e, 0x6d6e72,
        0x6b1a17, 0xa3231d, 0xc8582a, 0xb59a3c, 0x2f4a4a, 0x3d4f66, 0xd8d3c8,
    };

    private readonly string _operatorId;
    private Color _jacket, _pants, _accent;

    public CustomizeScreen(string operatorId) => _operatorId = operatorId ?? string.Empty;

    private OperatorStage _stage = null!;
    private VBoxContainer _groups = null!;

    public void Build(Control host)
    {
        CharacterData? op = OperatorInfo.Find(_operatorId);
        OperatorInfo.ResolveColors(op, out _jacket, out _pants, out _accent);

        HBoxContainer layout = LoadoutLayout.Root(host);

        PanelContainer colorPanel = MenuStyle.Panel("COLORS", "helmet-gear", out VBoxContainer colorBody);
        colorPanel.CustomMinimumSize = new Vector2(360, 0);
        layout.AddChild(colorPanel);
        _groups = LoadoutLayout.ScrollColumn(colorBody, 10);

        _stage = LoadoutLayout.StageColumn(layout);

        PanelContainer infoPanel = MenuStyle.Panel("OPERATOR", "users", out VBoxContainer infoBody);
        infoPanel.CustomMinimumSize = new Vector2(300, 0);
        layout.AddChild(infoPanel);
        var info = LoadoutLayout.ScrollColumn(infoBody, 8);
        info.AddChild(MenuStyle.Text(op == null ? "NO CLASS" : OperatorInfo.ClassName(op.Class), 17, MenuStyle.Accent, MenuStyle.Bold));
        info.AddChild(MenuStyle.Text(OperatorInfo.Callsign(op), 36, MenuStyle.TextMain, MenuStyle.Bold));
        info.AddChild(MenuStyle.Text(OperatorInfo.RealName(op), 17, MenuStyle.TextDim));
        infoBody.AddChild(MenuStyle.Outline("RESET COLORS", Reset));

        RebuildGroups();
        _stage.ShowOperator(op, OperatorInfo.FindWeapon(PlayerProfile.StartingWeaponName));
    }

    private void Reset()
    {
        PlayerProfile.ClearColors(_operatorId);
        OperatorInfo.ResolveColors(OperatorInfo.Find(_operatorId), out _jacket, out _pants, out _accent);
        _stage.Recolor(_jacket, _pants, _accent);
        RebuildGroups();
    }

    private void Set(int region, Color color)
    {
        if (region == 0) _jacket = color;
        else if (region == 1) _pants = color;
        else _accent = color;
        PlayerProfile.SetColors(_operatorId, _jacket, _pants, _accent);
        _stage.Recolor(_jacket, _pants, _accent);
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        LoadoutLayout.Clear(_groups);
        AddGroup("JACKET", 0, _jacket);
        AddGroup("PANTS", 1, _pants);
        AddGroup("BOOTS + GLOVES", 2, _accent);
    }

    private void AddGroup(string title, int region, Color current)
    {
        _groups.AddChild(LoadoutLayout.SectionLabel(title, MenuStyle.TextMuted));
        var grid = new GridContainer { Columns = 7 };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 8);
        foreach (uint rgb in Presets)
        {
            var color = new Color(((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f);
            Color captured = color;
            grid.AddChild(MenuStyle.Swatch(color, color.IsEqualApprox(current), () => Set(region, captured)));
        }
        _groups.AddChild(grid);
    }
}
