
using Godot;
using System.Collections.Generic;

/// <summary>
/// Minimal local-only HUD: crosshair, HP bar, ammo counter, score, kill feed, and a death/respawn
/// message. Built entirely in code (same convention as LanMenu). Only the owning peer's Player
/// instance ever builds this UI - a remote observer's copy of this node does nothing.
///
/// The kill feed listens to the KillFeed autoload rather than to nearby players, so every confirmed
/// kill in the match shows up here, not just this player's own.
/// </summary>
public partial class PlayerHud : CanvasLayer
{
    /// <summary>SDLC good-first-task threshold: the health bar turns red below 30% HP.</summary>
    private const float LowHealthFraction = 0.3f;
    private const int MaxFeedLines = 5;

    private static readonly Color HealthyBarColor = new(0.25f, 0.75f, 0.30f);
    private static readonly Color LowHealthBarColor = new(0.85f, 0.20f, 0.20f);

    private Health? _health;
    private WeaponSwitcher? _weapon;
    private PlayerScore? _score;
    private KillFeed? _killFeed;
    private Label _healthLabel = null!;
    private ProgressBar _healthBar = null!;
    private StyleBoxFlat _healthFill = null!;
    private Label _ammoLabel = null!;
    private Label _deathLabel = null!;
    private Label _scoreLabel = null!;
    private Label _teamScoreLabel = null!;
    private Label _matchOverLabel = null!;
    private VBoxContainer _killFeedList = null!;
    private readonly List<(Label Line, ulong ExpiryMsec)> _feedLines = new();
    private bool _lowHealthApplied;

    public override void _Ready()
    {
        var player = (CharacterBody3D)GetParent();
        if (!player.IsMultiplayerAuthority())
        {
            SetProcess(false);
            return;
        }

        _health = player.GetNodeOrNull<Health>("Health");
        _weapon = player.GetNodeOrNull<WeaponSwitcher>("WeaponSwitcher");
        _score = player.GetNodeOrNull<PlayerScore>("Score");
        _killFeed = KillFeed.From(this);
        if (_killFeed != null) _killFeed.KillAnnounced += OnKillAnnounced;
        if (_score != null) _score.MultiKillAwarded += OnMultiKillAwarded;
        if (MatchManager.Instance != null) MatchManager.Instance.MatchEnded += OnMatchEnded;
        Build();
    }

    public override void _ExitTree()
    {
        if (_killFeed != null && IsInstanceValid(_killFeed)) _killFeed.KillAnnounced -= OnKillAnnounced;
        if (_score != null && IsInstanceValid(_score)) _score.MultiKillAwarded -= OnMultiKillAwarded;
        if (MatchManager.Instance != null && IsInstanceValid(MatchManager.Instance))
            MatchManager.Instance.MatchEnded -= OnMatchEnded;
    }

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
        crosshairCenter.AddChild(crosshair);

        // Touch builds move health/ammo/kill-feed to the TOP edge instead of the bottom. On
        // desktop the bottom band is just empty screen; on mobile TouchControls owns that whole
        // strip for the joystick and action buttons, so anything HUD-related has to clear it.
        bool touch = SettingsManager.Instance != null && SettingsManager.Instance.ShouldShowTouchControls();

        var healthBox = new VBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
        healthBox.AddThemeConstantOverride("separation", 4);
        healthBox.SetAnchorsPreset(touch ? Control.LayoutPreset.TopLeft : Control.LayoutPreset.BottomLeft);
        healthBox.Position = touch ? new Vector2(24, 24) : new Vector2(24, -70);
        root.AddChild(healthBox);
        _healthBar = new ProgressBar
        {
            MinValue = 0, MaxValue = _health?.MaxHealth ?? 100.0,
            ShowPercentage = false, CustomMinimumSize = new Vector2(200, 18),
        };
        _healthFill = new StyleBoxFlat { BgColor = HealthyBarColor };
        _healthBar.AddThemeStyleboxOverride("fill", _healthFill);
        healthBox.AddChild(_healthBar);
        _healthLabel = new Label();
        healthBox.AddChild(_healthLabel);

        var ammoBox = new VBoxContainer { CustomMinimumSize = new Vector2(160, 0) };
        ammoBox.SetAnchorsPreset(touch ? Control.LayoutPreset.TopRight : Control.LayoutPreset.BottomRight);
        ammoBox.Position = touch ? new Vector2(-184, 76) : new Vector2(-184, -70);
        root.AddChild(ammoBox);
        _ammoLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _ammoLabel.AddThemeFontSizeOverride("font_size", 20);
        ammoBox.AddChild(_ammoLabel);

        var deathCenter = new CenterContainer();
        deathCenter.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        deathCenter.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(deathCenter);
        _deathLabel = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _deathLabel.AddThemeFontSizeOverride("font_size", 28);
        deathCenter.AddChild(_deathLabel);

        var scoreBox = new VBoxContainer { CustomMinimumSize = new Vector2(180, 0) };
        scoreBox.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        scoreBox.Position = new Vector2(-204, 24);
        root.AddChild(scoreBox);
        _scoreLabel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _scoreLabel.AddThemeFontSizeOverride("font_size", 20);
        scoreBox.AddChild(_scoreLabel);

        // Team Deathmatch only: a top-centre "ALPHA 12 - BRAVO 9" line, updated in _Process.
        // Stays hidden in Deathmatch (FFA) since _teamScoreLabel.Visible only ever goes true once
        // MatchManager.GameMode is TeamDeathmatch.
        var teamScoreCenter = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        teamScoreCenter.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        teamScoreCenter.Position = new Vector2(0, 20);
        root.AddChild(teamScoreCenter);
        _teamScoreLabel = new Label { Visible = false };
        _teamScoreLabel.AddThemeFontSizeOverride("font_size", 22);
        teamScoreCenter.AddChild(_teamScoreLabel);

        // Match-over banner - built once, hidden until MatchManager's MatchEnded signal fires
        // (see OnMatchEnded), never polled in _Process like the labels above it.
        var matchOverCenter = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        matchOverCenter.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        matchOverCenter.Position = new Vector2(0, -80);
        root.AddChild(matchOverCenter);
        _matchOverLabel = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _matchOverLabel.AddThemeFontSizeOverride("font_size", 36);
        matchOverCenter.AddChild(_matchOverLabel);

        // Kill feed never takes mouse input. On desktop it stacks upward above the health box,
        // bottom-left; on touch it sits below health/ammo at the top instead, and narrower, since
        // 420px of feed text would eat well over half the width of a phone in portrait.
        _killFeedList = new VBoxContainer { CustomMinimumSize = new Vector2(touch ? 260 : 420, 0) };
        _killFeedList.AddThemeConstantOverride("separation", 2);
        _killFeedList.SetAnchorsPreset(touch ? Control.LayoutPreset.TopLeft : Control.LayoutPreset.BottomLeft);
        _killFeedList.Position = touch ? new Vector2(24, 74) : new Vector2(24, -170);
        _killFeedList.MouseFilter = Control.MouseFilterEnum.Ignore;
        root.AddChild(_killFeedList);
    }

    /// <summary>Feed line for any kill in the match, on every peer, in identical wording.</summary>
    private void OnKillAnnounced(long killerPeerId, long victimPeerId, int styleFlags, int basePoints,
        int totalPoints, long assistPeerId, int assistPoints)
    {
        string line = KillFeed.DescribeKill(killerPeerId, victimPeerId, styleFlags, totalPoints);
        if (assistPeerId >= 0) line += $"  (assist Player {assistPeerId} +{assistPoints})";
        AddFeedLine(line);
    }

    /// <summary>Owner-only line: the multi-kill bonus is decided on the killer's device, so only the
    /// killer sees this extra confirmation.</summary>
    private void OnMultiKillAwarded(int bonusPoints) => AddFeedLine($"MULTI-KILL  +{bonusPoints}");

    /// <summary>Fires once, locally, when this peer's own MatchManager tally hits the score limit
    /// or the clock runs out (see MatchManager.EndMatch) - every peer reaches the same result
    /// independently, so there is nothing to wait for here.</summary>
    private void OnMatchEnded(int winningTeam, string reason)
    {
        string reasonText = reason == "time" ? "Time Limit" : "Score Limit";
        string resultText = winningTeam < 0 ? "MATCH DRAW" : $"TEAM {MatchManager.TeamName(winningTeam)} WINS";
        _matchOverLabel.Text = $"{resultText}\n({reasonText} Reached)";
        _matchOverLabel.Visible = true;
    }

    private void AddFeedLine(string text)
    {
        var line = new Label { Text = text };
        line.AddThemeFontSizeOverride("font_size", 15);
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

        MatchManager? match = MatchManager.Instance;
        bool teamDeathmatch = match != null && match.GameMode == MatchManager.Mode.TeamDeathmatch;

        if (_score != null)
        {
            string scoreText = $"SCORE {_score.TotalPoints}\nK {_score.Kills} / D {_score.Deaths}";
            if (teamDeathmatch)
            {
                int myTeam = match!.GetTeam(Multiplayer.GetUniqueId());
                if (myTeam >= 0) scoreText += $"\nTEAM {MatchManager.TeamName(myTeam)}";
            }
            _scoreLabel.Text = scoreText;
        }

        _teamScoreLabel.Visible = teamDeathmatch;
        if (teamDeathmatch)
            _teamScoreLabel.Text = $"{MatchManager.TeamName(0)} {match!.GetTeamScore(0)}   -   " +
                $"{MatchManager.TeamName(1)} {match.GetTeamScore(1)}";

        ExpireFeedLines();

        if (_weapon != null)
        {
            _ammoLabel.Text = _weapon.MagazineSize <= 0
                ? $"{_weapon.WeaponName}\nMELEE"
                : _weapon.IsReloading
                    ? $"{_weapon.WeaponName}\nRELOADING…"
                    : $"{_weapon.WeaponName}\n{_weapon.CurrentAmmo} / {_weapon.MagazineSize}";
        }
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
}
