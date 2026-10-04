using System.Collections.Generic;
using System.Linq;
using Godot;
using Missbehave;
using Misscore;
using Misstate;
using Misstate.Tests;

namespace MissfitsTests;

/// <summary>
/// What the addons can do together. No addon may know another one, so this lives with the
/// development project rather than in any of them.
/// <code>
/// godot --headless --path &lt;project&gt; res://tests/integration_test.tscn
/// </code>
/// </summary>
public partial class IntegrationSelfTest : Node {
    const double Step = 0.05;

    readonly List<string> _failures = [];
    int _checks;

    public override void _Ready() {
        AStateCanRunABehaviorSubtree();
        LeavingAStateInterruptsTheSubtree();
        ATransitionCanCheckAConditionList();

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"missfits integration test: {_checks - _failures.Count}/{_checks} checks passed");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    void AStateCanRunABehaviorSubtree() {
        var first = new FsmProbeAction { RunningTicks = 1 };
        var second = new FsmProbeAction();
        var combo = new FsmState { Name = "Combo", Node = Composite<SequenceNode>(first, second) };
        var done = new FsmState { Name = "Done" };
        combo.Transitions.Add(new FsmTransition { TargetStateId = done.Id, On = FsmTrigger.Succeeded });

        var instance = FsmInstance.Create(Machine(combo, done));
        var ctx = new MissContext { Blackboard = new Blackboard(), Delta = Step };

        instance.Tick(ctx);
        var sequence = instance.NodeOf(combo);
        var a = (FsmProbeAction) sequence.Children[0];
        var b = (FsmProbeAction) sequence.Children[1];
        Check("a state runs a copy of a whole subtree", sequence is SequenceNode && !ReferenceEquals(a, first) && first.TotalTicks == 0);
        Check("the sequence waits on its running child", instance.Current == combo && a.TotalTicks == 1 && b.TotalTicks == 0);

        instance.Tick(ctx);
        Check("and carries on to the next one", a.TotalTicks == 2 && b.TotalTicks == 1);
        Check("the state is left once the subtree succeeded", instance.Current == done);
    }

    void LeavingAStateInterruptsTheSubtree() {
        var busy = new FsmState { Name = "Busy", Node = Composite<SequenceNode>(new FsmProbeAction { RunningTicks = 100 }) };
        var next = new FsmState { Name = "Next" };
        busy.Transitions.Add(new FsmTransition { TargetStateId = next.Id, Condition = new FsmProbeCondition() });

        var instance = FsmInstance.Create(Machine(busy, next));
        instance.Tick(new MissContext { Blackboard = new Blackboard(), Delta = Step });

        var running = (FsmProbeAction) instance.NodeOf(busy).Children[0];
        Check("leaving a state interrupts the child its subtree left running", instance.Current == next && running.Interrupts == 1);
    }

    void ATransitionCanCheckAConditionList() {
        var holds = new FsmProbeCondition();
        var fails = new FsmProbeCondition { AtLeast = 1 };

        FsmState Arrive(ListMode mode) {
            var list = Composite<ConditionListNode>((MissNode) holds.Duplicate(false), (MissNode) fails.Duplicate(false));
            list.Mode = mode;
            var from = new FsmState { Name = "From" };
            var to = new FsmState { Name = "To" };
            from.Transitions.Add(new FsmTransition { TargetStateId = to.Id, Condition = list });
            var instance = FsmInstance.Create(Machine(from, to));
            instance.Tick(new MissContext { Blackboard = new Blackboard(), Delta = Step });
            return instance.Current;
        }

        Check("a condition list that needs all of them does not fire", Arrive(ListMode.Sequence).Name == "From");
        Check("one that needs any of them does", Arrive(ListMode.Selector).Name == "To");
    }

    static T Composite<T>(params MissNode[] children) where T : MissNode, new() {
        var node = new T();
        foreach (var child in children) node.Children.Add(child);
        return node;
    }

    static Fsm Machine(params FsmState[] states) {
        var machine = new Fsm();
        foreach (var state in states) machine.States.Add(state);
        return machine;
    }

    void Check(string what, bool condition) {
        _checks++;
        if (!condition) _failures.Add(what);
    }
}