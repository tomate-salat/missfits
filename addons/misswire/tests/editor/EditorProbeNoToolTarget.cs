using Godot;

namespace Misswire.Tests;

/// <summary>A target without <c>[Tool]</c>: calls cannot reach it in the editor, setting can.</summary>
public partial class EditorProbeNoToolTarget : Node {
    [Export] public int Seen { get; set; }

    public void Poke() { }
}
