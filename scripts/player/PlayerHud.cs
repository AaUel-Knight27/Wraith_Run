using Godot;

/// <summary>
/// Minimal local-only HUD: crosshair, HP bar, ammo counter, and a death/respawn message.
/// Built entirely in code (same convention as LanMenu). Only the owning peer's Player instance
/// ever builds this UI - a remote observer's copy of this node does nothing.
/// </summary>
public partial class PlayerHud : CanvasLayer
{
    private Health? _health;
    private WeaponSwitcher? _weapon;
    private Label _healthLabel = null!;
    private ProgressBar _healthBar = null!;
    private Label _ammoLabel = null!;
    private Label _deathLabel = null!;

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
        Build();
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

        var healthBox = new VBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
        healthBox.AddThemeConstantOverride("separation", 4);
        healthBox.SetAnchorsPreset(Control.LayoutPreset.BottomLeft);
        healthBox.Position = new Vector2(24, -70);
        root.AddChild(healthBox);
        _healthBar = new ProgressBar
        {
            MinValue = 0, MaxValue = _health?.MaxHealth ?? 100.0,
            ShowPercentage = false, CustomMinimumSize = new Vector2(200, 18),
        };
        healthBox.AddChild(_healthBar);
        _healthLabel = new Label();
        healthBox.AddChild(_healthLabel);

        var ammoBox = new VBoxContainer { CustomMinimumSize = new Vector2(160, 0) };
        ammoBox.SetAnchorsPreset(Control.LayoutPreset.BottomRight);
        ammoBox.Position = new Vector2(-184, -70);
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
    }

    public override void _Process(double delta)
    {
        if (_health != null)
        {
            _healthBar.MaxValue = _health.MaxHealth;
            _healthBar.Value = _health.CurrentHealth;
            _healthLabel.Text = $"{Mathf.CeilToInt(_health.CurrentHealth)} / {Mathf.CeilToInt(_health.MaxHealth)} HP";
            _deathLabel.Visible = _health.IsDead;
            if (_health.IsDead)
                _deathLabel.Text = $"You died - respawning in {Mathf.CeilToInt(_health.RespawnTimeRemaining)}...";
        }

        if (_weapon != null)
        {
            _ammoLabel.Text = _weapon.MagazineSize <= 0
                ? $"{_weapon.WeaponName}\nMELEE"
                : _weapon.IsReloading
                    ? $"{_weapon.WeaponName}\nRELOADING…"
                    : $"{_weapon.WeaponName}\n{_weapon.CurrentAmmo} / {_weapon.MagazineSize}";
        }
    }
}
