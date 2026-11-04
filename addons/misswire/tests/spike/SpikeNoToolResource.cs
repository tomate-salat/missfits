using Godot;

namespace Misswire.Tests;

/// <summary>A resource without <c>[Tool]</c>, held by a placeholder.</summary>
public partial class SpikeNoToolResource : Resource {
    [Export] public int Value { get; set; } = 1;
}
