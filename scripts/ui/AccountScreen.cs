using Godot;
using System;

/// <summary>
/// The screen where a player chooses their account: the one name everyone will see, and an avatar.
/// Shown automatically the first time the game is opened (CREATE ACCOUNT) and again from the
/// PROFILE screen's EDIT button (EDIT PROFILE). Same Build(host) shape as OperatorScreen and
/// CustomizeScreen in LoadoutScreens.cs; LanMenu supplies the header and the "what next" callback.
///
/// Left: name box with a live preview and the SAVE button. Right: emblem and colour pickers.
/// Picking an emblem or colour updates the picker and the preview in place, so the name you are
/// typing is never thrown away.
/// </summary>
public sealed class AccountScreen
{
    private readonly bool _firstRun;
    private readonly Action _onDone;

    private string _name;
    private int _emblem;
    private int _color;

    private Label? _status;
    private VBoxContainer? _preview;
    private VBoxContainer? _pickers;

    public AccountScreen(bool firstRun, Action onDone)
    {
        _firstRun = firstRun;
        _onDone = onDone;
        PlayerCard card = PlayerAccount.Current;
        _name = card.Name;
        _emblem = card.AvatarEmblem;
        _color = card.AvatarColor;
    }

    public void Build(Control host)
    {
        HBoxContainer layout = LoadoutLayout.Root(host);

        // ---- left: the name ----
        PanelContainer namePanel = MenuStyle.Panel(_firstRun ? "WHO ARE YOU?" : "YOUR NAME", "user",
            out VBoxContainer nameBody);
        namePanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        namePanel.CustomMinimumSize = new Vector2(360, 0);
        layout.AddChild(namePanel);
        VBoxContainer nameColumn = LoadoutLayout.ScrollColumn(nameBody, 10);

        nameColumn.AddChild(MenuStyle.Text("ACCOUNT NAME", 16, MenuStyle.TextDim, MenuStyle.SemiBold));
        var edit = new LineEdit
        {
            Text = _name,
            PlaceholderText = "Your name",
            MaxLength = AccountRules.MaxNameLength,
        };
        MenuStyle.StyleLineEdit(edit);
        edit.TextChanged += text =>
        {
            _name = text;
            RebuildPreview();
        };
        edit.TextSubmitted += _ => Save();
        nameColumn.AddChild(edit);

        nameColumn.AddChild(MenuStyle.Wrapped(
            $"Anything you like, up to {AccountRules.MaxNameLength} characters. This is the one name other players " +
            "see - in the kill feed, on the scoreboard and on your stat card. Your stats are saved with it on this device.",
            16, MenuStyle.TextMuted));

        _status = MenuStyle.Text(string.Empty, 17, MenuStyle.Accent, MenuStyle.SemiBold);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        nameColumn.AddChild(_status);

        _preview = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        nameColumn.AddChild(_preview);
        RebuildPreview();

        nameBody.AddChild(MenuStyle.Primary(_firstRun ? "CREATE" : "SAVE", new Vector2(0, 62), Save));

        // ---- right: the avatar ----
        PanelContainer avatarPanel = MenuStyle.Panel("AVATAR", "class-operator", out VBoxContainer avatarBody);
        avatarPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        avatarPanel.CustomMinimumSize = new Vector2(300, 0);
        layout.AddChild(avatarPanel);
        _pickers = LoadoutLayout.ScrollColumn(avatarBody, 10);
        RebuildPickers();

        if (_firstRun) edit.GrabFocus();
    }

    private void Save()
    {
        if (!PlayerAccount.Apply(_name, _emblem, _color))
        {
            UiSounds.Play(UiSounds.Sound.Error);
            if (_status != null) _status.Text = "Type a name first - letters, numbers or symbols, whatever you like.";
            return;
        }
        _onDone();
    }

    private void RebuildPreview()
    {
        if (_preview == null) return;
        LoadoutLayout.Clear(_preview);

        string shown = AccountRules.SanitizeName(_name);
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 14);
        row.AddChild(AvatarView.Create(_emblem, _color, 76));
        var texts = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        texts.AddThemeConstantOverride("separation", -4);
        texts.AddChild(MenuStyle.Text(shown.Length > 0 ? shown : "Your name", 32,
            shown.Length > 0 ? MenuStyle.TextMain : MenuStyle.TextMuted, MenuStyle.Bold));
        texts.AddChild(MenuStyle.Text("How other players see you", 15, MenuStyle.TextMuted, MenuStyle.Medium));
        row.AddChild(texts);
        _preview.AddChild(row);
    }

    private void RebuildPickers()
    {
        if (_pickers == null) return;
        LoadoutLayout.Clear(_pickers);

        _pickers.AddChild(LoadoutLayout.SectionLabel("EMBLEM", MenuStyle.TextMuted));
        var emblems = new GridContainer { Columns = 5 };
        emblems.AddThemeConstantOverride("h_separation", 8);
        emblems.AddThemeConstantOverride("v_separation", 8);
        for (int i = 0; i < AvatarCatalog.EmblemCount; i++)
        {
            int captured = i;
            emblems.AddChild(MenuStyle.Tab(AvatarView.EmblemIcons[i], null, i == _emblem,
                AvatarView.EmblemNames[i], () => Pick(captured, _color)));
        }
        _pickers.AddChild(emblems);

        _pickers.AddChild(LoadoutLayout.SectionLabel("COLOUR", MenuStyle.TextMuted));
        var colors = new GridContainer { Columns = 4 };
        colors.AddThemeConstantOverride("h_separation", 8);
        colors.AddThemeConstantOverride("v_separation", 8);
        for (int i = 0; i < AvatarCatalog.ColorCount; i++)
        {
            int captured = i;
            colors.AddChild(MenuStyle.Swatch(AvatarView.Palette[i], i == _color, () => Pick(_emblem, captured)));
        }
        _pickers.AddChild(colors);
    }

    private void Pick(int emblem, int color)
    {
        _emblem = emblem;
        _color = color;
        RebuildPickers();
        RebuildPreview();
    }
}
