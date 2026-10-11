using System;
using System.Collections.Generic;
using Godot;

// ==============================================================================================
// The weapon part of the lobby. Three screens, one step each:
//
//   WEAPON SETS    three cards (SHORT / MID / LONG RANGE by default). Picking one makes it the active
//                  set and opens it.
//   WEAPON SET     the set's name (editable) and its weapons: two primaries and a pistol. Pick a row, see the
//                  weapon, change it.
//   CHOOSE WEAPON  category tabs across the top, the weapons of that category in a list, the model
//                  turning in the middle, the description on the right. A slot only offers its own
//                  kind of weapon, so a set can be changed but never grown or shrunk.
//
// What is allowed lives in WeaponRules, what is saved lives in PlayerProfile; these classes only draw it.
// ==============================================================================================

/// <summary>The right-hand description panel, shared by the set screen and the picker so a weapon
/// reads the same everywhere: category, name, fire mode, a short description, then the numbers.</summary>
internal static class WeaponDescription
{
    public static void Fill(VBoxContainer target, WeaponData weapon)
    {
        WeaponCategory? category = WeaponRules.CategoryOf(weapon);
        string categoryText = category == null
            ? weapon.WeaponClass.ToUpperInvariant()
            : WeaponRules.CategoryName(category.Value);
        target.AddChild(MenuStyle.Text(categoryText, 17, MenuStyle.Accent, MenuStyle.Bold));

        var name = MenuStyle.Text(weapon.WeaponName.ToUpperInvariant(), 36, MenuStyle.TextMain, MenuStyle.Bold);
        name.ClipText = true;
        target.AddChild(name);

        var chips = new HBoxContainer();
        chips.AddThemeConstantOverride("separation", 6);
        chips.AddChild(MenuStyle.Chip(OperatorInfo.FireModeName(weapon), MenuStyle.TextDim, 12));
        target.AddChild(chips);

        if (!string.IsNullOrEmpty(weapon.Description))
            target.AddChild(MenuStyle.Wrapped(weapon.Description, 16, MenuStyle.TextMuted));
        target.AddChild(MenuStyle.Divider());

        int pellets = Math.Max(1, weapon.PelletsPerShot);
        string damage = pellets > 1 ? $"{weapon.BodyDamage:0} x{pellets}" : $"{weapon.BodyDamage:0}";
        target.AddChild(MenuStyle.StatBar("DAMAGE", damage, weapon.BodyDamage * pellets / 100.0f));
        target.AddChild(MenuStyle.StatBar("HEADSHOT",
            weapon.HeadshotDamage >= 100.0f ? "KILL" : $"{weapon.HeadshotDamage:0}",
            weapon.HeadshotDamage / 100.0f));
        target.AddChild(MenuStyle.StatBar("FIRE RATE", $"{weapon.FireRateRPM:0}",
            weapon.FireRateRPM / 1100.0f));

        target.AddChild(MenuStyle.StatBar("MAGAZINE", $"{weapon.MagazineSize} + {weapon.MaxReserveAmmo}",
            weapon.MagazineSize / 50.0f));
        target.AddChild(MenuStyle.StatBar("RELOAD",
            weapon.ShellByShellReload ? $"{weapon.ReloadTime:0.0}s / shell" : $"{weapon.ReloadTime:0.0}s",
            weapon.ReloadTime / 3.5f));
        target.AddChild(MenuStyle.StatBar("RECOIL", $"{weapon.RecoilVertical:0.0}", weapon.RecoilVertical / 8.0f));
    }
}

// ==============================================================================================
// WEAPON SETS
// ==============================================================================================

/// <summary>The three sets side by side, each listing the weapons inside it.</summary>
public sealed class WeaponSetsScreen
{
    private readonly Action<int> _openSet;

    /// <param name="openSet">Called with the set's index once it has been made the active set.</param>
    public WeaponSetsScreen(Action<int> openSet) => _openSet = openSet;

    public void Build(Control host)
    {
        HBoxContainer layout = LoadoutLayout.Root(host);
        int active = PlayerProfile.ActiveWeaponSet;
        Button? focus = null;

        for (int set = 0; set < WeaponRules.SetCount; set++)
        {
            int captured = set;
            bool isActive = set == active;
            Button card = MenuStyle.SetCard(isActive, () => Open(captured), out VBoxContainer body);

            var top = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            var number = MenuStyle.Text($"SET {set + 1}", 15, MenuStyle.Accent, MenuStyle.Bold);
            number.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            top.AddChild(number);
            if (isActive) top.AddChild(MenuStyle.Chip("ACTIVE", MenuStyle.TextMain, 12));
            body.AddChild(top);

            var name = MenuStyle.Text(PlayerProfile.GetSetName(set).ToUpperInvariant(), 30, MenuStyle.TextMain, MenuStyle.Bold);
            name.ClipText = true;
            body.AddChild(name);
            body.AddChild(MenuStyle.Divider());

            for (int slot = 0; slot < WeaponRules.SlotCount; slot++)
                body.AddChild(SlotLine(set, (WeaponSlot)slot));

            layout.AddChild(card);
            if (isActive) focus = card;
        }
        focus?.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void Open(int set)
    {
        PlayerProfile.ActiveWeaponSet = set;
        _openSet(set);
    }

    /// <summary>One weapon inside a card: icon, slot name, weapon name.</summary>
    private static Control SlotLine(int set, WeaponSlot slot)
    {
        WeaponData? weapon = OperatorInfo.FindWeapon(PlayerProfile.GetSetWeapon(set, slot));

        var row = new HBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0, 50),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", 10);

        var glyph = MenuStyle.Icon(weapon == null ? "rifle" : OperatorInfo.WeaponIcon(weapon), 46, 20, MenuStyle.Accent);
        glyph.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(glyph);

        var texts = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        texts.AddThemeConstantOverride("separation", -3);
        texts.AddChild(MenuStyle.Text(WeaponRules.SlotName(slot), 13, MenuStyle.TextMuted, MenuStyle.SemiBold));
        var weaponName = MenuStyle.Text((weapon?.WeaponName ?? "NONE").ToUpperInvariant(), 21,
            MenuStyle.TextMain, MenuStyle.Bold);
        weaponName.ClipText = true;
        texts.AddChild(weaponName);
        row.AddChild(texts);

        return row;
    }
}

// ==============================================================================================
// WEAPON SET (one set: name + its weapons)
// ==============================================================================================

/// <summary>One set open for editing: its name at the top of the list, its weapons underneath, the
/// operator on the stage holding whichever one is selected, and that weapon's description on the
/// right. The name and the weapons are saved as they change, so renaming and re-arming a set
/// happen together on this one screen.</summary>
public sealed class WeaponSetScreen
{
    private readonly int _set;
    private readonly Action<WeaponSlot> _openPicker;
    private readonly Action _finish;

    private LineEdit _name = null!;
    private VBoxContainer _list = null!;
    private VBoxContainer _details = null!;
    private VBoxContainer _actions = null!;
    private OperatorStage _stage = null!;
    private Button? _selectedRow;

    /// <summary>The row currently highlighted, so the picker can hand the player back to the same one.</summary>
    public WeaponSlot SelectedSlot { get; private set; }

    /// <param name="set">Which set (0-2).</param>
    /// <param name="selected">Row to highlight when the screen opens (null = Primary 1).</param>
    /// <param name="openPicker">Opens the weapon picker for a slot.</param>
    /// <param name="finish">CONTINUE: leave the weapon screens.</param>
    public WeaponSetScreen(int set, WeaponSlot? selected, Action<WeaponSlot> openPicker, Action finish)
    {
        _set = Math.Clamp(set, 0, WeaponRules.SetCount - 1);
        _openPicker = openPicker;
        _finish = finish;
        WeaponSlot start = selected ?? WeaponSlot.Primary1;
        SelectedSlot = (int)start < WeaponRules.SlotCount ? start : WeaponSlot.Primary1;
    }

    public void Build(Control host)
    {
        HBoxContainer layout = LoadoutLayout.Root(host);

        PanelContainer listPanel = MenuStyle.Panel($"SET {_set + 1}", "rifle", out VBoxContainer listBody);
        listPanel.CustomMinimumSize = new Vector2(350, 0);
        layout.AddChild(listPanel);

        listBody.AddChild(LoadoutLayout.SectionLabel("NAME", MenuStyle.TextMuted));
        _name = new LineEdit
        {
            Text = PlayerProfile.GetSetName(_set),
            PlaceholderText = WeaponRules.DefaultSetNames[_set],
            MaxLength = WeaponRules.MaxSetNameLength,
        };
        MenuStyle.StyleLineEdit(_name);
        _name.TextChanged += OnNameTyped;
        _name.TextSubmitted += _ => _name.ReleaseFocus();
        _name.FocusExited += CommitName;
        listBody.AddChild(_name);

        _list = LoadoutLayout.ScrollColumn(listBody, 6);

        _stage = LoadoutLayout.StageColumn(layout);

        PanelContainer detailPanel = MenuStyle.Panel("WEAPON", "crosshair", out VBoxContainer detailBody);
        detailPanel.CustomMinimumSize = new Vector2(360, 0);
        layout.AddChild(detailPanel);
        _details = LoadoutLayout.ScrollColumn(detailBody, 8);
        _actions = new VBoxContainer();
        _actions.AddThemeConstantOverride("separation", 8);
        detailBody.AddChild(_actions);

        RebuildList();
        ShowPreview();
        _selectedRow?.CallDeferred(Control.MethodName.GrabFocus);
    }

    // ---- name -------------------------------------------------------------------------------

    private void OnNameTyped(string text)
    {
        // A blank box is only a stop on the way to typing something else; the default comes back
        // when the player leaves the box (CommitName).
        if (!string.IsNullOrWhiteSpace(text)) PlayerProfile.SetSetName(_set, text);
    }

    /// <summary>Saves the typed name, or restores the default if the box was left empty. Called when
    /// the box loses focus and before the screen is left by any button.</summary>
    private void CommitName()
    {
        if (_name == null || !GodotObject.IsInstanceValid(_name)) return;
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            PlayerProfile.SetSetName(_set, null);
            _name.Text = PlayerProfile.GetSetName(_set);
        }
        else PlayerProfile.SetSetName(_set, _name.Text);
    }

    // ---- list -------------------------------------------------------------------------------

    private WeaponData? SlotWeapon(WeaponSlot slot) =>
        OperatorInfo.FindWeapon(PlayerProfile.GetSetWeapon(_set, slot));

    private void Select(WeaponSlot slot)
    {
        SelectedSlot = slot;
        RebuildList();
        ShowPreview();
        _selectedRow?.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void RebuildList()
    {
        LoadoutLayout.Clear(_list);
        _selectedRow = null;
        for (int i = 0; i < WeaponRules.SlotCount; i++)
        {
            var slot = (WeaponSlot)i;
            bool selected = slot == SelectedSlot;
            WeaponData? weapon = SlotWeapon(slot);

            Button row = MenuStyle.ListRow(
                weapon == null ? "rifle" : OperatorInfo.WeaponIcon(weapon), 56, 22,
                (weapon?.WeaponName ?? "NONE").ToUpperInvariant(), WeaponRules.SlotName(slot),
                selected, () => Select(slot));
            _list.AddChild(row);
            if (selected) _selectedRow = row;
        }
    }

    // ---- stage + details --------------------------------------------------------------------

    private void ShowPreview()
    {
        CharacterData? op = OperatorInfo.Find(PlayerProfile.OperatorId);
        _stage.ShowOperator(op, SlotWeapon(SelectedSlot));
        RebuildDetails();
    }

    private void RebuildDetails()
    {
        LoadoutLayout.Clear(_details);
        LoadoutLayout.Clear(_actions);

        WeaponData? weapon = SlotWeapon(SelectedSlot);
        if (weapon != null) WeaponDescription.Fill(_details, weapon);

        WeaponSlot slot = SelectedSlot;
        Button change = MenuStyle.Primary("CHANGE WEAPON", new Vector2(0, 58), () =>
        {
            CommitName();
            _openPicker(slot);
        }, UiSounds.Sound.Select);
        change.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _actions.AddChild(change);

        Button next = MenuStyle.Outline("CONTINUE", () =>
        {
            CommitName();
            _finish();
        }, null, 50);
        _actions.AddChild(next);
    }

    /// <summary>Called by the menu's back button / Escape: make sure a half-typed name is kept.</summary>
    public void Leave() => CommitName();
}

// ==============================================================================================
// CHOOSE WEAPON (pick the weapon for one slot)
// ==============================================================================================

/// <summary>Category tabs along the top (only the kinds this slot may hold), the weapons of the open
/// tab in a list, the model turning on the stage, and the description on the right. CONTINUE puts the
/// weapon in the slot. ATTACHMENTS and SKINS are placeholders that say so.</summary>
public sealed class WeaponPickerScreen
{
    private readonly int _set;
    private readonly WeaponSlot _slot;
    private readonly Action _return;
    private readonly WeaponCategory[] _categories;

    private WeaponCategory _category;
    private string _previewName;

    private Control _host = null!;
    private HBoxContainer _tabs = null!;
    private VBoxContainer _list = null!;
    private VBoxContainer _details = null!;
    private VBoxContainer _actions = null!;
    private OperatorStage _stage = null!;
    private Button? _previewRow;
    private Control? _modal;

    /// <param name="set">Which set (0-2).</param>
    /// <param name="slot">Which slot of the set is being changed.</param>
    /// <param name="returnToSet">Goes back to the set screen (used by CONTINUE and by back).</param>
    public WeaponPickerScreen(int set, WeaponSlot slot, Action returnToSet)
    {
        _set = Math.Clamp(set, 0, WeaponRules.SetCount - 1);
        _slot = slot;
        _return = returnToSet;
        _categories = WeaponRules.CategoriesFor(slot);

        _previewName = PlayerProfile.GetSetWeapon(_set, slot);
        WeaponData? current = OperatorInfo.FindWeapon(_previewName);
        WeaponCategory? currentCategory = current == null ? null : WeaponRules.CategoryOf(current);
        _category = currentCategory != null && Array.IndexOf(_categories, currentCategory.Value) >= 0
            ? currentCategory.Value
            : _categories[0];
    }

    /// <summary>Back button / Escape / gamepad B: closes the popup if one is open, otherwise leaves.</summary>
    public void Back()
    {
        if (CloseModal()) return;
        _return();
    }

    public void Build(Control host)
    {
        _host = host;
        HBoxContainer layout = LoadoutLayout.RootWithTabs(host, out _tabs);

        PanelContainer listPanel = MenuStyle.Panel(WeaponRules.SlotName(_slot), "weapon", out VBoxContainer listBody);
        listPanel.CustomMinimumSize = new Vector2(340, 0);
        layout.AddChild(listPanel);
        _list = LoadoutLayout.ScrollColumn(listBody, 6);

        var soon = new HBoxContainer();
        soon.AddThemeConstantOverride("separation", 8);
        Button attachments = MenuStyle.Outline("ATTACHMENTS", () => OpenSoon("ATTACHMENTS"), null, 46);
        Button skins = MenuStyle.Outline("SKINS", () => OpenSoon("SKINS"), null, 46);
        soon.AddChild(attachments);
        soon.AddChild(skins);
        listBody.AddChild(soon);

        _stage = LoadoutLayout.StageColumn(layout);

        PanelContainer detailPanel = MenuStyle.Panel("DESCRIPTION", "crosshair", out VBoxContainer detailBody);
        detailPanel.CustomMinimumSize = new Vector2(360, 0);
        layout.AddChild(detailPanel);
        _details = LoadoutLayout.ScrollColumn(detailBody, 8);
        _actions = new VBoxContainer();
        detailBody.AddChild(_actions);

        RebuildTabs();
        RebuildList();
        ShowPreview();
        _previewRow?.CallDeferred(Control.MethodName.GrabFocus);
    }

    // ---- categories -------------------------------------------------------------------------

    private void RebuildTabs()
    {
        LoadoutLayout.Clear(_tabs);
        foreach (WeaponCategory category in _categories)
        {
            WeaponCategory captured = category;
            _tabs.AddChild(MenuStyle.WideTab(WeaponRules.CategoryIcon(category), WeaponRules.CategoryName(category),
                category == _category, () => SetCategory(captured)));
        }
    }

    private List<WeaponData> WeaponsInCategory()
    {
        var result = new List<WeaponData>();
        foreach (WeaponData weapon in WeaponRules.WeaponsFor(_slot))
            if (WeaponRules.CategoryOf(weapon) == _category) result.Add(weapon);
        return result;
    }

    private void SetCategory(WeaponCategory category)
    {
        if (category == _category) return;
        _category = category;

        // Keep the preview inside the open tab: the weapon already in this slot if it lives here,
        // otherwise the first weapon of the tab.
        List<WeaponData> inTab = WeaponsInCategory();
        string held = PlayerProfile.GetSetWeapon(_set, _slot);
        _previewName = inTab.Exists(w => w.WeaponName == held)
            ? held
            : inTab.Count > 0 ? inTab[0].WeaponName : _previewName;

        RebuildTabs();
        RebuildList();
        ShowPreview();
        _previewRow?.CallDeferred(Control.MethodName.GrabFocus);
    }

    // ---- list -------------------------------------------------------------------------------

    private WeaponData? PreviewData => OperatorInfo.FindWeapon(_previewName);

    private void Preview(string name)
    {
        _previewName = name;
        RebuildList();
        ShowPreview();
        _previewRow?.CallDeferred(Control.MethodName.GrabFocus);
    }

    private void RebuildList()
    {
        LoadoutLayout.Clear(_list);
        _previewRow = null;

        string[] setWeapons = PlayerProfile.GetSetWeapons(_set);
        string held = setWeapons[(int)_slot];

        foreach (WeaponData weapon in WeaponsInCategory())
        {
            string name = weapon.WeaponName;
            bool previewed = name == _previewName;

            string? tag = null;
            Color tagColor = MenuStyle.Accent;
            if (name == held) tag = "EQUIPPED";
            else
            {
                WeaponSlot? holder = WeaponRules.SlotHolding(name, _slot, setWeapons);
                if (holder != null) { tag = $"IN {WeaponRules.SlotName(holder.Value)}"; tagColor = MenuStyle.TextMuted; }
            }

            int pellets = Math.Max(1, weapon.PelletsPerShot);
            string damage = pellets > 1 ? $"{weapon.BodyDamage:0} x{pellets}" : $"{weapon.BodyDamage:0}";
            Button row = MenuStyle.ListRow(OperatorInfo.WeaponIcon(weapon), 56, 22,
                name.ToUpperInvariant(), $"{OperatorInfo.FireModeName(weapon)}  |  {damage} dmg",
                previewed, () => Preview(name), null, tag, tagColor);
            _list.AddChild(row);
            if (previewed) _previewRow = row;
        }
    }

    // ---- stage + details --------------------------------------------------------------------

    private void ShowPreview()
    {
        WeaponData? weapon = PreviewData;
        if (weapon != null) _stage.ShowWeapon(weapon);
        RebuildDetails();
    }

    private void RebuildDetails()
    {
        LoadoutLayout.Clear(_details);
        LoadoutLayout.Clear(_actions);

        WeaponData? weapon = PreviewData;
        if (weapon == null) return;
        WeaponDescription.Fill(_details, weapon);

        // A weapon that already sits in the set's other slot cannot be picked a second time.
        bool inUse = WeaponRules.SlotHolding(weapon.WeaponName, _slot, PlayerProfile.GetSetWeapons(_set)) != null;
        Button go = MenuStyle.Primary(inUse ? "IN USE" : "CONTINUE", new Vector2(0, 58), Continue, UiSounds.Sound.Select);
        go.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        go.Disabled = inUse;
        _actions.AddChild(go);
    }

    private void Continue()
    {
        WeaponData? weapon = PreviewData;
        if (weapon != null) PlayerProfile.SetSetWeapon(_set, _slot, weapon.WeaponName);
        _return();
    }

    // ---- "coming soon" popup ----------------------------------------------------------------

    private void OpenSoon(string title)
    {
        CloseModal();

        var modal = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
        MenuStyle.Fill(modal);
        var dim = new ColorRect { Color = new Color(0.0f, 0.0f, 0.0f, 0.78f), MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(dim);
        modal.AddChild(dim);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        MenuStyle.Fill(center);
        modal.AddChild(center);

        PanelContainer panel = MenuStyle.Panel(title, "lock", out VBoxContainer body);
        panel.CustomMinimumSize = new Vector2(420, 0);
        body.AddChild(MenuStyle.Text("Coming soon", 30, MenuStyle.TextMain, MenuStyle.Bold));
        Button close = MenuStyle.Card("arrow-left", "BACK", null, new Vector2(0, 56), () => CloseModal(),
            titleSize: 24, iconSize: 26);
        body.AddChild(close);
        center.AddChild(panel);

        _host.AddChild(modal);
        _modal = modal;
        close.GrabFocus();
    }

    /// <summary>Closes the popup. Returns false when there was none open.</summary>
    private bool CloseModal()
    {
        if (_modal == null) return false;
        Control modal = _modal;
        _modal = null;
        if (!GodotObject.IsInstanceValid(modal)) return false;
        modal.GetParent()?.RemoveChild(modal);
        modal.QueueFree();
        _previewRow?.CallDeferred(Control.MethodName.GrabFocus);
        return true;
    }
}
