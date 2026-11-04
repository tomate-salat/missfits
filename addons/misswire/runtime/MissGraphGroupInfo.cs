using Godot;
using Misscore;

namespace Misswire;

/// <summary>
/// A frame around some boxes of the graph editor, with a title and a colour, that moves them
/// together. Only the editor reads this; the game ignores it.
/// </summary>
[GlobalClass, Tool]
public partial class MissGraphGroupInfo : MissResource {
    [Export]
    public string Id { get; set; } = BlackboardEntry.NewId();

    [Export]
    public string Title { get; set; } = "Group";

    [Export]
    public Color Color { get; set; } = new(0.35f, 0.45f, 0.6f);

    /// <summary>The boxes inside, by the names the graph editor gives them.</summary>
    [Export]
    public Godot.Collections.Array<string> Members { get; set; } = [];

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(Id) or nameof(Members)) property["usage"] = (int) PropertyUsageFlags.Storage;
    }
}
