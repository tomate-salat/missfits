using Godot;
using Misscore;

namespace Misstate;

/// <summary>
/// One state of a machine: the actions that run while the machine is in it, and the ways out.
/// <para>
/// The actions are a list, worked through like a behavior tree's action list: one after the other
/// by default, or all at once when <see cref="Parallel"/> is set. A run is started when the state
/// is entered and interrupted if the state is left mid-run; when it finishes and no transition
/// fires, it starts over on the next tick.
/// </para>
/// </summary>
[GlobalClass, Tool]
public partial class FsmState : MissResource {
    /// <summary>Stable identity, which is what transitions refer to.</summary>
    [Export]
    public string Id { get; set; } = BlackboardEntry.NewId();

    [Export]
    public string Name { get; set; } = "";

    /// <summary>
    /// When a run of the actions is over: as a sequence it fails with the first action that fails
    /// and succeeds once all have succeeded; as a selector it succeeds with the first that succeeds
    /// and fails once all have failed.
    /// </summary>
    [Export]
    public ListMode Mode { get; set; } = ListMode.Sequence;

    /// <summary>
    /// Off, the actions run one after the other, each waiting for the one before it. On, all of
    /// them are ticked on every tick; the one that decides the run interrupts the others.
    /// </summary>
    [Export]
    public bool Parallel { get; set; }

    /// <summary>
    /// What the state does, top to bottom: <see cref="ActionNode"/>s as a rule, but any node will
    /// do. May be empty for a state that only waits.
    /// </summary>
    [Export]
    public Godot.Collections.Array<MissNode> Actions { get; set; } = [];

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