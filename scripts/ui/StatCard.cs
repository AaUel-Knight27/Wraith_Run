using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The player stat card: the modern version of the ASCII mock-up.
///
///   +--------------------------------------------------------------------+
///   | [avatar]  NAME  (YOU)                          K/D 2.19  WIN 63%   |
///   |           645 matches  .  64.9 h  .  63% win rate           [EDIT] |
///   +--------------------------------+-----------------------------------+
///   | MAIN STATS                     | MEDALS / ACHIEVEMENTS             |
///   |  Matches Played | Wins         |  Clear Kill 10,338 | Head shot ...|
///   |  Ties           | Losses       |  ...                              |
///   |  ...                           |                                   |
///   | BEST WEAPONS                   |                                   |
///   |  NAME      K/D     KILLS       |                                   |
///   |  AK-47     8.17    49          |                                   |
///   +--------------------------------+-----------------------------------+
///
/// One builder serves every place a card is shown - your own PROFILE screen in the main menu and
/// another player's card from the in-match PLAYERS list - so the two can never look different.
/// It returns an ordinary control; the caller puts it in a ScrollContainer (a card is taller than a
/// phone's landscape screen). Everything it draws is plain Label text, so a hostile player name can
/// never inject formatting.
/// </summary>
public static class StatCard
{
    private const int BestWeaponCount = 4;

    public static Control Build(PlayerCard card, bool isYou, bool wide, Action? onEdit = null)
    {
        var root = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        root.AddThemeConstantOverride("separation", 12);
        root.AddChild(BuildHeader(card, isYou, onEdit));

        Control left = BuildLeftColumn(card.Stats);
        Control right = BuildMedals(card.Stats, wide);
        if (wide)
        {
            var columns = new HBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            columns.AddThemeConstantOverride("separation", 14);
            left.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            left.SizeFlagsStretchRatio = 0.45f;
            right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            right.SizeFlagsStretchRatio = 0.55f;
            columns.AddChild(left);
            columns.AddChild(right);
            root.AddChild(columns);
        }
        else
        {
            root.AddChild(left);
            root.AddChild(right);
        }
        return root;
    }

    /// <summary>What to show for somebody whose card has not reached this machine (yet).</summary>
    public static Control BuildUnavailable(string name, string message)
    {
        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var style = MenuStyle.Box(MenuStyle.PanelFill, MenuStyle.Hairline);
        style.SetContentMarginAll(14);
        panel.AddThemeStyleboxOverride("panel", style);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 16);
        row.AddChild(AvatarView.CreateUnknown(84));
        var texts = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        texts.AddChild(MenuStyle.Text(name, 38, MenuStyle.TextMain, MenuStyle.Bold));
        texts.AddChild(MenuStyle.Wrapped(message, 18, MenuStyle.TextDim, MenuStyle.Medium));
        row.AddChild(texts);
        panel.AddChild(row);
        return panel;
    }

    // -------------------------------------------------------------------------------------------
    // Header
    // -------------------------------------------------------------------------------------------

    private static Control BuildHeader(PlayerCard card, bool isYou, Action? onEdit)
    {
        CareerStats s = card.Stats;

        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var style = MenuStyle.Box(MenuStyle.PanelFill, isYou ? MenuStyle.SelectedBorder : MenuStyle.Hairline,
            isYou ? 2 : 1);
        style.SetContentMarginAll(14);
        panel.AddThemeStyleboxOverride("panel", style);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 16);
        panel.AddChild(row);
        row.AddChild(AvatarView.Create(card, 84));

        var names = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        names.AddThemeConstantOverride("separation", 0);

        var nameRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        nameRow.AddThemeConstantOverride("separation", 10);
        Label name = MenuStyle.Text(card.Name, 38, MenuStyle.TextMain, MenuStyle.Bold);
        name.ClipText = true;
        name.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        nameRow.AddChild(name);
        if (isYou) nameRow.AddChild(MenuStyle.Chip("YOU", MenuStyle.Accent, 13, filled: true));
        names.AddChild(nameRow);

        string subtitle = s.MatchesPlayed == 0
            ? "No matches played yet"
            : $"{CareerStats.FormatCount(s.MatchesPlayed)} matches  \u00b7  {CareerStats.FormatPlaytime(s.PlaytimeSeconds)}";
        names.AddChild(MenuStyle.Text(subtitle, 17, MenuStyle.TextDim, MenuStyle.Medium));
        row.AddChild(names);

        row.AddChild(BigStat("K/D", CareerStats.FormatKd(s.KdRatio), MenuStyle.Accent));
        row.AddChild(BigStat("WIN RATE", CareerStats.FormatPercent(s.WinRatePercent), MenuStyle.TextMain));

        if (onEdit != null)
        {
            Button edit = MenuStyle.Outline("EDIT", onEdit, "user", 44);
            edit.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            edit.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            edit.CustomMinimumSize = new Vector2(120, 44);
            row.AddChild(edit);
        }
        return panel;
    }

    private static Control BigStat(string caption, string value, Color color)
    {
        var box = new VBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        box.AddThemeConstantOverride("separation", -6);
        box.AddChild(MenuStyle.Text(caption, 14, MenuStyle.TextMuted, MenuStyle.SemiBold, HorizontalAlignment.Center));
        box.AddChild(MenuStyle.Text(value, 38, color, MenuStyle.Bold, HorizontalAlignment.Center));
        return box;
    }

    // -------------------------------------------------------------------------------------------
    // Left column: main stats + best weapons
    // -------------------------------------------------------------------------------------------

    private static Control BuildLeftColumn(CareerStats s)
    {
        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 12);

        // ---- MAIN STATS: ten figures, two to a row, in the order of the original card ----
        PanelContainer main = MenuStyle.Panel("MAIN STATS", "crosshair", out VBoxContainer mainBody);
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 6);
        grid.MouseFilter = Control.MouseFilterEnum.Ignore;
        grid.AddChild(StatCell("Matches Played", CareerStats.FormatCount(s.MatchesPlayed)));
        grid.AddChild(StatCell("Wins", CareerStats.FormatCount(s.Wins)));
        grid.AddChild(StatCell("Ties", CareerStats.FormatCount(s.Ties)));
        grid.AddChild(StatCell("Losses", CareerStats.FormatCount(s.Losses)));
        grid.AddChild(StatCell("Kills", CareerStats.FormatCount(s.Kills)));
        grid.AddChild(StatCell("Deaths", CareerStats.FormatCount(s.Deaths)));
        grid.AddChild(StatCell("K/D", CareerStats.FormatKd(s.KdRatio), MenuStyle.Accent));
        grid.AddChild(StatCell("Head shots", CareerStats.FormatCount(s.HeadShots)));
        grid.AddChild(StatCell("Head shots %", CareerStats.FormatPercent(s.HeadShotPercent)));
        grid.AddChild(StatCell("Playtime", CareerStats.FormatPlaytime(s.PlaytimeSeconds)));
        mainBody.AddChild(grid);
        column.AddChild(main);

        // ---- BEST WEAPONS ----
        PanelContainer weapons = MenuStyle.Panel("BEST WEAPONS", "weapon", out VBoxContainer weaponBody);
        List<KeyValuePair<string, WeaponRecord>> best = s.BestWeapons(BestWeaponCount);
        if (best.Count == 0)
        {
            weaponBody.AddChild(MenuStyle.Wrapped("No weapon kills recorded yet.", 17, MenuStyle.TextMuted, MenuStyle.Medium));
        }
        else
        {
            weaponBody.AddChild(WeaponLine("NAME", "K/D", "KILLS", 14, MenuStyle.TextMuted, MenuStyle.Bold, -1.0f));
            double topKd = 0.0;
            foreach (var entry in best) topKd = Math.Max(topKd, entry.Value.KdRatio);
            foreach (var entry in best)
            {
                float fraction = topKd > 0.0 ? (float)(entry.Value.KdRatio / topKd) : 0.0f;
                weaponBody.AddChild(WeaponLine(entry.Key, CareerStats.FormatKd(entry.Value.KdRatio),
                    CareerStats.FormatCount(entry.Value.Kills), 19, MenuStyle.TextMain, MenuStyle.SemiBold, fraction));
            }
        }
        column.AddChild(weapons);
        return column;
    }

    /// <summary>One figure: caption on the left, number on the right, in a dark tile.</summary>
    private static Control StatCell(string label, string value, Color? valueColor = null)
    {
        var tile = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var style = MenuStyle.Box(MenuStyle.RowFill, MenuStyle.Hairline);
        style.ContentMarginLeft = 10;
        style.ContentMarginRight = 10;
        style.ContentMarginTop = 6;
        style.ContentMarginBottom = 6;
        tile.AddThemeStyleboxOverride("panel", style);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 8);
        Label caption = MenuStyle.Text(label, 16, MenuStyle.TextMuted, MenuStyle.SemiBold);
        caption.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        caption.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        caption.ClipText = true;
        row.AddChild(caption);
        Label number = MenuStyle.Text(value, 22, valueColor ?? MenuStyle.TextMain, MenuStyle.Bold, HorizontalAlignment.Right);
        number.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(number);
        tile.AddChild(row);
        return tile;
    }

    /// <summary>A table row: name, K/D, kills - plus, when fraction >= 0, a thin bar showing this
    /// weapon's K/D against the best one on the card.</summary>
    private static Control WeaponLine(string name, string kd, string kills, int size, Color color, Font font,
        float fraction)
    {
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 3);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 6);
        Label nameLabel = MenuStyle.Text(name, size, color, font);
        nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        nameLabel.ClipText = true;
        row.AddChild(nameLabel);
        Label kdLabel = MenuStyle.Text(kd, size, fraction >= 0.0f ? MenuStyle.Accent : color, font, HorizontalAlignment.Right);
        kdLabel.CustomMinimumSize = new Vector2(62, 0);
        row.AddChild(kdLabel);
        Label killsLabel = MenuStyle.Text(kills, size, color, font, HorizontalAlignment.Right);
        killsLabel.CustomMinimumSize = new Vector2(72, 0);
        row.AddChild(killsLabel);
        box.AddChild(row);

        if (fraction >= 0.0f)
        {
            var track = new Control { CustomMinimumSize = new Vector2(0, 4), MouseFilter = Control.MouseFilterEnum.Ignore };
            var back = new ColorRect { Color = new Color(0.114f, 0.067f, 0.075f), MouseFilter = Control.MouseFilterEnum.Ignore };
            MenuStyle.Fill(back);
            track.AddChild(back);
            var bar = new ColorRect { Color = MenuStyle.Accent, MouseFilter = Control.MouseFilterEnum.Ignore };
            MenuStyle.Place(bar, 0, 0, Mathf.Clamp(fraction, 0.0f, 1.0f), 1, 0, 0, 0, 0);
            track.AddChild(bar);
            box.AddChild(track);
        }
        return box;
    }

    // -------------------------------------------------------------------------------------------
    // Right column: medals
    // -------------------------------------------------------------------------------------------

    private static Control BuildMedals(CareerStats s, bool wide)
    {
        PanelContainer panel = MenuStyle.Panel("MEDALS / ACHIEVEMENTS", "check", out VBoxContainer body);
        var grid = new GridContainer { Columns = wide ? 2 : 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 6);
        grid.MouseFilter = Control.MouseFilterEnum.Ignore;

        foreach (MedalCatalog.Info info in MedalCatalog.All)
            grid.AddChild(MedalCell(info, s.MedalCount(info.Medal)));
        body.AddChild(grid);
        return panel;
    }

    /// <summary>Medals you have not earned yet stay on the card, dimmed, so the player can see what
    /// there is to go after; hovering one (desktop) tells them how.</summary>
    private static Control MedalCell(MedalCatalog.Info info, long count)
    {
        bool earned = count > 0;
        var tile = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = info.Description,
        };
        var style = MenuStyle.Box(MenuStyle.RowFill, earned ? MenuStyle.Hairline : new Color(0.10f, 0.05f, 0.06f));
        style.ContentMarginLeft = 10;
        style.ContentMarginRight = 10;
        style.ContentMarginTop = 5;
        style.ContentMarginBottom = 5;
        tile.AddThemeStyleboxOverride("panel", style);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 6);
        Label label = MenuStyle.Text(info.Label, 16, earned ? MenuStyle.TextDim : MenuStyle.TextMuted, MenuStyle.SemiBold);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        label.ClipText = true;
        row.AddChild(label);
        Label number = MenuStyle.Text(CareerStats.FormatCount(count), 18,
            earned ? MenuStyle.TextMain : MenuStyle.TextMuted, MenuStyle.Bold, HorizontalAlignment.Right);
        number.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(number);
        tile.AddChild(row);
        return tile;
    }
}
