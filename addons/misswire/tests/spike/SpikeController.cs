using Godot;

namespace Misswire.Tests;

/// <summary>A target that has to act in the editor, so it is <c>[Tool]</c>, but holds no connections.</summary>
[Tool]
public partial class SpikeController : Node {
    [Export] public SpikeOutfit Outfit { get; set; }
    [Export] public int AppliedCount { get; set; }

    public void ApplyOutfit() => AppliedCount++;
}
