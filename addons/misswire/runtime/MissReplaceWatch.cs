using Godot;
using Misscore;

namespace Misswire;

/// <summary>
/// Signals wired by hand that say a node's property holds another resource now, such as
/// <c>UpdateOutfit</c> for <c>Outfit</c>: bindings on that resource let go of the old one and
/// listen to the new one. misswire also finds such signals itself (see
/// <see cref="MissWiring.ReplaceSignals"/>); these are the ones it cannot.
/// </summary>
[GlobalClass, Tool]
public partial class MissReplaceWatch : MissResource {
    /// <summary>The node, relative to the mediator.</summary>
    [Export]
    public NodePath Node { get; set; } = new();

    /// <summary>The property holding the resource — a path for a resource inside a resource.</summary>
    [Export]
    public StringName Property { get; set; } = new();

    /// <summary>The signals, as paths (<c>../Model:UpdateOutfit</c>).</summary>
    [Export]
    public Godot.Collections.Array<NodePath> Signals { get; set; } = [];
}
