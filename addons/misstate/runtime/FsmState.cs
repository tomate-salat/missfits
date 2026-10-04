using Godot;
using Misscore;

namespace Misstate;

/// <summary>
/// One state of a machine: what runs while the machine is in it, and the ways out.
/// <para>
/// <see cref="Node"/> runs the way a behavior tree's root does. It is started when the state is
/// entered, ticked while the state lasts and interrupted if the state is left mid-run; when it
/// finishes and no transition fires, it starts over on the next tick.
/// </para>
/// </summary>
[GlobalClass, Tool]
public partial class FsmState : Resource {
    /// <summary>Stable identity, which is what transitions refer to.</summary>
    [Export]
    public string Id { get; set; } = BlackboardEntry.NewId();

    [Export]
    public string Name { get; set; } = "";

    /// <summary>
    /// What the state does: an <see cref="ActionNode"/>, or any other node — a whole subtree, where a
    /// behavior tree addon provides the composites. May be empty for a state that only waits.
    /// </summary>
    [Export]
    public MissNode Node { get; set; }

    /// <summary>The ways out, considered top to bottom after each tick of the state.</summary>
    [Export]
    public Godot.Collections.Array<FsmTransition> Transitions { get; set; } = [];

    /// <summary>Authored position in a graph editor. Storage only.</summary>
    [Export]
    public Vector2 GraphPosition { get; set; }

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(Id) or nameof(GraphPosition)) {
            property["usage"] = (int) PropertyUsageFlags.Storage;
        }
    }

    public override string ToString() => Name;
}