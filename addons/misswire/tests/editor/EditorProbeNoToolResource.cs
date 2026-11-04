using Godot;

namespace Misswire.Tests;

/// <summary>A resource without <c>[Tool]</c>: it cannot say when its inside changes.</summary>
public partial class EditorProbeNoToolResource : Resource {
    [Export] public int Value { get; set; }
}
