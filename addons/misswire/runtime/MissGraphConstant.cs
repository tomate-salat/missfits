using Godot;
using Misscore;

namespace Misswire;

/// <summary>
/// A fixed value in the graph, as a box of its own: steps take it through their value port.
/// Several steps can share one.
/// </summary>
[GlobalClass, Tool]
public partial class MissGraphConstant : MissResource {
    [Export]
    public string Id { get; set; } = BlackboardEntry.NewId();

    [Export]
    public Variant Value { get; set; } = "";

    /// <summary>Where the graph editor shows the box. The game ignores it.</summary>
    [Export]
    public Vector2 GraphPosition { get; set; }

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(Id) or nameof(GraphPosition)) property["usage"] = (int) PropertyUsageFlags.Storage;
    }
}
