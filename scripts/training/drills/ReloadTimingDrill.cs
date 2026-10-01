using Godot;
using System.Collections.Generic;

/// <summary>Reload discipline. You start each wave with a half-empty magazine while enemies strafe and return fire.
/// Reload in the lulls, not while someone is shooting at you, and never get caught pulling the trigger on empty.</summary>
public partial class ReloadTimingDrill : TrainingDrill
{
    private static readonly int[] WaveSizes = { 2, 3, 3, 3 };

    private Transform3D _anchor;
    private readonly RandomNumberGenerator _rng = new();
    private readonly List<TrainingActor> _enemies = new();
    private int _wave = -1;
    private float _pause = 1.0f;
    private bool _waveActive;
    private int _kills;
    private int _total;

    public override string Objective => _waveActive
        ? $"Wave {_wave + 1}/{WaveSizes.Length}: kill them. You are low on ammo - reload when nobody is shooting you."
        : "LULL. Nobody is engaging you - the right moment to reload. Next wave is coming.";

    public override string StatusLine => $"Kills {_kills}/{_total}   bad reloads {Session.Stats.BadReloads}   dry fires {Session.Stats.DryFires}";

    protected override void OnBegin()
    {
        _rng.Randomize();
        foreach (int n in WaveSizes) _total += n;
        _anchor = Session.Anchor(TrainingAnchors.CombatYard);
        Session.PlacePlayer(_anchor);
        Session.Weapons.EquipByName("M4");
    }

    protected override void OnTick(float dt)
    {
        if (_waveActive) return;
        _pause -= dt;
        if (_pause > 0.0f) return;
        if (_wave + 1 >= WaveSizes.Length)
        {
            Finish(_kills >= _total - 2, _kills >= _total - 2 ? "Disciplined." : "Too many got away - manage your ammo.");
            return;
        }
        StartWave(_wave + 1);
    }

    private void StartWave(int index)
    {
        _wave = index;
        _waveActive = true;
        Session.Weapons.SetLoadedRounds(Mathf.RoundToInt(Session.Weapons.MagazineSize * 0.35f));

        int count = WaveSizes[index];
        Vector3 axis = _anchor.Basis * Vector3.Right;
        for (int i = 0; i < count; i++)
        {
            float x = (i - (count - 1) * 0.5f) * 9.0f;
            Vector3 world = _anchor * new Vector3(x, 0, -_rng.RandfRange(24.0f, 32.0f));
            TrainingActor enemy = Session.SpawnActor(TrainingActor.FactionType.Enemy, world, "Bot", a =>
            {
                a.Mode = TrainingActor.BehaviorType.Strafe;
                a.Anchor = world;
                a.Axis = axis;
                a.Amplitude = 3.5f;
                a.Period = _rng.RandfRange(2.6f, 3.6f);
                a.PhaseOffset = _rng.RandfRange(0.0f, Mathf.Tau);
                a.CanShoot = true;
                a.Accuracy = 0.35f;
                a.ShotDamage = 4.0f;
                a.FireInterval = 1.1f;
                a.TellSeconds = 0.8f;
                a.ShootRange = 80.0f;
            });
            enemy.Died += OnEnemyDied;
            _enemies.Add(enemy);
        }
    }

    private void OnEnemyDied(TrainingActor actor, ShotInfo? shot)
    {
        if (shot.HasValue) _kills++;
        foreach (TrainingActor e in _enemies)
            if (!e.IsDead) return;
        _enemies.Clear();
        _waveActive = false;
        _pause = 6.0f;
        Session.Toast("Wave clear - reload if you need to", new Color(0.7f, 0.95f, 1.0f), 2.2f);
    }

    protected override float ComputeRating()
    {
        TrainingStats s = Session.Stats;
        float kills = _total > 0 ? (float)_kills / _total : 0.0f;
        float discipline = 1.0f - Mathf.Min(1.0f, (s.BadReloads + s.DryFires) / 4.0f);
        float safety = Mathf.Clamp(1.0f - s.DamageTaken / 100.0f, 0.0f, 1.0f);
        return 100.0f * (0.4f * kills + 0.45f * discipline + 0.15f * safety);
    }

    protected override void BuildResults()
    {
        TrainingStats s = Session.Stats;
        Row("Kills", $"{_kills} / {_total}");
        Row("Reloads", s.Reloads.ToString());
        Row("Reloads under fire", s.BadReloads.ToString());
        Row("Dry fires (caught empty)", s.DryFires.ToString());
        Row("Damage taken", $"{s.DamageTaken:0}");
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        TrainingStats s = Session.Stats;
        if (s.BadReloads > 0) tips.Add("You reloaded while an enemy had eyes on you. Break line of sight or finish the target first, then reload.");
        if (s.DryFires > 0) tips.Add("You pulled the trigger on an empty gun. Reload the moment a fight pauses - a reload costs seconds you do not have mid-fight.");
        if (s.Reloads == 0) tips.Add("You never reloaded. Lulls between waves are free reload time: use them.");
        if (tips.Count == 0) tips.Add("Clean ammo management. That is what wins long fights.");
        return tips.ToArray();
    }
}
