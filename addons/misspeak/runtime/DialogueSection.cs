using Godot;
using Misscore;

namespace Misspeak;

/// <summary>
/// A stretch of dialogue that runs straight through: <see cref="Lines"/> spoken one after the other,
/// and at the end the <see cref="Options"/> — the ways on to other sections. In a graph this is one
/// box, so wires are only needed where a dialogue branches or jumps, not between every two lines.
/// <para>
/// The choices among the options are offered together with the section's last line. With no choice
/// on offer, the player carries on past that line and the dialogue takes the first option that is
/// no choice. A section with no way on ends the dialogue. A section in which nothing is said — no
/// lines, or only silent ones — moves on by itself, which makes it a branch.
/// </para>
/// </summary>
[GlobalClass, Tool]
public partial class DialogueSection : MissResource {
    /// <summary>Stable identity, which is what options refer to.</summary>
    [Export]
    public string Id { get; set; } = BlackboardEntry.NewId();

    /// <summary>What the section is called in an editor and in <see cref="DialogueRunner.Start"/>. Not shown to the player.</summary>
    [Export]
    public string Name { get; set; } = "";

    /// <summary>What is said and done, top to bottom.</summary>
    [Export]
    public Godot.Collections.Array<DialogueLine> Lines { get; set; } = [];

    /// <summary>The ways on, considered top to bottom once the lines are through.</summary>
    [Export]
    public Godot.Collections.Array<DialogueOption> Options { get; set; } = [];

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
