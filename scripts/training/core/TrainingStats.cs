using Godot;
using System.Collections.Generic;

/// <summary>
/// Live counters for the current drill, fed by the session from WeaponSwitcher's events and the targets'
/// hit reports. Drills read these to score; the results screen prints them; the coaching notes explain them.
/// </summary>
public sealed class TrainingStats
{
    public int Shots;              // trigger pulls
    public int Pellets;            // projectiles fired (shotgun = many per pull)
    public int Hits;               // projectiles that hit a shootable
    public int Headshots;
    public int Kills;
    public int Reloads;
    public int DryFires;           // pulled the trigger on an empty magazine
    public int BadReloads;         // reloads started while an enemy was actively engaging you
    public float DamageTaken;
    public float DamageDealt;
    public int FriendlyHits;       // shots that hit a no-shoot / friendly target
    public readonly List<float> ReactionTimes = new();   // exposure -> first hit, seconds
    public readonly List<Vector3> HitPoints = new();     // world hit points (recoil analysis)

    public float Accuracy => Pellets > 0 ? (float)Hits / Pellets : 0.0f;
    public float HeadshotRate => Hits > 0 ? (float)Headshots / Hits : 0.0f;
    public float AverageReaction
    {
        get
        {
            if (ReactionTimes.Count == 0) return 0.0f;
            float sum = 0.0f;
            foreach (float t in ReactionTimes) sum += t;
            return sum / ReactionTimes.Count;
        }
    }

    public void Reset()
    {
        Shots = Pellets = Hits = Headshots = Kills = Reloads = DryFires = BadReloads = FriendlyHits = 0;
        DamageTaken = DamageDealt = 0.0f;
        ReactionTimes.Clear();
        HitPoints.Clear();
    }
}
