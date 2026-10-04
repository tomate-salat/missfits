using Godot;
using Misscore;

namespace Misspeak;

/// <summary>
/// A way on from a <see cref="DialogueSection"/>. With a <see cref="Text"/> it is a choice offered
/// to the player; without one it is simply where the dialogue goes next. Either way it only counts
/// while its <see cref="Conditions"/> hold.
/// </summary>
[GlobalClass, Tool]
public partial class DialogueOption : MissResource {
    /// <summary>Stable identity, by which an editor tells the options of a section apart.</summary>
    [Export]
    public string Id { get; set; } = BlackboardEntry.NewId();

    /// <summary>
    /// What the player picks, e.g. "Tell me more." Left empty, the option is no choice but the way
    /// the dialogue continues by itself. May contain <c>{entry}</c> placeholders, like a line's text.
    /// </summary>
    [Export(PropertyHint.MultilineText)]
    public string Text { get; set; } = "";

    /// <summary>The section to go to, by id. Left empty, taking the option ends the dialogue.</summary>
    [Export]
    public string TargetSectionId { get; set; } = "";

    /// <summary>
    /// Whether all of the <see cref="Conditions"/> have to hold (<see cref="ListMode.Sequence"/>) or
    /// one is enough (<see cref="ListMode.Selector"/>).
    /// </summary>
    [Export]
    public ListMode Mode { get; set; } = ListMode.Sequence;

    /// <summary>
    /// What has to hold for the option to count, checked when the section's lines are through:
    /// usually <see cref="ConditionNode"/>s, but any node will do — it holds when it returns Success.
    /// </summary>
    [Export]
    public Godot.Collections.Array<MissNode> Conditions { get; set; } = [];

    /// <summary>Whether this is a choice for the player rather than the way on by itself.</summary>
    public bool IsChoice => !string.IsNullOrEmpty(Text);

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(Id) or nameof(TargetSectionId)) property["usage"] = (int) PropertyUsageFlags.Storage;
    }
}
