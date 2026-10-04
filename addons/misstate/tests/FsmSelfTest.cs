using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;
using Misstate.Tests;

namespace Misstate;

/// <summary>
/// Headless self test for the runtime. Builds machines in code — no editor, no scene dependencies —
/// and exits with code 0 on success, 1 on failure.
/// <code>
/// godot --headless --path &lt;project&gt; res://addons/misstate/tests/self_test.tscn
/// </code>
/// </summary>
public partial class FsmSelfTest : Node {
    const double Step = 0.05;

    readonly List<string> _failures = [];
    int _checks;

    public override void _Ready() {
        // states
        TheInitialStateIsEnteredOnTheFirstTick();
        AStateRunsItsNodeLikeARoot();
        AStateWithoutANodeJustWaits();

        // transitions
        AConditionMovesTheMachineOn();
        TriggersFollowHowTheNodeFinished();
        TheFirstTransitionThatFiresWins();
        LeavingAStateInterruptsItsNode();
        ATransitionToNowhereIsIgnored();

        // blackboard
        NodesLinkToTheMachinesBlackboard();

        // runner
        TheRunnerReportsStateChanges();
        ANodeCanStopItsRunner();
        ANodeCanSendTheMachineElsewhere();
        RestartGoesBackToTheInitialState();
        TheRunnerOffersTheMachinesBlackboardInTheInspector();

        // instance isolation and serialisation
        TwoRunnersOfOneMachineAreIndependent();
        AMachineSurvivesSavingAndLoading();
        ProblemsAreReported();

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"misstate self test: {_checks - _failures.Count}/{_checks} checks passed");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    // ---- states ------------------------------------------------------------------------------

    void TheInitialStateIsEnteredOnTheFirstTick() {
        var idle = State("Idle");
        var walk = State("Walk");
        var machine = Machine(idle, walk);

        var run = new MachineRun(machine);
        Check("a machine is in no state before its first tick", run.Instance.Current == null);
        run.Tick();
        Check("the first state is the initial one by default", run.Instance.Current == idle);

        machine.InitialStateId = walk.Id;
        run = new MachineRun(machine);
        run.Tick();
        Check("another state can be made the initial one", run.Instance.Current == walk);

        Check("a machine without states cannot be run", FsmInstance.Create(new Fsm()) == null);
    }

    void AStateRunsItsNodeLikeARoot() {
        var action = new FsmProbeAction { RunningTicks = 2 };
        var run = new MachineRun(Machine(State("Work", action)));

        run.Tick();
        run.Tick();
        var clone = (FsmProbeAction) run.Instance.CurrentNode;
        Check("a state runs a copy of its node", clone != null && !ReferenceEquals(clone, action) && action.TotalTicks == 0);
        Check("a running node is resumed, not restarted", clone.BeforeRuns == 1 && clone.Ticks == 2 && clone.AfterRuns == 0);

        run.Tick();
        Check("a finished node gets its AfterRun", clone.AfterRuns == 1);
        run.Tick();
        Check("and starts over while the state lasts", clone.BeforeRuns == 2 && clone.Ticks == 1);
    }

    void AStateWithoutANodeJustWaits() {
        var gate = new FsmProbeCondition { AtLeast = 1 };
        var wait = State("Wait");
        var done = State("Done");
        Link(wait, done, condition: gate);
        var run = new MachineRun(Machine(wait, done));

        run.Tick();
        Check("a state without a node stays put", run.Instance.Current == wait);
        ((FsmProbeCondition) gate).Value.Literal = 1;
        run = new MachineRun(Machine(wait, done));
        run.Tick();
        Check("and still takes its transitions", run.Instance.Current == done);
    }

    // ---- transitions -------------------------------------------------------------------------

    void AConditionMovesTheMachineOn() {
        var count = Entry("count", Variant.Type.Int, 0);
        var action = new FsmProbeAction { RunningTicks = 100 };
        Bind(action.Counter, count);
        var enough = new FsmProbeCondition { AtLeast = 3 };
        Bind(enough.Value, count);

        var work = State("Work", action);
        var rest = State("Rest", new FsmProbeAction());
        Link(work, rest, condition: enough);
        var machine = Machine(work, rest);
        machine.Blackboard.Add(count);

        var run = new MachineRun(machine);
        run.Tick();
        run.Tick();
        Check("a condition that does not hold keeps the state", run.Instance.Current == work);
        run.Tick();
        Check("once it holds, the machine moves on", run.Instance.Current == rest);

        var restNode = (FsmProbeAction) run.Instance.CurrentNode;
        Check("the new state starts on the next tick, not the same one", restNode.TotalTicks == 0);
        run.Tick();
        Check("and then runs its own node", restNode.TotalTicks == 1);
    }

    void TriggersFollowHowTheNodeFinished() {
        FsmState Arrive(FsmTrigger trigger, MissStatus result, int runningTicks, int ticks) {
            var from = State("From", new FsmProbeAction { Result = result, RunningTicks = runningTicks });
            var to = State("To");
            Link(from, to, trigger);
            var run = new MachineRun(Machine(from, to));
            for (var i = 0; i < ticks; i++) run.Tick();
            return run.Instance.Current;
        }

        Check("Finished waits while the node is running", Arrive(FsmTrigger.Finished, MissStatus.Success, 2, 2).Name == "From");
        Check("Finished fires when it is done", Arrive(FsmTrigger.Finished, MissStatus.Failure, 2, 3).Name == "To");
        Check("Succeeded fires on success", Arrive(FsmTrigger.Succeeded, MissStatus.Success, 0, 1).Name == "To");
        Check("Succeeded ignores a failure", Arrive(FsmTrigger.Succeeded, MissStatus.Failure, 0, 3).Name == "From");
        Check("Failed fires on failure", Arrive(FsmTrigger.Failed, MissStatus.Failure, 0, 1).Name == "To");
        Check("Failed ignores a success", Arrive(FsmTrigger.Failed, MissStatus.Success, 0, 3).Name == "From");

        var gated = State("From", new FsmProbeAction());
        var target = State("To");
        Link(gated, target, FsmTrigger.Finished, new FsmProbeCondition { AtLeast = 1 });
        var blocked = new MachineRun(Machine(gated, target));
        blocked.Tick();
        Check("a trigger still needs its condition to hold", blocked.Instance.Current == gated);
    }

    void TheFirstTransitionThatFiresWins() {
        var from = State("From");
        var first = State("First");
        var second = State("Second");
        Link(from, first, condition: new FsmProbeCondition { AtLeast = 1 });
        Link(from, second, condition: new FsmProbeCondition());
        Link(from, first, condition: new FsmProbeCondition());

        var run = new MachineRun(Machine(from, first, second));
        run.Tick();
        Check("transitions are considered top to bottom", run.Instance.Current == second);
        run.Tick();
        Check("and only one is taken per tick", run.Instance.Current == second);
    }

    void LeavingAStateInterruptsItsNode() {
        var busy = State("Busy", new FsmProbeAction { RunningTicks = 100 });
        var next = State("Next");
        Link(busy, next, condition: new FsmProbeCondition());
        var run = new MachineRun(Machine(busy, next));

        run.Tick();
        var left = run.NodeOf(busy);
        Check("a node still running when its state is left is interrupted", left.Interrupts == 1 && left.AfterRuns == 0);

        var quick = State("Quick", new FsmProbeAction());
        Link(quick, next, FsmTrigger.Finished);
        run = new MachineRun(Machine(quick, next));
        run.Tick();
        Check("a node that finished is not", run.NodeOf(quick).Interrupts == 0 && run.NodeOf(quick).AfterRuns == 1);
    }

    void ATransitionToNowhereIsIgnored() {
        var only = State("Only");
        only.Transitions.Add(new FsmTransition { TargetStateId = "gone" });
        var run = new MachineRun(Machine(only));
        run.Tick();
        Check("a transition to a state that no longer exists changes nothing", run.Instance.Current == only);
    }

    // ---- blackboard --------------------------------------------------------------------------

    void NodesLinkToTheMachinesBlackboard() {
        var count = Entry("count", Variant.Type.Int, 5);
        var action = new FsmProbeAction();
        action.EnsureId();
        Bind(action.Counter, count);
        var gate = new FsmProbeCondition();
        gate.EnsureId();
        var state = State("Count", action);
        Link(state, state, condition: gate);
        var machine = Machine(state);
        machine.Blackboard.Add(count);

        var hosts = ((IBlackboardSource) machine).ParamHosts().ToList();
        Check("a machine offers the nodes of its states and transitions to the blackboard editor",
            hosts.Contains(action) && hosts.Contains(gate));

        var run = new MachineRun(machine);
        run.Tick();
        Check("a state's node reads and writes the linked entry", run.Board.Get<int>("count") == 6);

        Check("a fitting link is no problem", !machine.LinkProblems(action).Any());
        count.VariantType = Variant.Type.String;
        Check("a link that no longer fits is reported", machine.LinkProblems(action).Any(p => p.Contains("expects int")));
    }

    // ---- runner ------------------------------------------------------------------------------

    void TheRunnerReportsStateChanges() {
        var a = State("A", new FsmProbeAction());
        var b = State("B");
        Link(a, b, FsmTrigger.Finished);
        var runner = Runner(Machine(a, b));

        var changes = new List<string>();
        runner.StateChanged += (from, to) => changes.Add($"{from}>{to}");

        Check("a runner is in no state before its first tick", runner.CurrentState == "");
        Check("a machine with a state to be in is running", runner.Tick(Step) == MissStatus.Running);
        runner.Tick(Step);
        Check("every state entered is reported, even one left on the same tick", changes.SequenceEqual([">A", "A>B"]) && runner.CurrentState == "B");

        Check("a state can be forced by name", runner.GoTo("A") && runner.CurrentState == "A" && changes.Last() == "B>A");
        Check("but not one the machine does not have", !runner.GoTo("Nope") && runner.CurrentState == "A");
        runner.QueueFree();
    }

    /// <summary>
    /// The probe knows nothing about state machines: it stops whatever runs it through
    /// <see cref="IMissRunner"/>, which is all a shared action can rely on.
    /// </summary>
    void ANodeCanStopItsRunner() {
        var runner = Runner(Machine(State("Work", new FsmProbeAction { RunningTicks = 100, StopRunnerAtTick = 2 })));

        runner.Tick(Step);
        runner.Tick(Step);
        var node = (FsmProbeAction) runner.Instance.CurrentNode;
        Check("a node can stop the runner from inside a tick", !runner.Enabled && node.TotalTicks == 2);
        Check("which interrupts what was running, after the tick", node.Interrupts == 1);

        runner.Tick(Step);
        Check("a stopped runner does not tick", node.TotalTicks == 2);
        runner.Enabled = true;
        runner.Tick(Step);
        Check("switched back on, the machine carries on in its state", runner.CurrentState == "Work" && node.BeforeRuns == 2);
        runner.QueueFree();
    }

    void ANodeCanSendTheMachineElsewhere() {
        var jumper = State("Jump", new FsmProbeAction { RunningTicks = 100, GoToOnFirstTick = "Land" });
        var skipped = State("Skipped");
        var land = State("Land");
        Link(jumper, skipped, condition: new FsmProbeCondition());
        var runner = Runner(Machine(jumper, skipped, land));

        runner.Tick(Step);
        Check("a node can send the machine to another state mid-tick", runner.CurrentState == "Land");
        runner.Tick(Step);
        Check("and the transitions of the state it left no longer apply", runner.CurrentState == "Land");
        runner.QueueFree();
    }

    void RestartGoesBackToTheInitialState() {
        var a = State("A");
        var b = State("B");
        Link(a, b, condition: new FsmProbeCondition());
        var runner = Runner(Machine(a, b));

        runner.Tick(Step);
        runner.Restart();
        Check("a restarted machine is in no state", runner.CurrentState == "");
        runner.Tick(Step);
        Check("until its next tick enters the initial one", runner.CurrentState == "B");
        runner.QueueFree();
    }

    void TheRunnerOffersTheMachinesBlackboardInTheInspector() {
        var count = Entry("count", Variant.Type.Int, 5);
        var action = new FsmProbeAction();
        Bind(action.Counter, count);
        var machine = Machine(State("Count", action));
        machine.Blackboard.Add(count);

        var runner = new FsmRunner { Machine = machine, Thread = MissRunner.ProcessThread.Manual };
        var shown = runner.GetPropertyList().Select(p => p["name"].AsString()).ToList();
        Check("the runner lists the machine's entries by name", shown.Contains(MissRunner.BlackboardGroup + "count"));
        Check("an entry shows the machine's default", runner.Get(MissRunner.BlackboardGroup + "count").AsInt32() == 5);

        runner.Set(MissRunner.BlackboardGroup + "count", 40);
        Check("a changed value is kept as an override, by entry id", runner.TryGetOverride(count.Id, out var stored) && stored.AsInt32() == 40);

        AddChild(runner);
        runner.Tick(Step);
        Check("and is what the machine starts with", runner.Blackboard.Get<int>("count") == 41);
        runner.QueueFree();
    }
    // ---- instance isolation and serialisation ------------------------------------------------

    void TwoRunnersOfOneMachineAreIndependent() {
        var action = new FsmProbeAction { RunningTicks = 100 };
        var machine = Machine(State("Work", action));
        var first = new MachineRun(machine);
        var second = new MachineRun(machine);

        first.Tick();
        first.Tick();
        second.Tick();
        var a = (FsmProbeAction) first.Instance.CurrentNode;
        var b = (FsmProbeAction) second.Instance.CurrentNode;
        Check("two runners of one machine do not share a node", !ReferenceEquals(a, b) && a.TotalTicks == 2 && b.TotalTicks == 1);
        Check("and neither ticks the definition", action.TotalTicks == 0 && action.IsDefinition && !a.IsDefinition);
    }

    void AMachineSurvivesSavingAndLoading() {
        var count = Entry("count", Variant.Type.Int, 0);
        var action = new FsmProbeAction { RunningTicks = 1 };
        Bind(action.Counter, count);
        var work = State("Work", action);
        var rest = State("Rest");
        Link(work, rest, FsmTrigger.Finished, new FsmProbeCondition { AtLeast = 2 });
        var machine = Machine(work, rest);
        machine.InitialStateId = work.Id;
        machine.Blackboard.Add(count);

        const string path = "user://misstate_machine.tres";
        Check("a machine saves", ResourceSaver.Save(machine, path) == Error.Ok);
        var loaded = ResourceLoader.Load<Fsm>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
        var loadedWork = loaded?.FindStateByName("Work");
        Check("states come back with their ids", loadedWork?.Id == work.Id && loaded.InitialState == loadedWork);
        Check("a state's node comes back with its link", loadedWork?.Node is FsmProbeAction { RunningTicks: 1 } node && node.Counter.EntryId == count.Id);
        Check("a transition comes back with its target, trigger and condition",
            loadedWork?.Transitions.FirstOrDefault() is { On: FsmTrigger.Finished, Condition: FsmProbeCondition { AtLeast: 2 } } transition
            && transition.TargetStateId == rest.Id);

        var run = new MachineRun(loaded);
        run.Tick();
        run.Tick();
        Check("a loaded machine runs", run.Instance.Current?.Name == "Work" && run.Board.Get<int>("count") == 2);
    }

    void ProblemsAreReported() {
        Check("a machine without states says so", new Fsm().Validate().Any(p => p.Contains("no states")));

        var a = State("Twin");
        var b = State("Twin");
        a.Transitions.Add(new FsmTransition { TargetStateId = "gone" });
        Link(b, a);
        var machine = Machine(a, b);
        machine.InitialStateId = "gone";
        var problems = machine.Validate();

        Check("a missing initial state is reported", problems.Any(p => p.Contains("initial state")));
        Check("a name used twice is reported", problems.Any(p => p.Contains("more than one state")));
        Check("a transition to nowhere is reported", problems.Any(p => p.Contains("leads nowhere")));
        Check("a transition that always fires is reported", problems.Any(p => p.Contains("left after one tick")));

        var fine = State("A", new FsmProbeAction());
        Link(fine, fine, FsmTrigger.Finished);
        Check("a sound machine has no problems", Machine(fine).Validate().Length == 0);
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>Runs a machine the way a runner does, without a scene around it.</summary>
    sealed class MachineRun {
        public readonly FsmInstance Instance;
        public readonly Blackboard Board = new();

        public MachineRun(Fsm machine) {
            Instance = FsmInstance.Create(machine);
            Board.Declare(machine.Blackboard);
        }

        public void Tick(double delta = Step) => Instance.Tick(new MissContext { Blackboard = Board, Delta = delta });

        public FsmProbeAction NodeOf(FsmState state) => Instance.NodeOf(state) as FsmProbeAction;
    }

    FsmRunner Runner(Fsm machine) {
        var runner = new FsmRunner { Machine = machine, Thread = MissRunner.ProcessThread.Manual };
        AddChild(runner);
        return runner;
    }

    static Fsm Machine(params FsmState[] states) {
        var machine = new Fsm();
        foreach (var state in states) machine.States.Add(state);
        return machine;
    }

    static FsmState State(string name, MissNode node = null) => new() { Name = name, Node = node };

    static void Link(FsmState from, FsmState to, FsmTrigger on = FsmTrigger.Always, MissNode condition = null)
        => from.Transitions.Add(new FsmTransition { TargetStateId = to.Id, On = on, Condition = condition });

    static BlackboardEntry Entry(string name, Variant.Type type, Variant value)
        => new() { Name = name, VariantType = type, Default = value };

    static void Bind(IBbParam param, BlackboardEntry entry) {
        param.EntryId = entry.Id;
        param.EntryName = entry.Name;
    }

    void Check(string what, bool condition) {
        _checks++;
        if (!condition) _failures.Add(what);
    }
}