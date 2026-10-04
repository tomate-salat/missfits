using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;

namespace Misstate;

/// <summary>
/// An authored state machine, saved as a single <c>.tres</c>. This is a definition: it is never run
/// directly, and it may be assigned to any number of runners, each of which works on its own copy.
/// </summary>
[GlobalClass, Tool]
public partial class Fsm : MissResource, IBlackboardSource {
    [Export]
    public Godot.Collections.Array<FsmState> States { get; set; } = [];

    /// <summary>Waypoints for the wires of a graph editor. See <see cref="Destination"/>.</summary>
    [Export]
    public Godot.Collections.Array<FsmReroute> Reroutes { get; set; } = [];

    /// <summary>The state a runner starts in. Left empty, it is the first state.</summary>
    [Export]
    public string InitialStateId { get; set; } = "";

    [Export(PropertyHint.MultilineText)]
    public string Description { get; set; } = "";

    /// <summary>
    /// The values this machine works with; the actions and conditions link to entries by id through
    /// <see cref="BbParam{T}"/>.
    /// </summary>
    [Export]
    public Godot.Collections.Array<BlackboardEntry> Blackboard { get; set; } = [];

    public FsmState InitialState => FindState(InitialStateId) ?? States.FirstOrDefault(s => s != null);

    public FsmState FindState(string id) {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var state in States) {
            if (state?.Id == id) return state;
        }
        return null;
    }

    public FsmReroute FindReroute(string id) {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var reroute in Reroutes) {
            if (reroute?.Id == id) return reroute;
        }
        return null;
    }

    /// <summary>
    /// The state a transition's target stands for: the state of that id, or the one at the end of
    /// the reroutes that start there. Null when the id names nothing, or the reroutes end nowhere
    /// or run in a circle.
    /// </summary>
    public FsmState Destination(string targetId) {
        for (var hops = 0; hops <= Reroutes.Count; hops++) {
            if (FindState(targetId) is { } state) return state;
            if (FindReroute(targetId) is not { } reroute) return null;
            targetId = reroute.TargetId;
        }
        return null;
    }

    public FsmState FindStateByName(string name) {
        foreach (var state in States) {
            if (state != null && state.Name == name) return state;
        }
        return null;
    }

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(Blackboard) or nameof(InitialStateId) or nameof(Reroutes)) {
            property["usage"] = (int) PropertyUsageFlags.Storage;
        }
    }

    /// <summary>Every node the machine holds: what its states run and what its transitions check.</summary>
    public IEnumerable<MissNode> AllNodes() {
        foreach (var state in States) {
            if (state == null) continue;
            foreach (var action in state.Actions) {
                foreach (var node in Walk(action)) yield return node;
            }
            foreach (var transition in state.Transitions) {
                if (transition == null) continue;
                foreach (var condition in transition.Conditions) {
                    foreach (var node in Walk(condition)) yield return node;
                }
            }
        }
    }

    static IEnumerable<MissNode> Walk(MissNode node) {
        if (node == null) yield break;
        yield return node;
        foreach (var child in node.Children) {
            foreach (var descendant in Walk(child)) yield return descendant;
        }
    }

    IEnumerable<IBbParamHost> IBlackboardSource.ParamHosts() => AllNodes();

    /// <summary>
    /// Gives every node that has none an id. The blackboard editor tells nodes apart by it, and a node
    /// created in the Inspector starts without one.
    /// </summary>
    public void EnsureNodeIds() {
        foreach (var node in AllNodes()) node.EnsureId();
    }

    public string[] Validate() {
        var problems = new List<string>();
        if (!States.Any(s => s != null)) {
            problems.Add("The machine has no states.");
            return [.. problems];
        }
        if (!string.IsNullOrEmpty(InitialStateId) && FindState(InitialStateId) == null) {
            problems.Add("The initial state no longer exists.");
        }

        foreach (var state in States) {
            if (state == null) continue;
            var name = string.IsNullOrEmpty(state.Name) ? "(unnamed state)" : state.Name;
            if (States.Count(s => s != null && s.Name == state.Name) > 1) problems.Add($"{name}: the name is used by more than one state.");

            for (var i = 0; i < state.Transitions.Count; i++) {
                var transition = state.Transitions[i];
                if (transition == null) problems.Add($"{name}: transition #{i + 1} is empty.");
                else if (Destination(transition.TargetStateId) == null) problems.Add($"{name}: transition #{i + 1} leads nowhere.");
                else if (transition.On == FsmTrigger.Always && !transition.Conditions.Any(c => c != null)) {
                    problems.Add($"{name}: transition #{i + 1} has neither a trigger nor a condition, so the state is left after one tick.");
                }
            }
        }

        foreach (var node in AllNodes()) {
            foreach (var warning in node.GetConfigurationWarnings()) problems.Add($"{node.GetLabel()}: {warning}");
            foreach (var problem in LinkProblems(node)) problems.Add($"{node.GetLabel()}: {problem}");
        }
        return [.. problems];
    }

    /// <summary>Parameters of <paramref name="node"/> linked to entries that are gone or of the wrong type.</summary>
    public IEnumerable<string> LinkProblems(MissNode node) {
        if (node == null) yield break;
        foreach (var (member, param) in node.BlackboardParams()) {
            if (!param.IsLinked) continue;

            var entry = this.FindEntry(param.EntryId);
            if (entry == null) {
                yield return $"{member.Name} is linked to a blackboard entry that no longer exists ({param.EntryName})";
            }
            else if (!BbTypes.Accepts(member.ValueType, entry)) {
                var expected = BbTypes.Describe(member.ValueType);
                yield return $"{member.Name} expects {BbTypes.Label(expected.VariantType, expected.ClassName)}, " +
                             $"but {entry.Name} is {entry.TypeLabel}";
            }
        }
    }
}