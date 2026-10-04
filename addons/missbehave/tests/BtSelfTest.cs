using System.Collections.Generic;
using System.Linq;
using Godot;
using Missbehave.Tests;
using Misscore;

namespace Missbehave;

/// <summary>
/// Headless self test for the runtime. Builds trees in code — no editor, no scene dependencies —
/// and exits with code 0 on success, 1 on failure.
/// <code>
/// godot --headless --path &lt;project&gt; res://addons/missbehave/tests/self_test.tscn
/// </code>
/// </summary>
public partial class BtSelfTest : Node {
    const double Step = 0.05;

    readonly List<string> _failures = [];
    int _checks;

    public override void _Ready() {
        // composites
        SequenceStopsAtFirstFailure();
        SelectorStopsAtFirstSuccess();
        RunningChildIsResumedNotRestarted();
        ReactiveSelectorPreemptsLowerPriority();
        ReactiveSequenceRechecksEarlierChildren();
        SequenceStarResumesAtTheFailedChild();
        RandomSequenceRunsEveryChildOnce();
        SimpleParallelReportsPrimaryAndCutsSecondary();
        ListsWorkThroughTheirEntriesByMode();

        // decorators
        InverterFlipsResult();
        FailerAndSucceederOverrideResult();
        UntilFailLoopsWhileChildSucceeds();
        RepeaterCountsSuccesses();
        LimiterCutsOffALongRunningChild();
        TimeLimiterCutsOffAfterWaitTime();
        DelayerHoldsTheChildBack();
        CooldownBlocksAfterTheChildFinished();

        // blackboard
        BlackboardLeavesRoundTrip();
        DeclaredEntriesAnswerToNameAndId();
        ParametersReadFallBackAndWriteLocally();
        ValueIsCurrentBeforeRunAndEveryTick();
        ParametersSurviveSavingLoadingAndCloning();
        ParametersSurviveAnAssemblyReload();
        ParametersNeedNoInitializer();
        ParameterTypesDecideWhatTheyCanLinkTo();
        RunnerOverridesSurviveASceneFile();

        // runner
        ALeafCanStopItsRunner();

        // live debugging
        FrameThrottleSuppressesRepeatsButNotAfterASwitch();
        LiveDebugKeepsStreamingWhenPausedOrTheWatchedRunnerDies();

        // instance isolation and serialisation
        DefinitionIsNeverMutatedByTicking();
        TwoInstancesOfOneDefinitionAreIndependent();
        ExportedResourcesStaySharedAcrossClones();
        ResourceRoundTripKeepsStructure();
        SharedTreeResourceStillGivesSeparateInstances();

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"missbehave self test: {_checks - _failures.Count}/{_checks} checks passed");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    // ---- composites --------------------------------------------------------------------------

    void SequenceStopsAtFirstFailure() {
        var run = Run(Node<SequenceNode>(
            Probe(BehaviorStatus.Success),
            Probe(BehaviorStatus.Failure),
            Probe(BehaviorStatus.Success)));

        Check("sequence fails at first failure", run.Tick() == BehaviorStatus.Failure);
        Check("sequence skips children after the failure", run.Probe(3).Ticks == 0);
    }

    void SelectorStopsAtFirstSuccess() {
        var run = Run(Node<SelectorNode>(
            Probe(BehaviorStatus.Failure),
            Probe(BehaviorStatus.Success),
            Probe(BehaviorStatus.Success)));

        Check("selector succeeds at first success", run.Tick() == BehaviorStatus.Success);
        Check("selector skips children after the success", run.Probe(3).Ticks == 0);
    }

    void RunningChildIsResumedNotRestarted() {
        var run = Run(Node<SequenceNode>(
            Probe(BehaviorStatus.Success),
            Probe(BehaviorStatus.Success, runningTicks: 2)));

        Check("running child reports Running", run.Tick() == BehaviorStatus.Running);
        run.Tick();
        Check("resuming does not re-tick earlier children", run.Probe(1).Ticks == 1);
        Check("resuming does not call BeforeRun again", run.Probe(2).BeforeRuns == 1);
        Check("sequence completes once the child finishes", run.Tick() == BehaviorStatus.Success);
    }

    void ReactiveSelectorPreemptsLowerPriority() {
        var run = Run(Node<SelectorReactiveNode>(
            Probe(BehaviorStatus.Failure),
            Probe(BehaviorStatus.Success, runningTicks: 10)));

        Check("reactive selector falls through to the running branch", run.Tick() == BehaviorStatus.Running);

        // the high-priority branch becomes viable
        run.Probe(1).Result = BehaviorStatus.Success;

        Check("reactive selector switches branch", run.Tick() == BehaviorStatus.Success);
        Check("reactive selector interrupts the preempted branch", run.Probe(2).Interrupts == 1);
    }

    void ReactiveSequenceRechecksEarlierChildren() {
        var run = Run(Node<SequenceReactiveNode>(
            Probe(BehaviorStatus.Success),
            Probe(BehaviorStatus.Success, runningTicks: 10)));

        run.Tick();
        Check("reactive sequence re-ticks the guard", run.Probe(1).Ticks == 1);
        run.Tick();
        Check("reactive sequence re-ticks the guard every tick", run.Probe(1).BeforeRuns == 2);

        // the guard stops holding
        run.Probe(1).Result = BehaviorStatus.Failure;

        Check("reactive sequence fails when the guard fails", run.Tick() == BehaviorStatus.Failure);
        Check("reactive sequence interrupts the running action", run.Probe(2).Interrupts == 1);
    }

    void SequenceStarResumesAtTheFailedChild() {
        var run = Run(Node<SequenceStarNode>(
            Probe(BehaviorStatus.Success),
            Probe(BehaviorStatus.Failure)));

        Check("sequence star reports the failure", run.Tick() == BehaviorStatus.Failure);
        run.Tick();
        Check("sequence star does not re-run completed children", run.Probe(1).Ticks == 1);
        Check("sequence star retries the failed child", run.Probe(2).Ticks == 1);

        run.Probe(2).Result = BehaviorStatus.Success;
        Check("sequence star completes once the child succeeds", run.Tick() == BehaviorStatus.Success);
    }

    void RandomSequenceRunsEveryChildOnce() {
        var root = Node<SequenceRandomNode>(
            Probe(BehaviorStatus.Success),
            Probe(BehaviorStatus.Success),
            Probe(BehaviorStatus.Success));
        ((SequenceRandomNode) root).RandomSeed = 12345;
        var run = Run(root);

        Check("random sequence succeeds when all children succeed", run.Tick() == BehaviorStatus.Success);
        var ticked = 0;
        for (var i = 1; i <= 3; i++) ticked += run.Probe(i).Ticks;
        Check("random sequence runs each child exactly once", ticked == 3);
    }

    void SimpleParallelReportsPrimaryAndCutsSecondary() {
        var run = Run(Node<SimpleParallelNode>(
            Probe(BehaviorStatus.Success, runningTicks: 1),
            Probe(BehaviorStatus.Success, runningTicks: 10)));

        Check("parallel runs while the primary runs", run.Tick() == BehaviorStatus.Running);
        Check("parallel ticks the secondary too", run.Probe(2).Ticks == 1);
        Check("parallel reports the primary result", run.Tick() == BehaviorStatus.Success);
        Check("parallel interrupts the secondary", run.Probe(2).Interrupts == 1);
    }

    // ---- decorators --------------------------------------------------------------------------

    void ListsWorkThroughTheirEntriesByMode() {
        var anyOf = Node<ConditionListNode>(Holds(false), Holds(true), Holds(true));
        anyOf.Mode = ListMode.Selector;
        var run = Run(anyOf);
        Check("a condition list in selector mode succeeds at the first condition that holds",
            run.Tick() == BehaviorStatus.Success && ((BtProbeCondition) run.Instance.Flat[3]).Checks == 0);
        Check("the entries of a list report their own status to the debugger",
            run.Instance.Frame[1] == (byte) BehaviorStatus.Failure && run.Instance.Frame[2] == (byte) BehaviorStatus.Success
            && run.Instance.Frame[3] == BehaviorStatusExtensions.NotTicked);
        Check("the debugger maps list entries back to their definitions",
            run.Instance.IdTable[2] == anyOf.Children[1].Id && run.Instance.ParentTable[2] == 0);

        var allOf = Run(Node<ConditionListNode>(Holds(true), Holds(false), Holds(true)));
        Check("a condition list in sequence mode fails at the first condition that does not hold",
            allOf.Tick() == BehaviorStatus.Failure && ((BtProbeCondition) allOf.Instance.Flat[3]).Checks == 0);

        var actions = Run(Node<ActionListNode>(Probe(BehaviorStatus.Success), Probe(BehaviorStatus.Success, runningTicks: 1)));
        Check("an action list reports a running action", actions.Tick() == BehaviorStatus.Running);
        Check("an action list resumes the running action instead of starting over",
            actions.Tick() == BehaviorStatus.Success && actions.Probe(1).Ticks == 1 && actions.Probe(2).BeforeRuns == 1);

        var mixed = Node<ConditionListNode>(Holds(true), Probe(BehaviorStatus.Success));
        Check("a list warns about an entry of the wrong kind",
            mixed.GetConfigurationWarnings().Any(w => w.Contains("not a ConditionNode")));
    }

    static BtProbeCondition Holds(bool holds) {
        var probe = new BtProbeCondition { Holds = holds };
        probe.EnsureId();
        return probe;
    }

    void InverterFlipsResult() {
        var failing = Run(Node<InverterNode>(Probe(BehaviorStatus.Failure)));
        var succeeding = Run(Node<InverterNode>(Probe(BehaviorStatus.Success)));

        Check("inverter turns Failure into Success", failing.Tick() == BehaviorStatus.Success);
        Check("inverter turns Success into Failure", succeeding.Tick() == BehaviorStatus.Failure);
    }

    void FailerAndSucceederOverrideResult() {
        var failer = Run(Node<FailerNode>(Probe(BehaviorStatus.Success)));
        var succeeder = Run(Node<SucceederNode>(Probe(BehaviorStatus.Failure)));

        Check("failer reports Failure", failer.Tick() == BehaviorStatus.Failure);
        Check("succeeder reports Success", succeeder.Tick() == BehaviorStatus.Success);
    }

    void UntilFailLoopsWhileChildSucceeds() {
        var run = Run(Node<UntilFailNode>(Probe(BehaviorStatus.Success)));

        Check("until-fail keeps running on Success", run.Tick() == BehaviorStatus.Running);
        run.Probe(1).Result = BehaviorStatus.Failure;
        Check("until-fail succeeds on Failure", run.Tick() == BehaviorStatus.Success);
    }

    void RepeaterCountsSuccesses() {
        var root = Node<RepeaterNode>(Probe(BehaviorStatus.Success));
        ((RepeaterNode) root).Repetitions = 3;
        var run = Run(root);

        Check("repeater runs again after the 1st success", run.Tick() == BehaviorStatus.Running);
        Check("repeater runs again after the 2nd success", run.Tick() == BehaviorStatus.Running);
        Check("repeater succeeds after the last repetition", run.Tick() == BehaviorStatus.Success);
        Check("repeater ran the child once per repetition", run.Probe(1).BeforeRuns == 3);
    }

    void LimiterCutsOffALongRunningChild() {
        var root = Node<LimiterNode>(Probe(BehaviorStatus.Success, runningTicks: 10));
        ((LimiterNode) root).MaxTicks = 2;
        var run = Run(root);

        Check("limiter passes the 1st tick through", run.Tick() == BehaviorStatus.Running);
        Check("limiter passes the 2nd tick through", run.Tick() == BehaviorStatus.Running);
        Check("limiter fails once the budget is spent", run.Tick() == BehaviorStatus.Failure);
        Check("limiter interrupts the child it cut off", run.Probe(1).Interrupts == 1);
    }

    void TimeLimiterCutsOffAfterWaitTime() {
        var root = Node<TimeLimiterNode>(Probe(BehaviorStatus.Success, runningTicks: 10));
        ((TimeLimiterNode) root).WaitTime = Step * 2;
        var run = Run(root);

        Check("time limiter allows the child within budget", run.Tick(Step) == BehaviorStatus.Running);
        run.Tick(Step);
        Check("time limiter fails once the time is up", run.Tick(Step) == BehaviorStatus.Failure);
        Check("time limiter interrupts the child it cut off", run.Probe(1).Interrupts == 1);
    }

    void DelayerHoldsTheChildBack() {
        var root = Node<DelayerNode>(Probe(BehaviorStatus.Success));
        ((DelayerNode) root).WaitTime = Step * 2;
        var run = Run(root);

        Check("delayer runs while waiting", run.Tick(Step) == BehaviorStatus.Running);
        run.Tick(Step);
        Check("delayer did not touch the child yet", run.Probe(1).Ticks == 0);
        Check("delayer releases the child after the wait", run.Tick(Step) == BehaviorStatus.Success);
        Check("delayer ran the child once", run.Probe(1).Ticks == 1);
    }

    void CooldownBlocksAfterTheChildFinished() {
        var root = Node<CooldownNode>(Probe(BehaviorStatus.Success));
        ((CooldownNode) root).WaitTime = Step * 2;
        var run = Run(root);

        Check("cooldown lets the first run through", run.Tick(Step) == BehaviorStatus.Success);
        Check("cooldown blocks right after", run.Tick(Step) == BehaviorStatus.Failure);
        Check("cooldown still blocks", run.Tick(Step) == BehaviorStatus.Failure);
        Check("cooldown reopens after the wait", run.Tick(Step) == BehaviorStatus.Success);
    }

    // ---- blackboard --------------------------------------------------------------------------

    void BlackboardLeavesRoundTrip() {
        var ammo = Entry("ammo", Variant.Type.Int, 0);

        var set = new BlackboardSetNode();
        Link(set.Target, ammo);
        set.Value.Literal = 3;
        var has = new BlackboardHasNode();
        Link(has.Entry, ammo);
        var compare = new BlackboardCompareNode { Operator = BlackboardCompareNode.CompareOperator.GreaterOrEqual };
        Link(compare.Left, ammo);
        compare.Right.Literal = 3.0;
        set.EnsureId();
        has.EnsureId();
        compare.EnsureId();

        var tree = Tree(Node<SequenceNode>(set, has, compare));
        tree.Blackboard.Add(ammo);
        var run = new TreeRun(tree);

        Check("blackboard leaves chain to Success", run.Tick() == BehaviorStatus.Success);
        Check("the written value is readable by the entry's name", run.Board.Get<int>("ammo") == 3);
        Check("summaries name the linked entry", set.GetSummary() == "ammo = 3" && compare.GetSummary() == "ammo >= 3.0");

        var erase = new BlackboardEraseNode();
        Link(erase.Entry, ammo);
        erase.EnsureId();
        var eraseTree = Tree(Node<SequenceNode>(erase));
        eraseTree.Blackboard.Add(ammo);
        var eraseRun = new TreeRun(eraseTree);
        eraseRun.Tick();
        Check("erase removes the value outright", !eraseRun.Board.Has("ammo"));

        var unlinked = new BlackboardHasNode();
        Check("an entry-only parameter warns while unlinked",
            unlinked.GetConfigurationWarnings().Any(w => w.Contains(nameof(BlackboardHasNode.Entry))));
    }

    void DeclaredEntriesAnswerToNameAndId() {
        var list = Entry("targets", Variant.Type.Array, new Godot.Collections.Array { 1 });
        var speed = Entry("speed", Variant.Type.Float, 2.5);

        var board = new Blackboard();
        board.Declare([list, speed]);
        var other = new Blackboard();
        other.Declare([list, speed]);

        Check("a declared entry starts at its default", board.Get<float>("speed") == 2.5f);
        board.Set("speed", 7f);
        Check("setting by name writes the entry", board.TryGetById(speed.Id, out var byId) && byId.AsSingle() == 7f);
        board.Set("mood", 1);
        Check("a name nobody declared is kept ad hoc", !board.IsDeclared("mood") && board.Get<int>("mood") == 1);

        board.Get<Godot.Collections.Array>("targets").Add(2);
        Check("runners do not share collection defaults", other.Get<Godot.Collections.Array>("targets").Count == 1
                                                         && list.Default.AsGodotArray().Count == 1);
        Check("a value of the wrong type falls back", board.Get("speed", "fallback") == "fallback");
    }

    void ParametersReadFallBackAndWriteLocally() {
        var probe = new BtProbeParamAction();
        probe.EnsureId();
        var speed = Entry("speed", Variant.Type.Float, 10.0);

        var unlinkedRun = Run(Node<SequenceNode>(probe));
        unlinkedRun.Tick();
        unlinkedRun.Tick();
        var unlinkedClone = (BtProbeParamAction) unlinkedRun.Instance.Flat[1];
        Check("an unlinked parameter reads its fixed value", unlinkedClone.LastSpeed == 5f);
        Check("an unlinked parameter writes into the runner's own copy", probe.Speed.Literal == 4f);

        var linked = new BtProbeParamAction();
        linked.EnsureId();
        Link(linked.Speed, speed);
        var tree = Tree(Node<SequenceNode>(linked));
        tree.Blackboard.Add(speed);

        var run = new TreeRun(tree);
        run.Tick();
        Check("a linked parameter reads the entry", ((BtProbeParamAction) run.Instance.Flat[1]).LastSpeed == 10f);
        Check("a linked parameter writes the entry", run.Board.Get<float>("speed") == 11f);

        run.Board.EraseById(speed.Id);
        run.Tick();
        Check("an erased entry falls back to the fixed value", ((BtProbeParamAction) run.Instance.Flat[1]).LastSpeed == 4f);
    }

    /// <summary>
    /// Value is read before BeforeRun and again before every tick, so a node that keeps running still
    /// sees what others wrote in between; assigning it writes straight through to the blackboard.
    /// </summary>
    void ValueIsCurrentBeforeRunAndEveryTick() {
        var speed = Entry("speed", Variant.Type.Float, 10.0);
        var probe = new BtProbeParamAction { RunTicks = 5 };
        probe.EnsureId();
        Link(probe.Speed, speed);
        var tree = Tree(Node<SequenceNode>(probe));
        tree.Blackboard.Add(speed);

        var run = new TreeRun(tree);
        var clone = (BtProbeParamAction) run.Instance.Flat[1];
        Check("before running, Value is the fixed value", clone.Speed.Value == 4f);

        run.Tick();
        Check("Value is current in BeforeRun", clone.SpeedAtBeforeRun == 10f && clone.LastSpeed == 10f);
        Check("assigning Value writes the entry", run.Board.Get<float>("speed") == 11f && clone.Speed.Value == 11f);

        run.Board.Set("speed", 20f);
        Check("a write by someone else shows up only on the next tick", clone.Speed.Value == 11f);
        run.Tick();
        Check("Value is read again on a tick without BeforeRun", clone.LastSpeed == 20f && clone.SpeedAtBeforeRun == 10f);

        clone.Speed.Value = 50f;
        Check("Value keeps its context between ticks", run.Board.Get<float>("speed") == 50f);
    }

    void ParametersSurviveSavingLoadingAndCloning() {
        var speed = Entry("speed", Variant.Type.Float, 1.0);
        var probe = new BtProbeParamAction();
        probe.EnsureId();
        Link(probe.Speed, speed);
        probe.Speed.Literal = 9f;
        var tree = Tree(Node<SequenceNode>(probe));
        tree.Blackboard.Add(speed);

        const string path = "user://missbehave_params.tres";
        Check("a tree with parameters saves", ResourceSaver.Save(tree, path) == Error.Ok);
        var text = ReadText(path);
        Check("entries are saved with the tree", text.Contains($"Id = \"{speed.Id}\"") && text.Contains("Name = \"speed\""));
        Check("a changed parameter is saved", text.Contains("Speed = {") && text.Contains($"\"entry\": \"{speed.Id}\""));
        Check("an unchanged parameter is left out", !text.Contains("Untouched"));

        var reloaded = ResourceLoader.Load<BehaviorTree>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
        var loaded = reloaded?.Root?.Children.FirstOrDefault() as BtProbeParamAction;
        Check("the entry loads", reloaded?.FindEntry(speed.Id)?.Name == "speed");
        Check("a parameter loads its link and value", loaded != null && loaded.Speed.EntryId == speed.Id
                                                      && loaded.Speed.EntryName == "speed" && loaded.Speed.Literal == 9f);

        var clone = (BtProbeParamAction) probe.CloneRuntime();
        Check("a runtime clone carries the parameters", clone.Speed.EntryId == speed.Id && clone.Speed.Literal == 9f);
        Check("a runtime clone has parameters of its own", !ReferenceEquals(clone.Speed, probe.Speed));

        var legacy = new BtProbeParamAction();
        legacy.Set(nameof(BtProbeParamAction.Speed), 2.5);
        Check("a bare value from before the member was a parameter still loads", legacy.Speed.Literal == 2.5f && !legacy.Speed.IsLinked);
    }

    /// <summary>
    /// Reloading the assembly rebuilds each C# object from its Variant-compatible members only, so the
    /// parameter objects would come back at their initial values. This walks the node through the
    /// same two callbacks Godot calls around a reload, with the parameters reset in between.
    /// </summary>
    void ParametersSurviveAnAssemblyReload() {
        var probe = new BtProbeParamAction();
        probe.Speed.Literal = 6f;
        probe.Speed.EntryId = "abc";
        probe.Speed.EntryName = "speed";

        probe.OnBeforeSerialize();
        probe.Speed = 4f;
        probe.OnAfterDeserialize();

        Check("parameters are restored after a reload", probe.Speed.Literal == 6f && probe.Speed.EntryId == "abc");

        // A runner in an open scene reloads too; its tree travels untyped and is picked up on first use.
        var tree = Tree(Node<SequenceNode>(Probe(BehaviorStatus.Success)));
        var runner = new BehaviorTreeRunner { Tree = tree };
        runner.OnBeforeSerialize();
        // The generated save reads the Tree property first, then the fields. Reading it must not pull
        // the tree back in: on restore, Tree is assigned before the stash returns, and a saved tree
        // then looks like a change and connects the tree's "changed" signal a second time.
        Check("the reload saves no typed tree through the property", runner.Get("Tree").VariantType == Variant.Type.Nil);
        Check("a runner hands its tree over untyped for a reload", runner.Get("_tree").VariantType == Variant.Type.Nil);
        runner.OnAfterDeserialize();
        Check("a runner finds its tree again after a reload", ReferenceEquals(runner.Tree, tree));
        runner.Free();
    }

    void ParametersNeedNoInitializer() {
        var probe = new BtProbeParamAction();
        Check("a plain value initializes a parameter", probe.Speed.Literal == 4f && !probe.Speed.IsLinked);
        Check("a parameter without initializer is created by the node", probe.Target != null && probe.Target.Literal == null);
        Check("a get-only parameter without initializer is created too", probe.Hits != null);

        probe.EnsureId();
        var clone = (BtProbeParamAction) Run(Node<SequenceNode>(probe)).Instance.Flat[1];
        Check("a runtime clone has every parameter, stored or not", clone.Target != null && clone.Hits != null
                                                                    && !ReferenceEquals(clone.Hits, probe.Hits));
    }

    void ParameterTypesDecideWhatTheyCanLinkTo() {
        Check("float links to float", BbTypes.Accepts(typeof(float), Entry("a", Variant.Type.Float, 0.0)));
        Check("float does not link to String", !BbTypes.Accepts(typeof(float), Entry("a", Variant.Type.String, "")));
        Check("Variant links to anything", BbTypes.Accepts(typeof(Variant), Entry("a", Variant.Type.Vector3, Vector3.Zero)));
        Check("a node type links to its base and to subclasses",
            BbTypes.Accepts(typeof(Node3D), ObjectEntry("Node")) && BbTypes.Accepts(typeof(Node), ObjectEntry("Node3D")));
        Check("a node type does not link to a resource", !BbTypes.Accepts(typeof(Node3D), ObjectEntry("Resource")));
        Check("script classes resolve", BbTypes.IsA(nameof(BtProbeParamAction), "Resource"));
        // The editor's class picker answers with the script path for script classes.
        var picked = ObjectEntry("res://addons/missbehave/runtime/BehaviorTreeRunner.cs");
        Check("a picked script path is stored as its class name", picked.ClassName == nameof(BehaviorTreeRunner) && picked.IsNode);
        Check("a script path that is no global class is kept",
            ObjectEntry("res://nowhere/Missing.cs").ClassName == "res://nowhere/Missing.cs");
        Check("node parameters are entry-only", BbParams.Find(typeof(BtProbeParamAction), "Target")?.EntryOnly == true
                                                && BbParams.Find(typeof(BtProbeParamAction), "Speed")?.EntryOnly == false);
        Check("non-public parameters are found too", BbParams.Find(typeof(BtProbeParamAction), "Untouched") != null);
    }

    /// <summary>
    /// A runner fills in what a tree resource cannot hold — scene nodes — and can override defaults.
    /// Both have to survive being packed into a scene file, keyed by entry id.
    /// </summary>
    void RunnerOverridesSurviveASceneFile() {
        var target = ObjectEntry("Node3D");
        target.Name = "target";
        var speed = Entry("speed", Variant.Type.Float, 1.0);
        var probe = new BtProbeParamAction();
        probe.EnsureId();
        Link(probe.Speed, speed);
        Link(probe.Target, target);
        var tree = Tree(Node<SequenceNode>(probe));
        tree.Blackboard.Add(target);
        tree.Blackboard.Add(speed);

        var scene = new Node3D { Name = "Actor" };
        var goal = new Node3D { Name = "Goal" };
        var runner = new BehaviorTreeRunner { Name = "Runner", Tree = tree, Thread = BehaviorTreeRunner.ProcessThread.Manual };
        scene.AddChild(goal);
        scene.AddChild(runner);
        goal.Owner = scene;
        runner.Owner = scene;
        AddChild(scene);

        runner.Set(BehaviorTreeRunner.BlackboardGroup + "speed", 1.0);
        Check("setting a runner field to the default stores nothing", !runner.TryGetOverride(speed.Id, out _));
        runner.Set(BehaviorTreeRunner.BlackboardGroup + "speed", 3.0);
        runner.Set(BehaviorTreeRunner.BlackboardGroup + "target", goal);
        Check("runner fields are listed per entry", runner.GetPropertyList().Any(p => p["name"].AsString() == "Blackboard/target"));

        var packed = new PackedScene();
        Check("the scene packs", packed.Pack(scene) == Error.Ok);
        const string path = "user://missbehave_runner.tscn";
        Check("the scene saves", ResourceSaver.Save(packed, path) == Error.Ok);
        var text = ReadText(path);
        Check("overrides are stored by entry id",
            text.Contains($"{BehaviorTreeRunner.OverridePrefix}{speed.Id} = 3.0") && !text.Contains("Blackboard/"));
        Check("a node override is stored as a node path",
            text.Contains($"{BehaviorTreeRunner.OverridePrefix}{target.Id} = NodePath(\"../Goal\")"));
        scene.QueueFree();

        // Renamed after saving: the stored values must still find their entries.
        target.Name = "goal";
        var loaded = ResourceLoader.Load<PackedScene>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
        var instance = loaded?.Instantiate<Node3D>();
        if (instance == null) {
            Check("the saved scene instantiates", false);
            return;
        }
        var loadedRunner = instance.GetNode<BehaviorTreeRunner>("Runner");
        loadedRunner.Tree = tree;
        AddChild(instance);
        loadedRunner.Tick(Step);

        var clone = (BtProbeParamAction) loadedRunner.Instance.Flat[1];
        Check("a node override reaches the tree after loading", ReferenceEquals(clone.LastTarget, instance.GetNode("Goal")));
        Check("a value override reaches the tree after loading", clone.LastSpeed == 3f);
        Check("overrides follow a renamed entry", loadedRunner.Get(BehaviorTreeRunner.BlackboardGroup + "goal").AsGodotObject() == instance.GetNode("Goal"));
        instance.QueueFree();
    }

    static BlackboardEntry Entry(string name, Variant.Type type, Variant value)
        => new() { Name = name, VariantType = type, Default = value };

    static BlackboardEntry ObjectEntry(string className)
        => new() { Name = className, VariantType = Variant.Type.Object, ClassName = className };

    static void Link(IBbParam param, BlackboardEntry entry) {
        param.EntryId = entry.Id;
        param.EntryName = entry.Name;
    }

    // ---- runner ------------------------------------------------------------------------------

    /// <summary>
    /// A leaf stopping the tree from inside its own tick: the tick has to finish normally — the
    /// sibling after it still runs — and only then is the tree interrupted and switched off.
    /// </summary>
    void ALeafCanStopItsRunner() {
        var stopper = new BtProbeStopAction();
        stopper.EnsureId();
        var runner = new BehaviorTreeRunner {
            Tree = Tree(Node<SequenceNode>(stopper, Probe(BehaviorStatus.Success, runningTicks: 10))),
            Thread = BehaviorTreeRunner.ProcessThread.Manual,
        };
        AddChild(runner);

        var status = runner.Tick(Step);
        var stopClone = (BtProbeStopAction) runner.Instance.Flat[1];
        var running = (BtProbeAction) runner.Instance.Flat[2];

        Check("a leaf sees its runner through the context", ReferenceEquals(stopClone.SeenRunner, runner));
        Check("stopping from a leaf disables the runner", !runner.Enabled);
        // The probe zeroes Ticks when interrupted, so BeforeRuns is what proves it was reached.
        Check("the stopping tick still runs to its end", running.BeforeRuns == 1 && status == BehaviorStatus.Failure);
        Check("the running branch is interrupted after the tick", running.Interrupts == 1);

        runner.Tick(Step);
        Check("a stopped runner ignores further ticks", running.BeforeRuns == 1 && running.Interrupts == 1);

        runner.Enabled = true;
        runner.Tick(Step);
        Check("re-enabling resumes the tree", running.BeforeRuns == 2);

        runner.QueueFree();
    }

    // ---- live debugging ----------------------------------------------------------------------

    void FrameThrottleSuppressesRepeatsButNotAfterASwitch() {
        const ulong interval = 33;
        var throttle = new FrameThrottle();

        byte[] running = [2, 2];
        byte[] finished = [0, 2];

        Check("the first frame is sent", throttle.TryTake(running, 1000, interval));
        Check("an identical frame is suppressed", !throttle.TryTake(running, 2000, interval));
        Check("a changed frame within the rate cap waits", !throttle.TryTake(finished, 1010, interval));
        Check("a changed frame after the cap is sent", !throttle.TryTake(running, 2000, interval)
                                                       && throttle.TryTake(finished, 2100, interval));

        // Switching which runner the editor watches: the new one has been ticking along unwatched,
        // so its current frame matches what it last sent. Without invalidation the identical-frame
        // rule would suppress it forever and the graph would never colour again.
        Check("an unchanged frame is still suppressed before invalidating",
            !throttle.TryTake(finished, 3000, interval));
        throttle.Invalidate();
        Check("after invalidating, the same frame is sent again",
            throttle.TryTake(finished, 3001, interval));
    }

    /// <summary>
    /// Two enemies share a tree. Pausing the game stops every tick, and killing the watched enemy
    /// removes the runner the stream is filtered on — either used to leave the graph blank.
    /// </summary>
    void LiveDebugKeepsStreamingWhenPausedOrTheWatchedRunnerDies() {
        var sent = new System.Collections.Generic.List<(string Message, Godot.Collections.Array Data)>();
        ulong now = 1000;
        var stream = new DebugStream((message, data) => sent.Add((message, data)), () => now);

        var tree = Tree(Node<SequenceNode>(Probe(BehaviorStatus.Success, runningTicks: 100)));
        tree.ResourcePath = "res://missbehave_debug_stream_probe.tres";
        var first = new BehaviorTreeRunner { Tree = tree, Thread = BehaviorTreeRunner.ProcessThread.Manual };
        var second = new BehaviorTreeRunner { Tree = tree, Thread = BehaviorTreeRunner.ProcessThread.Manual };
        AddChild(first);
        AddChild(second);
        var firstId = (long) first.GetInstanceId();
        var secondId = (long) second.GetInstanceId();

        int FramesOf(long id) => sent.Count(s => s.Message == "frame" && s.Data[0].AsInt64() == id);
        void TickBoth() {
            foreach (var runner in new[] { first, second }) {
                if (!IsInstanceValid(runner) || runner.IsQueuedForDeletion()) continue;
                runner.Tick(Step);
                stream.SendFrame(runner);
            }
        }

        stream.Register(first);
        stream.Register(second);
        stream.OnEditorMessage("watch_path", [tree.ResourcePath]);
        stream.OnEditorMessage("watch_instance", [firstId]);
        TickBoth();
        Check("only the watched runner streams", FramesOf(firstId) == 1 && FramesOf(secondId) == 0);

        // Paused: nothing ticks from here on. The editor switches instance and clears its colours.
        sent.Clear();
        stream.OnEditorMessage("watch_instance", [secondId]);
        Check("switching while paused sends the newly watched runner's last frame at once",
            FramesOf(secondId) == 1 && FramesOf(firstId) == 0);

        sent.Clear();
        stream.OnEditorMessage("visible", [true]);
        Check("the editor becoming visible again repaints without a tick", FramesOf(secondId) == 1);

        // The editor's assembly reloaded mid-game and it forgot every runner: asked to announce them,
        // the game repeats every registration and what the watched runner last showed — still paused.
        sent.Clear();
        Check("the game understands a request to announce its runners", stream.OnEditorMessage("announce", []));
        Check("announcing repeats every registration",
            sent.Count(s => s.Message == "register") == 2
            && sent.Any(s => s.Message == "register" && s.Data[0].AsInt64() == firstId));
        Check("announcing resends the watched runner's last frame without a tick",
            FramesOf(secondId) == 1 && FramesOf(firstId) == 0
            && sent.FindLastIndex(s => s.Message == "register") < sent.FindIndex(s => s.Message == "frame"));

        stream.OnEditorMessage("watch_instance", [firstId]);
        sent.Clear();
        stream.Unregister(first);
        first.QueueFree();
        Check("the dying runner is announced", sent.Any(s => s.Message == "unregister" && s.Data[0].AsInt64() == firstId));
        Check("the filter on the dead runner is dropped", stream.WatchedInstance == -1);

        now += 100;
        TickBoth();
        Check("the surviving runner streams again before the editor even answers", FramesOf(secondId) == 1);

        stream.OnEditorMessage("watch_instance", [secondId]);
        Check("the editor's answer keeps it streaming", stream.WatchedInstance == secondId);

        stream.Unregister(second);
        second.QueueFree();
        tree.ResourcePath = "";
    }

    // ---- instance isolation ------------------------------------------------------------------

    void DefinitionIsNeverMutatedByTicking() {
        var leaf = Probe(BehaviorStatus.Success);
        var root = Node<SequenceNode>(leaf);
        var run = Run(root);
        run.Tick();

        Check("definition stays flagged as a definition", root.IsDefinition && leaf.IsDefinition);
        Check("definition leaf was never ticked", leaf.Ticks == 0);
        Check("clone is flagged as runtime", !run.Instance.Root.IsDefinition);
        Check("clone points back at its definition", run.Instance.Root.DefinitionId == root.Id);
    }

    void TwoInstancesOfOneDefinitionAreIndependent() {
        var definition = Tree(Node<SequenceNode>(
            Probe(BehaviorStatus.Success, runningTicks: 3),
            Probe(BehaviorStatus.Success)));

        var left = new TreeRun(definition);
        var right = new TreeRun(definition);

        left.Tick();
        left.Tick();

        Check("instances hold separate leaf state", left.Probe(1).Ticks == 2 && right.Probe(1).Ticks == 0);
        Check("instances hold separate clones", !ReferenceEquals(left.Probe(1), right.Probe(1)));

        right.Tick();
        Check("driving one instance does not advance the other", left.Probe(1).Ticks == 2);
    }

    void ExportedResourcesStaySharedAcrossClones() {
        var shared = new BtProbeAsset { Value = 42 };
        var leaf = new BtProbeAssetUser { Asset = shared };
        leaf.EnsureId();

        var run = Run(Node<SequenceNode>(leaf));
        var clone = (BtProbeAssetUser) run.Instance.Flat[1];

        Check("exported resources are shared, not deep-copied", ReferenceEquals(clone.Asset, shared));
    }

    void ResourceRoundTripKeepsStructure() {
        var leaf = Probe(BehaviorStatus.Failure);
        leaf.GraphPosition = new Vector2(120, 40);
        var root = Node<SelectorNode>(leaf);
        root.GraphPosition = new Vector2(60, -40);
        var tree = Tree(root);

        const string path = "user://missbehave_roundtrip.tres";
        Check("tree saves without error", ResourceSaver.Save(tree, path) == Error.Ok);

        var text = ReadText(path);
        Check("children serialise as built-in sub-resources", text.Contains("[sub_resource"));
        Check("no external node files are referenced", !text.Contains(".tres\" id="));

        var reloaded = ResourceLoader.Load<BehaviorTree>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
        Check("tree reloads", reloaded?.Root != null);
        if (reloaded?.Root == null) return;

        Check("root type survives", reloaded.Root is SelectorNode);
        Check("child count survives", reloaded.Root.Children.Count == 1);
        Check("ids survive", reloaded.Root.Id == root.Id);
        Check("graph positions survive", reloaded.Root.GraphPosition == root.GraphPosition
                                         && reloaded.Root.Children[0].GraphPosition == leaf.GraphPosition);
        Check("exported leaf parameters survive",
            reloaded.Root.Children[0] is BtProbeAction probe && probe.Result == BehaviorStatus.Failure);
    }

    /// <summary>
    /// The case that actually bites in a game: one .tres assigned to several runners. Loading it
    /// twice hands out the very same object, so the clone step is the only thing keeping two
    /// enemies from sharing a running-child index.
    /// </summary>
    void SharedTreeResourceStillGivesSeparateInstances() {
        const string path = "res://missbehave demo/demo_tree.tres";
        var first = ResourceLoader.Load<BehaviorTree>(path);
        var second = ResourceLoader.Load<BehaviorTree>(path);

        Check("loading a tree twice returns the shared resource", ReferenceEquals(first, second));
        if (first?.Root == null) {
            Check("demo tree loads with a root", false);
            return;
        }

        var left = new TreeRun(first);
        var right = new TreeRun(second);
        left.Tick();
        left.Tick();
        right.Tick();

        var separate = true;
        var clonesDifferFromDefinition = true;
        for (var i = 0; i < left.Instance.Flat.Length; i++) {
            if (ReferenceEquals(left.Instance.Flat[i], right.Instance.Flat[i])) separate = false;
            if (left.Instance.Flat[i].IsDefinition) clonesDifferFromDefinition = false;
        }

        Check("runners sharing a resource get separate node clones", separate);
        Check("no clone is still flagged as a definition", clonesDifferFromDefinition);
        Check("the shared definition is untouched", first.Root.IsDefinition);
        Check("clones carry no resource path of their own",
            string.IsNullOrEmpty(left.Instance.Root.ResourcePath));
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>One runtime instance plus its blackboard, ticked the way the runner ticks it.</summary>
    sealed class TreeRun {
        public readonly BehaviorTreeInstance Instance;
        public readonly Blackboard Board = new();

        BehaviorStatus _status = BehaviorStatus.Failure;

        public TreeRun(BehaviorTree tree) {
            Instance = BehaviorTreeInstance.Create(tree);
            Board.Declare(tree?.Blackboard);
        }

        public BehaviorStatus Tick(double delta = Step) {
            Instance.BeginFrame();
            var ctx = new BtContext { Blackboard = Board, Delta = delta, Instance = Instance };

            var root = Instance.Root;
            if (_status != BehaviorStatus.Running) root.BeforeRunInternal(ctx);
            _status = root.TickInternal(ctx);
            if (_status != BehaviorStatus.Running) root.AfterRun(ctx);
            return _status;
        }

        public BtProbeAction Probe(int runtimeIndex) => (BtProbeAction) Instance.Flat[runtimeIndex];
    }

    static TreeRun Run(ABehaviorNode root) => new(Tree(root));

    static BtProbeAction Probe(BehaviorStatus result, int runningTicks = 0) {
        var probe = new BtProbeAction { Result = result, RunningTicks = runningTicks };
        probe.EnsureId();
        return probe;
    }

    static T Node<T>(params ABehaviorNode[] children) where T : ABehaviorNode, new() {
        var node = new T();
        node.EnsureId();
        foreach (var child in children) node.Children.Add(child);
        return node;
    }

    static BehaviorTree Tree(ABehaviorNode root) => new() { Root = root };

    static string ReadText(string path) {
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        return file?.GetAsText() ?? "";
    }

    void Check(string what, bool condition) {
        _checks++;
        if (!condition) _failures.Add(what);
    }
}
