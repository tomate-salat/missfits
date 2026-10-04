using System.Collections.Generic;
using System.Linq;
using Godot;
using Missbehave;
using Misscore;
using Misspeak;
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
        AStateMachineTalks();
        ABehaviorTreeTalks();

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"missfits integration test: {_checks - _failures.Count}/{_checks} checks passed");
        // Godot arrays the tests left behind are finalized now, not after the engine has shut down.
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    void AStateCanRunABehaviorSubtree() {
        var first = new FsmProbeAction { RunningTicks = 1 };
        var second = new FsmProbeAction();
        var combo = new FsmState { Name = "Combo" };
        combo.Actions.Add(Composite<SequenceNode>(first, second));
        var done = new FsmState { Name = "Done" };
        combo.Transitions.Add(new FsmTransition { TargetStateId = done.Id, On = FsmTrigger.Succeeded });

        var instance = FsmInstance.Create(Machine(combo, done));
        var ctx = new MissContext { Blackboard = new Blackboard(), Delta = Step };

        instance.Tick(ctx);
        var sequence = instance.ActionsOf(combo)[0];
        var a = (FsmProbeAction) sequence.Children[0];
        var b = (FsmProbeAction) sequence.Children[1];
        Check("an action of a state can be a whole subtree, of which it runs a copy", sequence is SequenceNode && !ReferenceEquals(a, first) && first.TotalTicks == 0);
        Check("the sequence waits on its running child", instance.Current == combo && a.TotalTicks == 1 && b.TotalTicks == 0);

        instance.Tick(ctx);
        Check("and carries on to the next one", a.TotalTicks == 2 && b.TotalTicks == 1);
        Check("the state is left once the subtree succeeded", instance.Current == done);
    }

    void LeavingAStateInterruptsTheSubtree() {
        var busy = new FsmState { Name = "Busy" };
        busy.Actions.Add(Composite<SequenceNode>(new FsmProbeAction { RunningTicks = 100 }));
        var next = new FsmState { Name = "Next" };
        var leave = new FsmTransition { TargetStateId = next.Id };
        leave.Conditions.Add(new FsmProbeCondition());
        busy.Transitions.Add(leave);

        var instance = FsmInstance.Create(Machine(busy, next));
        instance.Tick(new MissContext { Blackboard = new Blackboard(), Delta = Step });

        var running = (FsmProbeAction) instance.ActionsOf(busy)[0].Children[0];
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
            var transition = new FsmTransition { TargetStateId = to.Id };
            transition.Conditions.Add(list);
            from.Transitions.Add(transition);
            var instance = FsmInstance.Create(Machine(from, to));
            instance.Tick(new MissContext { Blackboard = new Blackboard(), Delta = Step });
            return instance.Current;
        }

        Check("a condition list that needs all of them does not fire", Arrive(ListMode.Sequence).Name == "From");
        Check("one that needs any of them does", Arrive(ListMode.Selector).Name == "To");
    }

    /// <summary>A state starts a dialogue and is left when the talking is over; misstate and misspeak never hear of each other.</summary>
    void AStateMachineTalks() {
        var talking = new FsmState { Name = "Talking" };
        talking.Actions.Add(new StartDialogueAction { Dialogue = Greeting() });
        var done = new FsmState { Name = "Done" };
        talking.Transitions.Add(new FsmTransition { TargetStateId = done.Id, On = FsmTrigger.Finished });
        var away = new FsmState { Name = "Away" };
        var interrupted = new FsmTransition { TargetStateId = away.Id };
        interrupted.Conditions.Add(new FsmProbeCondition { AtLeast = 1 });
        talking.Transitions.Insert(0, interrupted);

        var actor = new Node { Name = "Npc" };
        var speaker = new DialogueRunner { Thread = MissRunner.ProcessThread.Manual };
        var brain = new FsmRunner { Machine = Machine(talking, done, away), Thread = MissRunner.ProcessThread.Manual };
        actor.AddChild(speaker);
        actor.AddChild(brain);
        AddChild(actor);

        brain.Tick(Step);
        Check("a state's action starts a dialogue on the actor's dialogue runner", speaker.IsActive && speaker.Text == "Hello.");
        brain.Tick(Step);
        Check("the state lasts as long as the talk", brain.CurrentState == "Talking");

        speaker.Advance();
        brain.Tick(Step);
        Check("and is left once the talk is over", brain.CurrentState == "Done" && !speaker.IsActive);

        brain.Restart();
        brain.Tick(Step);
        brain.Instance.GoTo(away.Id, new MissContext { Actor = actor, Blackboard = brain.Blackboard });
        Check("leaving the state mid-talk cancels the dialogue", brain.CurrentState == "Away" && !speaker.IsActive);

        actor.Free();
    }

    /// <summary>The same action in a behavior tree, with a condition of misspeak guarding a branch.</summary>
    void ABehaviorTreeTalks() {
        var after = new FsmProbeAction();
        var tree = new BehaviorTree {
            Root = Composite<SelectorNode>(
                Composite<SequenceNode>(new DialogueActiveCondition { Negate = true }, new StartDialogueAction { Dialogue = Greeting(), WaitUntilFinished = false }),
                Composite<SequenceNode>(new DialogueActiveCondition(), after)),
        };

        var actor = new Node { Name = "Npc" };
        var speaker = new DialogueRunner { Thread = MissRunner.ProcessThread.Manual };
        var brain = new BehaviorTreeRunner { Tree = tree, Thread = MissRunner.ProcessThread.Manual };
        actor.AddChild(speaker);
        actor.AddChild(brain);
        AddChild(actor);

        brain.Tick(Step);
        Check("a behavior tree starts a dialogue the same way", speaker.IsActive && speaker.Text == "Hello.");
        Check("and a branch can ask whether one is running", brain.Tick(Step) == MissStatus.Success && speaker.IsActive);

        actor.Free();
    }

    static Dialogue Greeting() {
        var dialogue = new Dialogue();
        var section = new DialogueSection();
        section.Lines.Add(new DialogueLine { Text = "Hello." });
        dialogue.Sections.Add(section);
        return dialogue;
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