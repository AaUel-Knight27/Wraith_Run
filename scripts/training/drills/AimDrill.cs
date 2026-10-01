using Godot;
using System.Collections.Generic;

/// <summary>Four aim drills in one: flick (react and snap), precision (heads only), tracking (stay on a mover) and
/// recoil control (spray at a wall, keep the group tight). Uses whatever weapon you hold, except recoil (AK-47).</summary>
public partial class AimDrill : TrainingDrill
{
    public enum Mode { Flick, Precision, Tracking, Recoil }

    private const int FlickTotal = 20;
    private const int PrecisionTotal = 12;
    private const float TrackingSeconds = 30.0f;
    private const float RecoilSeconds = 28.0f;

    private Mode _mode = Mode.Flick;
    private Transform3D _anchor;
    private readonly RandomNumberGenerator _rng = new();
    private TrainingTarget? _current;
    private int _spawned;
    private int _resolved;
    private int _killed;
    private int _headKills;
    private float _nextSpawnAt;
    private Vector3 _wallCenter;

    public AimDrill() { }
    public AimDrill(Mode mode) { _mode = mode; }

    private int Total => _mode == Mode.Flick ? FlickTotal : PrecisionTotal;

    public override string Objective => _mode switch
    {
        Mode.Flick => "Targets pop up one at a time and vanish fast. Snap to each and drop it before it disappears.",
        Mode.Precision => "Headshots only pay here. Take your time on each target - a body shot is a wasted opportunity.",
        Mode.Tracking => "Keep your crosshair glued to the moving target and fire. Hits over shots is your score.",
        _ => "Fire long bursts at the wall. Pull down against the recoil to keep every hit near the centre mark.",
    };

    public override string StatusLine => _mode switch
    {
        Mode.Flick => $"Down {_killed}/{FlickTotal}   accuracy {Percent(Session.Stats.Accuracy)}",
        Mode.Precision => $"Down {_killed}/{PrecisionTotal}   headshots {Session.Stats.Headshots}   ({Percent(Session.Stats.HeadshotRate)})",
        Mode.Tracking => $"Time left {Mathf.Max(0.0f, TrackingSeconds - Elapsed):0}s   accuracy {Percent(Session.Stats.Accuracy)}",
        _ => $"Shots {Session.Stats.Shots}   time left {Mathf.Max(0.0f, RecoilSeconds - Elapsed):0}s",
    };

    protected override void OnBegin()
    {
        _rng.Randomize();
        _anchor = Session.Anchor(TrainingAnchors.AimRange);
        Session.PlacePlayer(_anchor);

        if (_mode == Mode.Recoil)
        {
            Session.Weapons.EquipByName("AK-47");
            _wallCenter = _anchor * new Vector3(0, 1.5f, -20.0f);
            Session.SpawnTarget(_anchor * new Vector3(0, 0, -20.0f), t =>
            {
                t.IsWall = true;
                t.Indestructible = true;
                t.WallSize = new Vector2(3.0f, 3.0f);
            });
        }
        else if (_mode == Mode.Tracking)
        {
            Session.SpawnTarget(_anchor * new Vector3(0, 0, -22.0f), t =>
            {
                t.Indestructible = true;
                t.Movement = TrainingTarget.MoveMode.Strafe;
                t.Axis = _anchor.Basis * Vector3.Right;
                t.Amplitude = 6.0f;
                t.Period = 2.8f;
            });
        }
    }

    protected override void OnLive()
    {
        _nextSpawnAt = 0.3f;
    }

    protected override void OnTick(float dt)
    {
        switch (_mode)
        {
            case Mode.Flick:
            case Mode.Precision:
                if (_current == null && _spawned < Total && Elapsed >= _nextSpawnAt) SpawnNext();
                if (_resolved >= Total) End();
                break;
            case Mode.Tracking:
                if (Elapsed >= TrackingSeconds) End();
                break;
            case Mode.Recoil:
                if (Elapsed >= RecoilSeconds || Session.Stats.Shots >= 90) End();
                break;
        }
    }

    private void SpawnNext()
    {
        _spawned++;
        bool precision = _mode == Mode.Precision;
        float x = _rng.RandfRange(-13.0f, 13.0f);
        float z = -_rng.RandfRange(precision ? 18.0f : 12.0f, precision ? 38.0f : 30.0f);
        _current = Session.SpawnTarget(_anchor * new Vector3(x, 0, z), t =>
        {
            t.Lifetime = precision ? 3.2f : 1.9f;
            t.Killed += OnTargetKilled;
            t.Expired += OnTargetExpired;
        });
    }

    private void OnTargetKilled(TrainingTarget target, ShotInfo shot)
    {
        _killed++;
        if (shot.IsHeadshot) _headKills++;
        Resolve();
    }

    private void OnTargetExpired(TrainingTarget target) => Resolve();

    private void Resolve()
    {
        _current = null;
        _resolved++;
        _nextSpawnAt = Elapsed + 0.4f;
    }

    private void End()
    {
        float rating = ComputeRating();
        bool pass = rating >= 50.0f;
        Finish(pass, pass ? "Nice work." : "Not enough yet - the coach notes below show where to improve.");
    }

    protected override float ComputeRating()
    {
        TrainingStats stats = Session.Stats;
        switch (_mode)
        {
            case Mode.Flick:
            {
                float kill = (float)_killed / FlickTotal;
                float reaction = stats.ReactionTimes.Count == 0 ? 0.0f : Mathf.Clamp(1.0f - (stats.AverageReaction - 0.5f) / 1.0f, 0.0f, 1.0f);
                float accuracy = Mathf.Min(1.0f, stats.Accuracy / 0.7f);
                return 100.0f * (0.55f * kill + 0.30f * reaction + 0.15f * accuracy);
            }
            case Mode.Precision:
            {
                float kill = (float)_killed / PrecisionTotal;
                float head = Mathf.Min(1.0f, stats.HeadshotRate / 0.6f);
                return 100.0f * (0.4f * kill + 0.6f * head);
            }
            case Mode.Tracking:
            {
                float accuracy = Mathf.Min(1.0f, stats.Accuracy / 0.55f);
                float volume = Mathf.Min(1.0f, stats.Shots / 25.0f);
                return 100.0f * accuracy * volume;
            }
            default:
            {
                if (stats.Pellets == 0) return 0.0f;
                int tight = 0;
                foreach (Vector3 point in stats.HitPoints)
                    if (point.DistanceTo(_wallCenter) <= 0.7f) tight++;
                float tightFraction = (float)tight / stats.Pellets;
                float volume = Mathf.Min(1.0f, stats.Pellets / 30.0f);
                return 100.0f * Mathf.Min(1.0f, tightFraction / 0.75f) * volume;
            }
        }
    }

    protected override void BuildResults()
    {
        TrainingStats s = Session.Stats;
        Row("Shots fired", s.Shots.ToString());
        Row("Accuracy", Percent(s.Accuracy));
        switch (_mode)
        {
            case Mode.Flick:
                Row("Targets down", $"{_killed} / {FlickTotal}");
                Row("Average first-hit time", s.ReactionTimes.Count > 0 ? $"{s.AverageReaction:0.00} s" : "-");
                break;
            case Mode.Precision:
                Row("Targets down", $"{_killed} / {PrecisionTotal}");
                Row("Headshot rate", Percent(s.HeadshotRate));
                Row("Headshot kills", _headKills.ToString());
                break;
            case Mode.Tracking:
                Row("Hits", s.Hits.ToString());
                break;
            default:
            {
                int tight = 0;
                foreach (Vector3 point in s.HitPoints)
                    if (point.DistanceTo(_wallCenter) <= 0.7f) tight++;
                Row("Hits within 0.7 m of centre", $"{tight} / {s.Pellets}");
                Row("Average drift from centre", $"{AverageDrift(s):0.00} m");
                break;
            }
        }
    }

    private float AverageDrift(TrainingStats stats)
    {
        if (stats.HitPoints.Count == 0) return 0.0f;
        float sum = 0.0f;
        foreach (Vector3 point in stats.HitPoints) sum += point.DistanceTo(_wallCenter);
        return sum / stats.HitPoints.Count;
    }

    public override string[] CoachingTips()
    {
        var tips = new List<string>();
        TrainingStats s = Session.Stats;
        switch (_mode)
        {
            case Mode.Flick:
                if (s.ReactionTimes.Count > 0 && s.AverageReaction > 1.0f) tips.Add($"Your first hit took {s.AverageReaction:0.0} s. Park the crosshair at head height in the middle of the lane so any target is a short flick away.");
                if (s.Shots > 0 && s.Accuracy < 0.4f) tips.Add("Under 40% accuracy: stop spraying. Land the first shot, then fire.");
                break;
            case Mode.Precision:
                if (s.Hits > 0 && s.HeadshotRate < 0.4f) tips.Add("Aim at the top of the silhouette, not its centre. Headshot damage is much higher than body damage.");
                break;
            case Mode.Tracking:
                if (s.Shots > 0 && s.Accuracy < 0.4f) tips.Add("Match the target's speed with your mouse instead of chasing it. Lead a little in its direction of travel.");
                if (s.Shots < 25) tips.Add("You barely fired. Tracking is scored on volume too - keep the trigger down while on target.");
                break;
            default:
                tips.Add("Recoil climbs and drifts. Pull the mouse down and slightly against the sideways drift as the burst continues.");
                if (AverageDrift(s) > 1.0f) tips.Add("Your group drifted over a metre. Try bursts of 5-8 rounds and reset between them.");
                break;
        }
        return tips.ToArray();
    }
}
