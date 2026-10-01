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
