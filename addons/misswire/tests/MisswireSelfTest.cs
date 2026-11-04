using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Misswire.Tests;

namespace Misswire;

/// <summary>
/// Headless self test for the runtime. Builds scenes in code and exits with code 0 on success,
/// 1 on failure.
/// <code>
/// godot --headless --path &lt;project&gt; res://addons/misswire/tests/self_test.tscn
/// </code>
/// </summary>
public partial class MisswireSelfTest : Node {
    readonly List<string> _failures = [];
    int _checks;

    public override async void _Ready() {
        await AValueGoesOutOnceTheSceneIsReady();
        await ANewValueRunsTheStepsInOrder();
        await SignalArgumentsArePassedOn();
        await ArgumentsCanBePickedAndFixed();
        await AResourceSignalFollowsTheProperty();
        await APropertyInsideAResourceFollowsBoth();
        await AStepCanActOnAResource();
        await AStepTakesItsValueThroughThePort();
        await ACSharpDefaultFillsInTheRest();
        await AResourceSignalNoticesANewResourceBySignal();
        await LeavingTheTreeDisconnectsAndComingBackReconnects();
        await ProblemsAreReportedOnceAndTheRestRuns();
        await AFreedSourceOrTargetIsNoProblem();
        AGraphSurvivesSavingAndLoading();

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"misswire self test: {_checks - _failures.Count}/{_checks} checks passed");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    // ---- fixture -------------------------------------------------------------------------------

    sealed class Fixture {
        public Node Root;
        public MissMediator Mediator;
        public WireProbeModel Model;
        public WireProbeController Controller;
        public readonly List<string> Warnings = [];
        public List<string> Log => Controller.Log;
    }

    /// <summary>The mediator comes first, so its siblings are not ready yet when it wires up.</summary>
    Fixture Build(params MissBinding[] bindings) {
        var f = new Fixture {
            Root = new Node { Name = "Scene" },
            Mediator = new MissMediator { Name = "Mediator", Graph = new MissBindingGraph() },
            Model = new WireProbeModel { Name = "Model" },
            Controller = new WireProbeController { Name = "Controller" },
        };
        foreach (var binding in bindings) f.Mediator.Graph.Bindings.Add(binding);
        f.Mediator.Warned += message => f.Warnings.Add(message);
        f.Root.AddChild(f.Mediator);
        f.Root.AddChild(f.Model);
        f.Root.AddChild(f.Controller);
        return f;
    }

    static MissBinding OutfitBinding(params MissBindingStep[] steps) => Binding(new MissBinding {
        Kind = MissSourceKind.Property, Source = "../Model", Property = "Outfit", ChangeSignal = "OutfitChanged",
    }, steps);

    static MissBinding Binding(MissBinding binding, params MissBindingStep[] steps) {
        foreach (var step in steps) binding.Steps.Add(step);
        return binding;
    }

    static MissBindingStep Call(string method, params MissStepArgument[] arguments) => Step(MissStepKind.Call, method, arguments);
    static MissBindingStep Set(string property) => Step(MissStepKind.Set, property, []);

    static MissBindingStep Step(MissStepKind kind, string member, MissStepArgument[] arguments) {
        var step = new MissBindingStep { Kind = kind, Target = "../Controller", Member = member };
        foreach (var argument in arguments) step.Arguments.Add(argument);
        return step;
    }

    static WireProbeOutfit Outfit(string hair) => new() { Hair = hair };

    async Task Frames(int count = 1) {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    // ---- tests ---------------------------------------------------------------------------------

    async Task AValueGoesOutOnceTheSceneIsReady() {
        var f = Build(OutfitBinding(Set("Outfit"), Call("ApplyOutfit")));
        f.Model.Outfit = Outfit("red");
        AddChild(f.Root);
        Check("nothing goes out while the scene is still being readied", f.Log.Count == 0);
        await Frames();
        Check("the first value goes out once everything is ready", f.Log.SequenceEqual(["set red", "apply red"]));

        var off = Build(Binding(OutfitBinding(Set("Outfit")), []));
        off.Mediator.Graph.Bindings[0].PushInitial = false;
        off.Model.Outfit = Outfit("red");
        AddChild(off.Root);
        await Frames();
        Check("without PushInitial nothing goes out at the start", off.Log.Count == 0);
        f.Root.QueueFree();
        off.Root.QueueFree();
    }

    async Task ANewValueRunsTheStepsInOrder() {
        var f = Build(OutfitBinding(Set("Outfit"), Call("ApplyOutfit")));
        AddChild(f.Root);
        await Frames();
        f.Log.Clear();

        f.Model.Outfit = Outfit("blue");
        Check("the change signal runs the steps top to bottom, right away", f.Log.SequenceEqual(["set blue", "apply blue"]));
        f.Model.Outfit = f.Model.Outfit;
        Check("no change signal, no steps", f.Log.Count == 2);
        f.Root.QueueFree();

        var named = OutfitBinding(Call("ApplyOutfit"));
        named.ChangeSignal = "";
        var g = Build(named);
        AddChild(g.Root);
        await Frames();
        g.Log.Clear();
        g.Model.Outfit = Outfit("by name");
        Check("left empty, the change signal is found by its name", g.Log.Count == 1);
        g.Root.QueueFree();

        var once = OutfitBinding(Call("ApplyOutfit"));
        once.FollowChanges = false;
        var h = Build(once);
        AddChild(h.Root);
        await Frames();
        h.Model.Outfit = Outfit("later");
        Check("not following changes, the game gets the first value only", h.Log.SequenceEqual(["apply "]));
        h.Mediator.Graph.Bindings[0].Triggers.Add("../Model:Hit");
        h.Root.GetParent().RemoveChild(h.Root);
        AddChild(h.Root);
        await Frames();
        h.Log.Clear();
        h.Model.EmitSignal(WireProbeModel.SignalName.Hit, 1, "x");
        Check("a further trigger sets a property binding off", h.Log.Count == 1);
        h.Root.QueueFree();
    }

    async Task SignalArgumentsArePassedOn() {
        var f = Build(Binding(new MissBinding { Kind = MissSourceKind.Signal, Source = "../Model", Signal = "Hit" }, Call("Damage")));
        AddChild(f.Root);
        await Frames();
        Check("a plain signal sends nothing at the start", f.Log.Count == 0);
        f.Model.EmitSignal(WireProbeModel.SignalName.Hit, 5, "orc");
        Check("the signal's arguments are passed on as they are", f.Log.SequenceEqual(["damage 5 by orc"]));
        f.Root.QueueFree();
    }

    async Task ArgumentsCanBePickedAndFixed() {
        var picked = new MissStepArgument { Source = MissArgumentSource.Input, Index = 1 };
        var fixedOne = new MissStepArgument { Source = MissArgumentSource.Constant, Constant = "always" };
        var f = Build(Binding(new MissBinding { Kind = MissSourceKind.Signal, Source = "../Model", Signal = "Hit" },
            Call("Note", picked, fixedOne)));
        AddChild(f.Root);
        await Frames();
        f.Model.EmitSignal(WireProbeModel.SignalName.Hit, 5, "orc");
        Check("an argument can be picked from the input and one fixed", f.Log.SequenceEqual(["note orc always"]));
        f.Root.QueueFree();
    }

    async Task AResourceSignalFollowsTheProperty() {
        var f = Build(Binding(new MissBinding {
            Kind = MissSourceKind.ResourceSignal, Source = "../Model", Property = "Outfit", Signal = "changed",
            ChangeSignal = "OutfitChanged", PushInitial = false,
        }, Call("ApplyOutfit")));
        var first = Outfit("red");
        f.Model.Outfit = first;
        f.Controller.Outfit = first;
        AddChild(f.Root);
        await Frames();
        f.Log.Clear();

        first.Change("green");
        Check("a change inside the resource runs the steps", f.Log.SequenceEqual(["apply green"]));

        var second = Outfit("black");
        f.Model.Outfit = second;
        f.Controller.Outfit = second;
        f.Log.Clear();
        first.Change("pink");
        Check("the old resource is let go once the property holds another", f.Log.Count == 0);
        second.Change("white");
        Check("the new resource is listened to instead", f.Log.SequenceEqual(["apply white"]));

        f.Model.Outfit = null;
        f.Log.Clear();
        second.Change("grey");
        Check("an empty property listens to nothing", f.Log.Count == 0 && f.Warnings.Count == 0);
        f.Root.QueueFree();
    }

    async Task APropertyInsideAResourceFollowsBoth() {
        var f = Build(Binding(new MissBinding { Kind = MissSourceKind.Property, Source = "../Model", Property = "Outfit:Hair" }, Call("Seen")));
        var red = Outfit("red");
        f.Model.Outfit = red;
        AddChild(f.Root);
        await Frames();
        Check("a property inside a resource sends its first value", f.Log.SequenceEqual(["seen red"]));

        red.Change("green");
        Check("a change inside the resource reaches the steps", f.Log.Last() == "seen green");
        var blue = Outfit("blue");
        f.Model.Outfit = blue;
        Check("so does another resource in the property (OutfitChanged by name)", f.Log.Last() == "seen blue");
        f.Log.Clear();
        red.Change("pink");
        Check("the old resource is let go", f.Log.Count == 0);
        blue.Change("white");
        Check("the new one is listened to", f.Log.SequenceEqual(["seen white"]) && f.Warnings.Count == 0);
        f.Root.QueueFree();
    }

    async Task AStepCanActOnAResource() {
        var picked = new MissStepArgument { Source = MissArgumentSource.Input, Index = 1 };
        var set = new MissBindingStep { Kind = MissStepKind.Set, Target = "../Model:Outfit", Member = "Hair" };
        set.Arguments.Add(picked);
        var call = new MissBindingStep { Kind = MissStepKind.Call, Target = "../Model:Outfit", Member = "Change" };
        call.Arguments.Add(new MissStepArgument { Source = MissArgumentSource.Constant, Constant = "called" });
        var f = Build(Binding(new MissBinding { Kind = MissSourceKind.Signal, Source = "../Model", Signal = "Hit" }, set));
        var g = Build(Binding(new MissBinding { Kind = MissSourceKind.Signal, Source = "../Model", Signal = "Hit" }, call));
        f.Model.Outfit = Outfit("red");
        g.Model.Outfit = Outfit("red");
        AddChild(f.Root);
        AddChild(g.Root);
        await Frames();
        f.Model.EmitSignal(WireProbeModel.SignalName.Hit, 5, "orc");
        g.Model.EmitSignal(WireProbeModel.SignalName.Hit, 5, "orc");
        Check("a step sets a property of the resource a node holds", f.Model.Outfit.Hair == "orc");
        Check("and calls its methods", g.Model.Outfit.Hair == "called");
        f.Root.QueueFree();
        g.Root.QueueFree();
    }

    async Task AStepTakesItsValueThroughThePort() {
        var fromProperty = new MissBindingStep { Target = "../Controller", Member = "Seen", ValueFrom = MissStepValue.Port, ValueSource = "../Model:Outfit:Hair" };
        var fromConstant = new MissBindingStep { Target = "../Controller", Member = "Seen", ValueFrom = MissStepValue.Port, ValueConstant = "c1" };
        var f = Build(Binding(new MissBinding { Kind = MissSourceKind.Signal, Source = "../Model", Signal = "Hit" }, fromProperty, fromConstant));
        f.Mediator.Graph.Constants.Add(new MissGraphConstant { Id = "c1", Value = "fixed" });
        f.Model.Outfit = Outfit("blond");
        AddChild(f.Root);
        await Frames();
        f.Model.EmitSignal(WireProbeModel.SignalName.Hit, 5, "orc");
        Check("a step reads a wired property when it runs, not the signal's value", f.Log.SequenceEqual(["seen blond", "seen fixed"]));
        f.Mediator.Graph.Constants[0].Value = new NodePath("../Controller");
        f.Log.Clear();
        f.Model.EmitSignal(WireProbeModel.SignalName.Hit, 5, "orc");
        Check("a constant of type Node hands on the node", f.Log.Count == 2 && f.Log[1] == $"seen <Node#{f.Controller.GetInstanceId()}>");

        var cape = Outfit("cape");
        f.Mediator.Graph.Constants[0].Value = cape;
        f.Mediator.Graph.Constants[0].HoldsResource = true;
        f.Mediator.Graph.Bindings[0].Steps[1].Kind = MissStepKind.Set;
        f.Mediator.Graph.Bindings[0].Steps[1].Member = "Outfit";
        f.Model.EmitSignal(WireProbeModel.SignalName.Hit, 5, "orc");
        Check("a constant of type Resource hands on the resource", f.Controller.Outfit == cape);
        f.Mediator.Graph.Constants[0].Value = default;
        f.Log.Clear();
        f.Model.EmitSignal(WireProbeModel.SignalName.Hit, 5, "orc");
        Check("and without one the step is skipped and reported", f.Log.Count == 1 && f.Warnings.Any(w => w.Contains("no resource yet")));
        f.Root.QueueFree();
    }

    async Task AResourceSignalNoticesANewResourceBySignal() {
        var swap = Binding(new MissBinding { Kind = MissSourceKind.Property, Source = "../Model", Property = "Cape", PushInitial = false }, Call("Seen"));
        swap.Triggers.Add("../Model:CapeSwapped");
        var inside = Binding(new MissBinding {
            Kind = MissSourceKind.ResourceSignal, Source = "../Model", Property = "Cape", Signal = "changed", PushInitial = false,
        }, Call("ApplyOutfit"));
        var f = Build(swap, inside);
        var first = Outfit("first");
        f.Model.Cape = first;
        AddChild(f.Root);
        await Frames();
        f.Log.Clear();
        first.Change("x");
        Check("a change inside the resource runs the steps", f.Log.Count(l => l.StartsWith("apply")) == 1);

        var second = Outfit("second");
        f.Model.Cape = second;
        f.Log.Clear();
        first.Change("y");
        Check("after a new resource, the old one sets nothing off", f.Log.Count(l => l.StartsWith("apply")) == 0);
        second.Change("z");
        Check("the new one does: a trigger of a binding on the same property says it was swapped", f.Log.Count(l => l.StartsWith("apply")) == 1);
        f.Root.QueueFree();

        var g = Build(Binding(new MissBinding {
            Kind = MissSourceKind.ResourceSignal, Source = "../Model", Property = "Hat", Signal = "changed", PushInitial = false,
        }, Call("ApplyOutfit")));
        var old = Outfit("old");
        g.Model.Hat = old;
        AddChild(g.Root);
        await Frames();
        g.Model.Hat = Outfit("new");
        g.Log.Clear();
        old.Change("y");
        Check("with nothing saying so, a resource no longer held still sets nothing off", g.Log.Count == 0);
        g.Model.Hat.Change("z");
        Check("and the binding has moved over to the new one by then", g.Log.Count == 1);
        g.Root.QueueFree();
    }

    async Task ACSharpDefaultFillsInTheRest() {
        var f = Build(Binding(new MissBinding { Kind = MissSourceKind.Property, Source = "../Model", Property = "Outfit:Hair" }, Call("Greet")));
        f.Model.Outfit = Outfit("Bob");
        AddChild(f.Root);
        await Frames();
        Check("a C# method gets its own default for what the step leaves out", f.Log.SequenceEqual(["greet Bob x1"]) && f.Warnings.Count == 0);
        f.Root.QueueFree();
    }

    async Task LeavingTheTreeDisconnectsAndComingBackReconnects() {
        var f = Build(OutfitBinding(Call("ApplyOutfit")));
        AddChild(f.Root);
        await Frames();
        RemoveChild(f.Root);
        f.Log.Clear();

        f.Model.Outfit = Outfit("out");
        Check("outside the tree nothing runs", f.Log.Count == 0);

        AddChild(f.Root);
        await Frames();
        Check("coming back sends the current value again", f.Log.SequenceEqual(["apply "]));
        f.Log.Clear();
        f.Model.Outfit = Outfit("back");
        Check("coming back connects again, once", f.Log.Count == 1);

        RemoveChild(f.Root);
        AddChild(f.Root);
        await Frames();
        f.Log.Clear();
        f.Model.Outfit = Outfit("again");
        Check("going out and in twice still connects once", f.Log.Count == 1);
        f.Root.QueueFree();
    }

    async Task ProblemsAreReportedOnceAndTheRestRuns() {
        var missingTarget = new MissBindingStep { Target = "../Nobody", Member = "ApplyOutfit" };
        var f = Build(
            OutfitBinding(missingTarget, Call("NoSuchMethod"), Call("Damage"), Call("ApplyOutfit")),
            Binding(new MissBinding { Kind = MissSourceKind.Signal, Source = "../Model", Signal = "NoSuchSignal" }),
            Binding(new MissBinding { Kind = MissSourceKind.Signal, Source = "../Nowhere", Signal = "Hit" }),
            Binding(new MissBinding { Kind = MissSourceKind.Signal, Source = "../Model", Signal = "Crowded" }));
        AddChild(f.Root);
        await Frames();
        f.Model.Outfit = Outfit("x");

        Check("the step after the broken ones still runs, at the start and on the change", f.Log.Count(l => l.StartsWith("apply")) == 2);
        Check("a missing target is reported", f.Warnings.Any(w => w.Contains("nothing at '../Nobody'")));
        Check("a missing method is reported", f.Warnings.Any(w => w.Contains("no method 'NoSuchMethod'")));
        Check("a missing signal is reported", f.Warnings.Any(w => w.Contains("no signal 'NoSuchSignal'")));
        Check("a missing source is reported", f.Warnings.Any(w => w.Contains("no node at '../Nowhere'")));
        Check("a signal with too many arguments is reported", f.Warnings.Any(w => w.Contains("7 arguments")));
        Check("a method that needs more than it gets is reported", f.Warnings.Any(w => w.Contains("'Damage' needs 2 arguments, gets 1")));
        Check("each problem is reported once", f.Warnings.Count == f.Warnings.Distinct().Count() && f.Warnings.Count == 6);
        f.Root.QueueFree();
    }

    async Task AFreedSourceOrTargetIsNoProblem() {
        var f = Build(OutfitBinding(Call("ApplyOutfit")));
        AddChild(f.Root);
        await Frames();
        f.Controller.Free();
        f.Model.Outfit = Outfit("y");
        Check("a freed target is reported, not crashed on", f.Warnings.Any(w => w.Contains("nothing at '../Controller'")));
        f.Model.Free();
        await Frames();
        Check("a freed source leaves the mediator working", IsInstanceValid(f.Mediator));
        f.Root.QueueFree();
    }

    void AGraphSurvivesSavingAndLoading() {
        var graph = new MissBindingGraph();
        graph.Bindings.Add(Binding(new MissBinding {
            Kind = MissSourceKind.ResourceSignal, Source = "../Model", Property = "Outfit", Signal = "changed",
            ChangeSignal = "OutfitChanged", PushInitial = false, EditorActive = true, EditorTrigger = MissEditorTrigger.EveryEdit,
        }, Call("Note", new MissStepArgument { Source = MissArgumentSource.Constant, Constant = 42 }), Set("Outfit")));
        const string path = "user://misswire_self_test.tres";
        var saved = ResourceSaver.Save(graph, path);
        var loaded = ResourceLoader.Load<MissBindingGraph>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
        var b = loaded?.Bindings.FirstOrDefault();
        Check("a graph saves", saved == Error.Ok);
        Check("a binding keeps its source", b is { Kind: MissSourceKind.ResourceSignal } && b.Source == "../Model"
            && b.Property == "Outfit" && b.Signal == "changed" && b.ChangeSignal == "OutfitChanged");
        Check("a binding keeps its flags", b is { PushInitial: false, EditorActive: true, EditorTrigger: MissEditorTrigger.EveryEdit }
            && b.Id == graph.Bindings[0].Id);
        Check("a binding keeps its steps", b?.Steps.Count == 2 && b.Steps[0].Member == "Note"
            && b.Steps[0].Arguments[0].Constant.AsInt32() == 42 && b.Steps[1] is { Kind: MissStepKind.Set, Target: var t } && t == "../Controller");
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
    }

    void Check(string name, bool ok) {
        _checks++;
        if (!ok) _failures.Add(name);
    }
}
