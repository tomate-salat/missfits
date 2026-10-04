using Godot;
using Misscore;

namespace Misspeak;

/// <summary>
/// One line of a <see cref="DialogueSection"/>: what happens, and what is said.
/// <para>
/// On reaching a line its <see cref="Actions"/> run first, one after the other, each to its end —
/// the same actions a behavior tree or a state machine runs. Then the <see cref="Text"/> is shown
/// and the dialogue waits for the player. A line without text shows nothing and just runs its
/// actions. A line whose <see cref="Conditions"/> do not hold is skipped altogether.
/// </para>
/// </summary>
[GlobalClass, Tool]
public partial class DialogueLine : MissResource {
    /// <summary>Stable identity, by which an editor tells the lines of a section apart.</summary>
    [Export]
    public string Id { get; set; } = BlackboardEntry.NewId();

    /// <summary>Who says it. Free text; what it means is up to whatever shows the dialogue.</summary>
    [Export]
    public string Speaker { get; set; } = "";

    /// <summary>
    /// What is said. <c>{entry}</c> is replaced by the value of the blackboard entry of that name.
    /// Left empty, the line is silent.
    /// </summary>
    [Export(PropertyHint.MultilineText)]
    public string Text { get; set; } = "";

    /// <summary>
    /// What happens on reaching the line, before its text is shown: <see cref="ActionNode"/>s as a
    /// rule, but any node will do. They run top to bottom, each until it no longer returns Running;
    /// whether one succeeds or fails makes no difference.
    /// </summary>
    [Export]
    public Godot.Collections.Array<MissNode> Actions { get; set; } = [];

    /// <summary>
    /// Whether all of the <see cref="Conditions"/> have to hold (<see cref="ListMode.Sequence"/>) or
    /// one is enough (<see cref="ListMode.Selector"/>).
    /// </summary>
    [Export]
    public ListMode Mode { get; set; } = ListMode.Sequence;

    /// <summary>
    /// What has to hold for the line to be part of the dialogue at all, checked when it is its turn.
    /// Left empty, it always is.
    /// </summary>
    [Export]
    public Godot.Collections.Array<MissNode> Conditions { get; set; } = [];

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(Id)) property["usage"] = (int) PropertyUsageFlags.Storage;
    }

    public override string ToString() => string.IsNullOrEmpty(Speaker) ? Text : $"{Speaker}: {Text}";
}
