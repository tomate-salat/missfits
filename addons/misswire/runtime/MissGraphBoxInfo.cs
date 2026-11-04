using Godot;
using Misscore;

namespace Misswire;

/// <summary>
/// Where the graph editor shows a node, or the resource a node's property holds, and which of its
/// members it shows besides the ones in use. Only the editor reads this; the game ignores it.
/// <para>
/// A node can have more than one box, so that wires need not cross the whole graph: the first is
/// its main box, with an empty <see cref="Id"/>; every further one has an id of its own, which the
/// bindings wired to it name (<see cref="MissBinding.SourceBox"/>, <see cref="MissBindingStep.TargetBox"/>).
/// </para>
/// </summary>
[GlobalClass, Tool]
public partial class MissGraphBoxInfo : MissResource {
    /// <summary>Empty for a node's main box, set for each further box of the same node.</summary>
    [Export]
    public string Id { get; set; } = "";

    /// <summary>The node, relative to the mediator.</summary>
    [Export]
    public NodePath Node { get; set; } = new();

    /// <summary>
    /// Empty for the node itself, or the property whose resource the box stands for — a path for
    /// a resource inside a resource (<c>Outfit:Material</c>).
    /// </summary>
    [Export]
    public StringName Property { get; set; } = new();

    [Export]
    public Vector2 Position { get; set; }

    /// <summary>Whether the box shows everything the script declares, or only what is wired or added by hand.</summary>
    [Export]
    public bool ShowDeclared { get; set; } = true;

    /// <summary>Members added by hand (<c>kind:name</c>), shown even while nothing uses them.</summary>
    [Export]
    public Godot.Collections.Array<StringName> Rows { get; set; } = [];
}
