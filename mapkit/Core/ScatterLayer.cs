#nullable enable
using Godot;

namespace WraithRun.MapKit;

/// <summary>One entry in a ScatterLayer's weighted pool.</summary>
[Tool]
[GlobalClass]
public partial class ScatterEntry : Resource
{
    [Export] public PropDefinition? Prop { get; set; }

    /// <summary>Relative chance of this entry vs the layer's other entries.</summary>
    [Export(PropertyHint.Range, "0.01,10,0.01,or_greater")]
    public float Weight { get; set; } = 1.0f;
}

/// <summary>
/// Scatters a mix of props across ground. Used for rubble/rocks/dead trees on war-torn ground, crates
/// in a yard, grass clumps — any "many small things" layer. Density is per square metre so the same
/// layer looks right whether it is scattered over a 20x20 clearing or an 800x800 wasteland.
/// </summary>
[Tool]
[GlobalClass]
public partial class ScatterLayer : Resource
{
    [Export] public ScatterEntry[] Entries { get; set; } = System.Array.Empty<ScatterEntry>();

    [Export(PropertyHint.Range, "0,2,0.001,or_greater")]
    public float DensityPerSqm { get; set; } = 0.02f;

    /// <summary>Minimum gap between two scattered props, on top of their own footprints.</summary>
    [Export(PropertyHint.Range, "0,5,0.05")]
    public float ExtraSpacing { get; set; }

    [Export] public uint Seed { get; set; } = 1;

    public float TotalWeight()
    {
        float w = 0.0f;
        foreach (ScatterEntry e in Entries) if (e.Prop != null) w += Mathf.Max(e.Weight, 0.0001f);
        return w;
    }

    public PropDefinition? Pick(RandomNumberGenerator rng)
    {
        float total = TotalWeight();
        if (total <= 0.0f) return null;
        float roll = rng.RandfRange(0.0f, total);
        float acc = 0.0f;
        foreach (ScatterEntry e in Entries)
        {
            if (e.Prop == null) continue;
            acc += Mathf.Max(e.Weight, 0.0001f);
            if (roll <= acc) return e.Prop;
        }
        return null;
    }
}
