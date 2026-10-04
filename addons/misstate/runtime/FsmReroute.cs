using Godot;
using Misscore;

namespace Misstate;

/// <summary>
/// A waypoint for wires in a graph editor: transitions may lead to it instead of to a state, and it
/// leads on to a state or to another reroute. It does nothing at runtime — a transition that leads
/// to a reroute goes straight to the state at the end of the chain.
/// </summary>
[GlobalClass, Tool]
public partial class FsmReroute : MissResource {
    /// <summary>Stable identity, which is what transitions and other reroutes refer to.</summary>
    [Export]
    public string Id { get; set; } = BlackboardEntry.NewId();

    /// <summary>Where it leads on, by id: a state or another reroute.</summary>
    [Export]
    public string TargetId { get; set; } = "";

    /// <summary>Authored position in a graph editor.</summary>
    [Export]
    public Vector2 GraphPosition { get; set; }

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(Id) or nameof(TargetId) or nameof(GraphPosition)) {
            property["usage"] = (int) PropertyUsageFlags.Storage;
        }
    }
}
