using Godot;
using Misscore;

namespace Misstate;

/// <summary>What has to have happened in a state for a transition out of it to be considered.</summary>
public enum FsmTrigger {
    /// <summary>Nothing: the transition is considered on every tick.</summary>
    Always,

    /// <summary>The state's actions finished on this tick, with whatever result.</summary>
    Finished,

    /// <summary>The state's actions finished on this tick with Success.</summary>
    Succeeded,

    /// <summary>The state's actions finished on this tick with Failure.</summary>
    Failed,
}

/// <summary>
/// A way out of a state: once its <see cref="On"/> trigger is met and its <see cref="Conditions"/>
/// hold, the machine moves on to <see cref="TargetStateId"/>. A state's transitions are considered
/// top to bottom and the first one that fires wins.
/// </summary>
[GlobalClass, Tool]
public partial class FsmTransition : MissResource {
    /// <summary>Stable identity, by which an editor tells the transitions of a state apart.</summary>
    [Export]
    public string Id { get; set; } = BlackboardEntry.NewId();

    /// <summary>The state to go to, by id — so renaming a state never breaks a transition.</summary>
    [Export]
    public string TargetStateId { get; set; } = "";

    [Export]
    public FsmTrigger On { get; set; } = FsmTrigger.Always;

    /// <summary>
    /// Whether all of the <see cref="Conditions"/> have to hold (<see cref="ListMode.Sequence"/>) or
    /// one is enough (<see cref="ListMode.Selector"/>).
    /// </summary>
    [Export]
    public ListMode Mode { get; set; } = ListMode.Sequence;

    /// <summary>
    /// What has to hold as well, checked afresh each time: usually <see cref="ConditionNode"/>s, but
    /// any node will do — it holds when it returns Success. Left empty, the trigger alone decides.
    /// </summary>
    [Export]
    public Godot.Collections.Array<MissNode> Conditions { get; set; } = [];

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(Id) or nameof(TargetStateId)) property["usage"] = (int) PropertyUsageFlags.Storage;
    }
}