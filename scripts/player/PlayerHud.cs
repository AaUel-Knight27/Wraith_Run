using Godot;
using System.Collections.Generic;

/// <summary>
/// The Tactical (on-foot) tier of the two-tier HUD (Master Development Plan Part 7.3 / Part 16;
/// see also WraithRun_HUD_AssetLog.md's confirmed spec). Flat, high-contrast, cheap Canvas UI -
/// angular amber/white panels on near-black glass, no glow or transpar-glass holography (that's
/// the in-vehicle Holographic tier's job, once vehicles exist). Built entirely in code (same
/// convention as LanMenu). Only the owning peer's Player instance ever builds this UI - a remote
/// observer's copy of this node does nothing.
///
/// The kill feed listens to the KillFeed autoload rather than to nearby players, so every confirmed
/// kill in the match shows up here, not just this player's own. The kill-style bonus stack popup
/// (Part 6 / FR-SC-02) only fires for kills this player themself scored.
/// </summary>
public partial class PlayerHud : CanvasLayer
{
    /// <summary>SDLC good-first-task threshold: the health bar turns red below 30% HP.</summary>
    private const float LowHealthFraction = 0.3f;
    private const int MaxFeedLines = 5;

    /// <summary>How long the kill-style bonus stack popup stays up after a kill (Round 1 art asset
    /// used a similarly brief, punchy flash - no spec-mandated duration, so this is an explicit
    /// implementation choice).</summary>
    private const float BonusPopupSeconds = 2.5f;

    // ---------------------------------------------------------------------------------------
    // Tactical HUD palette (asset log style DNA): amber-orange and white accents on near-black
    // semi-transparent panels, angular (zero corner radius), stenciled-military feel. NOT
    // holographic - no glow, no glassmorphism; that palette belongs to the in-vehicle tier.
    // ---------------------------------------------------------------------------------------
    private static readonly Color PanelBg = new(0.03f, 0.03f, 0.03f, 0.62f);
    private static readonly Color AmberAccent = new(1.0f, 0.62f, 0.13f);
    private static readonly Color WhiteAccent = new(0.94f, 0.94f, 0.94f);
    private static readonly Color HealthyBarColor = new(0.25f, 0.75f, 0.30f);
    private static readonly Color LowHealthBarColor = new(0.85f, 0.20f, 0.20f);
    private static readonly Color DangerRed = new(0.90f, 0.18f, 0.18f);

    // Kill-style bonus stack colors - match the confirmed Round 1 art prompt exactly (gold / orange
    // / red for the three it depicted); verge-of-death gets its own crimson so it never reads as
    // the same bonus as a 360 no-scope.
    private static readonly Color GoldAccent = new(1.0f, 0.84f, 0.0f);
    private static readonly Color OrangeAccent = new(1.0f, 0.55f, 0.0f);
    private static readonly Color RedAccent = new(0.90f, 0.16f, 0.16f);
    private static readonly Color CrimsonAccent = new(0.80f, 0.10f, 0.38f);

    private static readonly (KillStyle Flag, string Label, int Value, Color Color)[] BonusRows =
    {
        (KillStyle.Headshot, "HEADSHOT", KillStyleBonus.HeadshotBonus, GoldAccent),
        (KillStyle.HipFire, "HIP-FIRE", KillStyleBonus.HipFireBonus, OrangeAccent),
        (KillStyle.NoScope360, "360 NO-SCOPE", KillStyleBonus.NoScope360Bonus, RedAccent),
        (KillStyle.VergeOfDeath, "VERGE OF DEATH", KillStyleBonus.VergeOfDeathBonus, CrimsonAccent),
    };

    private CharacterBody3D _player = null!;
    private long _localPeerId;

    private Health? _health;
    private WeaponSwitcher? _weapon;
    private PlayerScore? _score;
    private KillFeed? _killFeed;

    private Label _healthLabel = null!;
    private ProgressBar _healthBar = null!;
    private StyleBoxFlat _healthFill = null!;
    private Label _weaponNameLabel = null!;
    private Label _ammoLabel = null!;
    private Label _deathLabel = null!;
    private Label _scoreLabel = null!;
    private VBoxContainer _killFeedList = null!;
    private readonly List<(Label Line, ulong ExpiryMsec)> _feedLines = new();
    private bool _lowHealthApplied;

    private CompassStrip _compass = null!;

    private PanelContainer _eventBannerPanel = null!;
    private Label _eventBannerLabel = null!;
    private ulong _eventBannerExpiryMsec;

    private PanelContainer _bonusPopupPanel = null!;
    private VBoxContainer _bonusPopupList = null!;
    private ulong _bonusPopupExpiryMsec;

    public override void _Ready()
    {
        _player = (CharacterBody3D)GetParent();
        if (!_player.IsMultiplayerAuthority())
        {
            SetProcess(false);
            return;
        }

        _localPeerId = _player.GetMultiplayerAuthority();
        _health = _player.GetNodeOrNull<Health>("Health");
        _weapon = _player.GetNodeOrNull<WeaponSwitcher>("WeaponSwitcher");
        _score = _player.GetNodeOrNull<PlayerScore>("Score");
        _killFeed = KillFeed.From(this);
        if (_killFeed != null) _killFeed.KillAnnounced += OnKillAnnounced;
        if (_score != null) _score.MultiKillAwarded += OnMultiKillAwarded;
        Build();
    }

    public override void _ExitTree()
    {
        if (_killFeed != null && IsInstanceValid(_killFeed)) _killFeed.KillAnnounced -= OnKillAnnounced;
        if (_score != null && IsInstanceValid(_score)) _score.MultiKillAwarded -= OnMultiKillAwarded;
    }

    // -------------------------------------------------------------------------------------------
    // Build
    // -------------------------------------------------------------------------------------------

    private void Build()
    {
        var root = new Control();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(root);

        var crosshairCenter = new CenterContainer();
        crosshairCenter.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        crosshairCenter.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(crosshairCenter);
        var crosshair = new Label { Text = "+" };
        crosshair.AddThemeFontSizeOverride("font_size", 22);
        crosshair.AddThemeColorOverride("font_color", AmberAccent);
        crosshairCenter.AddChild(crosshair);

        // Touch builds move health/ammo/kill-feed to the TOP edge instead of the bottom. On
        // desktop the bottom band is just empty screen; on mobile TouchControls owns that whole
        // strip for the joystick and action buttons, so anything HUD-related has to clear it.
        bool touch = SettingsManager.Instance != null && SettingsManager.Instance.ShouldShowTouchControls();

        BuildHealth(root, touch);
        BuildAmmo(root, touch);
        BuildCompass(root, touch);
        BuildDeathLabel(root);
        BuildScore(root);
        BuildKillFeed(root, touch);
        BuildEventBanner(root);
        BuildBonusPopup(root);
    }

    /// <summary>Angular dark-glass panel shared by every Tactical HUD readout - one label, one
    /// look, everywhere (asset log fix-pass note: standardize labeling/framing across screens).</summary>
    private static PanelContainer CreatePanel()
    {
        var panel = new PanelContainer();
        var style = new StyleBoxFlat
        {
            BgColor = PanelBg,
            BorderColor = AmberAccent,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 0, CornerRadiusTopRight = 0,
            CornerRadiusBottomLeft = 0, CornerRadiusBottomRight = 0,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        return panel;
    }

    private void BuildHealth(Control root, bool touch)
    {
        var panel = CreatePanel();
        panel.SetAnchorsPreset(touch ? Control.LayoutPreset.TopLeft : Control.LayoutPreset.BottomLeft);
        panel.Position = touch ? new Vector2(24, 24) : new Vector2(24, -70);
        root.AddChild(panel);

        var healthBox = new VBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
        healthBox.AddThemeConstantOverride("separation", 4);
        panel.AddChild(healthBox);

        _healthBar = new ProgressBar
        {
            MinValue = 0, MaxValue = _health?.MaxHealth ?? 100.0,
            ShowPercentage = false, CustomMinimumSize = new Vector2(200, 18),
        };
        var healthBg = new StyleBoxFlat
        {
            BgColor = new Color(0.0f, 0.0f, 0.0f, 0.7f),
            BorderColor = AmberAccent,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
        };
        _healthFill = new StyleBoxFlat { BgColor = HealthyBarColor };
        _healthBar.AddThemeStyleboxOverride("background", healthBg);
        _healthBar.AddThemeStyleboxOverride("fill", _healthFill);
        healthBox.AddChild(_healthBar);

        _healthLabel = new Label();
        _healthLabel.AddThemeColorOverride("font_color", WhiteAccent);
        healthBox.AddChild(_healthLabel);
    }

    private void BuildAmmo(Control root, bool touch)
    {
        var panel = CreatePanel();
        panel.SetAnchorsPreset(touch ? Control.LayoutPreset.TopRight : Control.LayoutPreset.BottomRight);
        panel.Position = touch ? new Vector2(-184, 76) : new Vector2(-184, -70);
        root.AddChild(panel);

        var ammoBox = new VBoxContainer { CustomMinimumSize = new Vector2(160, 0) };
        panel.AddChild(ammoBox);

        _weaponNameLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _weaponNameLabel.AddThemeFontSizeOverride("font_size", 13);
        _weaponNameLabel.AddThemeColorOverride("font_color", AmberAccent);
        ammoBox.AddChild(_weaponNameLabel);

        // MAG / RESERVE format (Master Development Plan Part 7.3). "30 / 90" for the AK-47 is
        // the exact example the confirmed style-board prompt uses.
        _ammoLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _ammoLabel.AddThemeFontSizeOverride("font_size", 22);
        _ammoLabel.AddThemeColorOverride("font_color", WhiteAccent);
        ammoBox.AddChild(_ammoLabel);
    }

    /// <summary>Horizontal compass-strip minimap (asset log open decision #1, now locked: strip,
    /// not disc). Purely a heading readout for now - no map data exists yet, so it draws cardinal
    /// ticks that scroll under a fixed center player-direction marker, same idiom as the confirmed
    /// style-board prompt. Nearby-player blips can be layered onto CompassStrip later without
    /// touching this placement code.</summary>
    private void BuildCompass(Control root, bool touch)
    {
        var panel = CreatePanel();
        panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        // Touch already stacks the health panel here, so the compass drops below it there;
        // on desktop the whole top-left corner is free.
        panel.Position = touch ? new Vector2(24, 96) : new Vector2(24, 24);
        root.AddChild(panel);

        _compass = new CompassStrip
        {
            CustomMinimumSize = new Vector2(260, 40),
            ClipContents = true,
        };
        panel.AddChild(_compass);
    }

    private void BuildDeathLabel(Control root)
    {
        var deathCenter = new CenterContainer();
        deathCenter.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        deathCenter.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(deathCenter);
        _deathLabel = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _deathLabel.AddThemeFontSizeOverride("font_size", 28);
        _deathLabel.AddThemeColorOverride("font_color", DangerRed);
        deathCenter.AddChild(_deathLabel);
    }

    private void BuildScore(Control root)
    {
        var panel = CreatePanel();
        panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        panel.Position = new Vector2(-204, 24);
        root.AddChild(panel);

        var scoreBox = new VBoxContainer { CustomMinimumSize = new Vector2(180, 0) };
        panel.AddChild(scoreBox);
        _scoreLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _scoreLabel.AddThemeFontSizeOverride("font_size", 20);
        _scoreLabel.AddThemeColorOverride("font_color", WhiteAccent);
        scoreBox.AddChild(_scoreLabel);
    }

    private void BuildKillFeed(Control root, bool touch)
    {
        // Kill feed never takes mouse input. On desktop it stacks upward above the health box,
        // bottom-left; on touch it sits below health/ammo/compass at the top instead, and narrower,
        // since 420px of feed text would eat well over half the width of a phone in portrait.
        _killFeedList = new VBoxContainer { CustomMinimumSize = new Vector2(touch ? 260 : 420, 0) };
        _killFeedList.AddThemeConstantOverride("separation", 2);
        _killFeedList.SetAnchorsPreset(touch ? Control.LayoutPreset.TopLeft : Control.LayoutPreset.BottomLeft);
        _killFeedList.Position = touch ? new Vector2(24, 148) : new Vector2(24, -170);
        _killFeedList.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(_killFeedList);
    }

    /// <summary>Top-center banner slot for "active world event" text (Part 7.3's on-foot tier
    /// requirement). No event system is wired up yet - Cursed Events, Convoy Assault sector calls,
    /// etc. can all drive this later through ShowEventBanner/ClearEventBanner. Sits in an
    /// HBoxContainer with center alignment (not a fixed anchor) so it stays centered no matter how
    /// long the banner text is.</summary>
    private void BuildEventBanner(Control root)
    {
        var strip = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        strip.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        strip.Position = new Vector2(0, 20);
        strip.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(strip);

        _eventBannerPanel = CreatePanel();
        _eventBannerPanel.Visible = false;
        strip.AddChild(_eventBannerPanel);

        _eventBannerLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _eventBannerLabel.AddThemeFontSizeOverride("font_size", 18);
        _eventBannerLabel.AddThemeColorOverride("font_color", AmberAccent);
        _eventBannerPanel.AddChild(_eventBannerLabel);
    }

    /// <summary>Screen center-right kill-style bonus stack popup (confirmed Round 1 prompt: each
    /// earned bonus on its own line, its own accent color, stacked). Lives in a full-height strip
    /// pinned to the right so a CenterContainer can center it vertically regardless of screen size,
    /// the same way BuildDeathLabel/the crosshair use a full-rect CenterContainer.</summary>
    private void BuildBonusPopup(Control root)
    {
        var strip = new CenterContainer();
        strip.SetAnchorsPreset(Control.LayoutPreset.RightWide);
        strip.Position = new Vector2(-360, 0);
        strip.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(strip);

        _bonusPopupPanel = CreatePanel();
        _bonusPopupPanel.Visible = false;
        strip.AddChild(_bonusPopupPanel);

        _bonusPopupList = new VBoxContainer { CustomMinimumSize = new Vector2(220, 0) };
        _bonusPopupList.AddThemeConstantOverride("separation", 4);
        _bonusPopupPanel.AddChild(_bonusPopupList);
    }

    // -------------------------------------------------------------------------------------------
    // Event banner public API - for future Cursed Event / Convoy Assault / etc. systems to drive.
    // -------------------------------------------------------------------------------------------

    /// <summary>Shows the top-center event banner. Pass durationSeconds &lt;= 0 to leave it up until
    /// ClearEventBanner() is called explicitly (e.g. for an event that runs until some other
    /// condition ends it, rather than a fixed timer). urgent swaps the amber text for the same red
    /// used on low health, for time-critical warnings like a Cursed Event's 8-second omen.</summary>
    public void ShowEventBanner(string text, float durationSeconds = 8.0f, bool urgent = false)
    {
        _eventBannerLabel.Text = text;
        _eventBannerLabel.AddThemeColorOverride("font_color", urgent ? DangerRed : AmberAccent);
        _eventBannerPanel.Visible = true;
        _eventBannerExpiryMsec = durationSeconds > 0.0f
            ? Time.GetTicksMsec() + (ulong)(durationSeconds * 1000.0f)
            : ulong.MaxValue;
    }

    public void ClearEventBanner() => _eventBannerPanel.Visible = false;

    // -------------------------------------------------------------------------------------------
    // Kill feed + kill-style bonus popup
    // -------------------------------------------------------------------------------------------

    /// <summary>Feed line for any kill in the match, on every peer, in identical wording. Also fires
    /// the bonus stack popup, but only on the device that actually earned the kill.</summary>
    private void OnKillAnnounced(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
        int totalPoints, long assistPeerId, int assistPoints)
    {
        string line = KillFeed.DescribeKill(killerPeerId, victimPeerId, styleFlags, totalPoints);
        if (assistPeerId >= 0) line += $"  (assist Player {assistPeerId} +{assistPoints})";
        AddFeedLine(line);

        if (killerPeerId == _localPeerId) ShowBonusPopup((KillStyle)styleFlags);
    }

    /// <summary>Owner-only line: the multi-kill bonus is decided on the killer's device, so only the
    /// killer sees this extra confirmation.</summary>
    private void OnMultiKillAwarded(int bonusPoints) => AddFeedLine($"MULTI-KILL  +{bonusPoints}");

    private void AddFeedLine(string text)
    {
        var line = new Label { Text = text };
        line.AddThemeFontSizeOverride("font_size", 15);
        line.AddThemeColorOverride("font_color", WhiteAccent);
        _killFeedList.AddChild(line);
        _feedLines.Add((line, Time.GetTicksMsec() + (ulong)(KillFeed.MessageLifetimeSeconds * 1000.0f)));
        while (_feedLines.Count > MaxFeedLines) RemoveFeedLine(0);
    }

    private void RemoveFeedLine(int index)
    {
        Label line = _feedLines[index].Line;
        if (IsInstanceValid(line)) line.QueueFree();
        _feedLines.RemoveAt(index);
    }

    private void ExpireFeedLines()
    {
        ulong now = Time.GetTicksMsec();
        for (int i = _feedLines.Count - 1; i >= 0; i--)
            if (now >= _feedLines[i].ExpiryMsec) RemoveFeedLine(i);
    }

    /// <summary>Rebuilds the stack with only the bonus lines this kill actually earned, in the same
    /// fixed order as the spec table, each in its own accent color. No cap, all stack (FR-SC-02) -
    /// this just decodes the flags KillStyleBonus.Evaluate already scored the kill against.</summary>
    private void ShowBonusPopup(KillStyle styles)
    {
        foreach (Node child in _bonusPopupList.GetChildren()) child.QueueFree();

        bool any = false;
        foreach (var row in BonusRows)
        {
            if ((styles & row.Flag) != row.Flag) continue;
            any = true;
            var line = new Label { Text = $"{row.Label} +{row.Value}", HorizontalAlignment = HorizontalAlignment.Center };
            line.AddThemeFontSizeOverride("font_size", 20);
            line.AddThemeColorOverride("font_color", row.Color);
            _bonusPopupList.AddChild(line);
        }

        if (!any) return;
        _bonusPopupPanel.Visible = true;
        _bonusPopupExpiryMsec = Time.GetTicksMsec() + (ulong)(BonusPopupSeconds * 1000.0f);
    }

    // -------------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_health != null)
        {
            _healthBar.MaxValue = _health.MaxHealth;
            _healthBar.Value = _health.CurrentHealth;
            _healthLabel.Text = $"{Mathf.CeilToInt(_health.CurrentHealth)} / {Mathf.CeilToInt(_health.MaxHealth)} HP";
            ApplyLowHealthColour(_health.MaxHealth > 0.0f && _health.CurrentHealth / _health.MaxHealth < LowHealthFraction);
            _deathLabel.Visible = _health.IsDead;
            if (_health.IsDead)
                _deathLabel.Text = $"You died - respawning in {Mathf.CeilToInt(_health.RespawnTimeRemaining)}...";
        }

        if (_score != null)
            _scoreLabel.Text = $"SCORE {_score.TotalPoints}\nK {_score.Kills} / D {_score.Deaths}";

        ExpireFeedLines();

        if (_weapon != null)
        {
            _weaponNameLabel.Text = _weapon.WeaponName;
            _ammoLabel.Text = _weapon.MagazineSize <= 0
                ? "MELEE"
                : _weapon.IsReloading
                    ? "RELOADING…"
                    : $"{_weapon.CurrentAmmo} / {_weapon.CurrentReserve}";
        }

        _compass.HeadingDegrees = Mathf.RadToDeg(_player.Rotation.Y);
        _compass.QueueRedraw();

        ulong now = Time.GetTicksMsec();
        if (_eventBannerPanel.Visible && now >= _eventBannerExpiryMsec) _eventBannerPanel.Visible = false;
        if (_bonusPopupPanel.Visible && now >= _bonusPopupExpiryMsec) _bonusPopupPanel.Visible = false;
    }

    /// <summary>SDLC good-first-task: the bar reads green normally and red below 30% HP. The override
    /// is only re-applied when the state flips, so the HUD is not rewriting theme data every frame.</summary>
    private void ApplyLowHealthColour(bool low)
    {
        if (low == _lowHealthApplied) return;
        _lowHealthApplied = low;
        _healthFill.BgColor = low ? LowHealthBarColor : HealthyBarColor;
        _healthBar.AddThemeStyleboxOverride("fill", _healthFill);
    }

    /// <summary>
    /// Compass-strip minimap (asset log open decision #1, locked to "strip" over "disc"). Draws
    /// cardinal/intercardinal ticks that scroll under a fixed center player-direction marker as
    /// HeadingDegrees changes - a real heading readout, not a placeholder. Has no notion of world
    /// terrain or other players yet; that can be layered into _Draw later (e.g. nearby teammate
    /// blips) without changing how this control is placed in the HUD.
    /// </summary>
    private partial class CompassStrip : Control
    {
        private const float PixelsPerDegree = 3.2f;
        private static readonly Color TickMajor = AmberAccent;
        private static readonly Color TickMinor = new(1.0f, 1.0f, 1.0f, 0.45f);

        public float HeadingDegrees;

        public override void _Draw()
        {
            Vector2 size = Size;
            float centerX = size.X / 2.0f;

            for (int offsetDeg = -135; offsetDeg <= 135; offsetDeg += 15)
            {
                float x = centerX + offsetDeg * PixelsPerDegree;
                if (x < -10.0f || x > size.X + 10.0f) continue;

                float worldDeg = Mathf.Wrap(HeadingDegrees + offsetDeg, 0.0f, 360.0f);
                int rounded = Mathf.RoundToInt(worldDeg) % 360;
                bool cardinal = rounded % 90 == 0;
                bool intercardinal = rounded % 45 == 0;

                float tickHeight = cardinal ? 14.0f : intercardinal ? 9.0f : 5.0f;
                Color tickColor = cardinal ? TickMajor : TickMinor;
                DrawLine(new Vector2(x, size.Y - tickHeight), new Vector2(x, size.Y), tickColor, 2.0f);

                if (cardinal)
                {
                    string label = CardinalLabel(rounded);
                    DrawString(ThemeDB.FallbackFont, new Vector2(x - 5.0f, size.Y - tickHeight - 4.0f),
                        label, HorizontalAlignment.Center, -1.0f, 13, TickMajor);
                }
            }

            // Fixed center player-direction marker - never scrolls, only the strip beneath it does.
            var arrow = new Vector2[]
            {
                new(centerX - 6.0f, 2.0f),
                new(centerX + 6.0f, 2.0f),
                new(centerX, 13.0f),
            };
            DrawColoredPolygon(arrow, WhiteAccent);
        }

        private static string CardinalLabel(int deg) => deg switch
        {
            0 => "N",
            90 => "E",
            180 => "S",
            270 => "W",
            _ => string.Empty,
        };
    }
}
