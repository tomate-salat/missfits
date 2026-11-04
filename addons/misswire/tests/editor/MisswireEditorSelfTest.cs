#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Misswire.Editor;

namespace Misswire.Tests;

/// <summary>
/// Self test of the editor sync, inside the real editor: only there are the game nodes placeholders.
/// Edits go through the editor's undo history, as the inspector does. Exits with code 0 on
/// success, 1 on failure.
/// <code>
/// godot --headless -e --path &lt;project&gt; -- --misswire-editor-test
/// </code>
/// </summary>
[Tool]
public partial class MisswireEditorSelfTest : Node {
    const string ScenePath = "res://addons/misswire/tests/editor/editor_test_scene.tscn";
    const string OtherScenePath = "res://addons/misswire/tests/spike/spike_scene.tscn";

    /// <summary>Set by the plugin that started the test.</summary>
    [Export]
    public GodotObject Plugin { get; set; }

    readonly List<string> _failures = [];
    readonly List<string> _warnings = [];
    int _checks;

    MissEditorSync _sync;
    EditorUndoRedoManager _history;
    Node _root;
    Node _mediator;
    Node _model;
    EditorProbeController _controller;
    EditorProbeToolModel _toolModel;
    Node _noToolTarget;

    public override async void _Ready() {
        var editor = EditorInterface.Singleton;
        await Frames(10);
        while (editor.GetResourceFilesystem().IsScanning()) await Frames();

        _sync = ((MisswireEditorPlugin) Plugin).Sync;
        _sync.Warned += message => _warnings.Add(message);
        _history = editor.GetEditorUndoRedo();

        editor.OpenSceneFromPath(ScenePath);
        await Frames(5);
        _root = editor.GetEditedSceneRoot();
        _mediator = _root.GetNode("Mediator");
        _model = _root.GetNode("Model");
        _controller = _root.GetNode<EditorProbeController>("Controller");
        _toolModel = _root.GetNode<EditorProbeToolModel>("ToolModel");
        _noToolTarget = _root.GetNode("NoToolTarget");

        Check("the game nodes are placeholders", _model.GetType() == typeof(Node) && _mediator.GetType() == typeof(Node));

        await TheFirstValuesGoOutWhenTheGraphArrives();
        await AnEditOfAPlaceholderIsComparedAndPassedOn();
        await TheSameValueAgainRunsNothing();
        await ManyEditsInOneFrameMakeOneSync();
        await AResourceSignalIsListenedToAndFollowsTheProperty();
        await UndoComesBackToo();
        await AToolSourceIsListenedToNotCompared();
        await AResourceWithoutToolIsCompared();
        TheEditorsConnectionsAreNotSaved();
        await AnotherSceneLetsGoOfEverything();

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"misswire editor self test: {_checks - _failures.Count}/{_checks} checks passed");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    // ---- graph ---------------------------------------------------------------------------------

    static MissBindingGraph BuildGraph() {
        var graph = new MissBindingGraph();
        void Add(MissBinding binding, params MissBindingStep[] steps) {
            binding.EditorActive = binding.Id != "inactive";
            foreach (var step in steps) binding.Steps.Add(step);
            graph.Bindings.Add(binding);
        }

        Add(Property("outfit", "Model", "Outfit", "OutfitChanged"), Set("Controller", "Outfit"));
        Add(new MissBinding {
            Id = "apply", Kind = MissSourceKind.ResourceSignal, Source = "../Model", Property = "Outfit", Signal = "changed",
            ChangeSignal = "OutfitChanged",
        }, Call("Controller", "ApplyOutfit"));
        Add(Property("health", "Model", "Health", push: false), Call("Controller", "Note"));
        Add(Property("inactive", "Model", "Health", push: false), Mark("inactive"));
        Add(Property("tool", "ToolModel", "Value", "ValueChanged"), Mark("tool"));
        Add(Property("toolCompared", "ToolModel", "Other"), Mark("toolCompared"));
        Add(Property("poke", "Model", "Health", push: false), Call("NoToolTarget", "Poke"));
        Add(Property("seen", "Model", "Health", push: false), Set("NoToolTarget", "Seen"));
        Add(new MissBinding {
            Id = "extraEvery", Kind = MissSourceKind.ResourceSignal, Source = "../Model", Property = "Extra", Signal = "changed",
            PushInitial = false, EditorTrigger = MissEditorTrigger.EveryEdit,
        }, Mark("extraEvery"));
        Add(new MissBinding {
            Id = "extraValue", Kind = MissSourceKind.ResourceSignal, Source = "../Model", Property = "Extra", Signal = "changed",
            PushInitial = false,
        }, Mark("extraValue"));
        Add(Property("chain", "NoToolTarget", "Seen", push: false), Mark("chain"));
        return graph;
    }

    static MissBinding Property(string id, string node, string property, string changeSignal = "", bool push = true) => new() {
        Id = id, Kind = MissSourceKind.Property, Source = $"../{node}", Property = property, ChangeSignal = changeSignal, PushInitial = push,
    };

    static MissBindingStep Call(string node, string method) => new() { Kind = MissStepKind.Call, Target = $"../{node}", Member = method };
    static MissBindingStep Set(string node, string property) => new() { Kind = MissStepKind.Set, Target = $"../{node}", Member = property };

    static MissBindingStep Mark(string tag) {
        var step = Call("Controller", "Mark");
        step.Arguments.Add(new MissStepArgument { Source = MissArgumentSource.Constant, Constant = tag });
        return step;
    }

    // ---- tests ---------------------------------------------------------------------------------

    async Task TheFirstValuesGoOutWhenTheGraphArrives() {
        Commit(_mediator, "Graph", BuildGraph());
        await Frames(3);
        var outfit = _model.Get("Outfit").AsGodotObject();
        Check("a property's first value goes out", _controller.Sets == 1 && _controller.Outfit == outfit);
        Check("a resource signal runs once at the start", _controller.Applies == 1);
        Check("a binding that is not active in the editor stays quiet", _controller.MarkCount("inactive") == 0);
        Check("a tool source sends its first value too, once", _controller.MarkCount("tool") == 1 && _controller.MarkCount("toolCompared") == 1);
        Check("only sources that run in the editor are listened to", _sync.RelayCount == 2);
        _controller.Reset();
        _warnings.Clear();
    }

    async Task AnEditOfAPlaceholderIsComparedAndPassedOn() {
        Commit(_model, "Health", 50);
        await Frames(3);
        Check("an edit of a placeholder's property is passed on", _controller.Notes == 1 && _controller.LastNote.AsInt32() == 50);
        Check("an inactive binding ignores edits", _controller.MarkCount("inactive") == 0);
        Check("setting a placeholder's property works", _noToolTarget.Get("Seen").AsInt32() == 50);
        Check("what a binding set feeds the next one, in the same sync", _controller.MarkCount("chain") == 1);
        Check("calling a placeholder is refused with a warning", _warnings.Any(w => w.Contains("'NoToolTarget' has no [Tool]")));
        Check("a resource without tool runs on every edit, if asked to", _controller.MarkCount("extraEvery") == 1);
        Check("and otherwise only when it changed", _controller.MarkCount("extraValue") == 0);
    }

    async Task TheSameValueAgainRunsNothing() {
        _controller.Reset();
        Commit(_model, "Health", 50);
        await Frames(3);
        Check("the same value again runs nothing", _controller.Notes == 0 && _controller.MarkCount("chain") == 0);
        Check("an edit is still an edit for EveryEdit", _controller.MarkCount("extraEvery") == 1);
    }

    async Task ManyEditsInOneFrameMakeOneSync() {
        _controller.Reset();
        var syncs = _sync.Syncs;
        for (var i = 0; i < 5; i++) Commit(_model, "Health", 60 + i);
        await Frames(3);
        Check("five edits in one frame make one sync", _sync.Syncs - syncs == 1);
        Check("and pass on the last value once", _controller.Notes == 1 && _controller.LastNote.AsInt32() == 64);
    }

    async Task AResourceSignalIsListenedToAndFollowsTheProperty() {
        _controller.Reset();
        var red = (EditorProbeOutfit) _model.Get("Outfit").AsGodotObject();
        var blue = new EditorProbeOutfit { Hair = "blue" };
        Commit(_model, "Outfit", blue);
        await Frames(3);
        Check("a new resource in a placeholder is passed on", _controller.Sets == 1 && _controller.Outfit == blue);
        Check("swapping the resource does not count as a change inside it", _controller.Applies == 0);

        Commit(blue, "Hair", "green");
        await Frames(3);
        Check("a change inside the new resource runs its steps once", _controller.Applies == 1);
        Commit(red, "Hair", "pink");
        await Frames(3);
        Check("the old resource is let go", _controller.Applies == 1);
        Check("still two connections, the new resource's instead of the old one's", _sync.RelayCount == 2);
    }

    async Task UndoComesBackToo() {
        var blue = _controller.Outfit;
        Commit(_model, "Outfit", new EditorProbeOutfit { Hair = "yellow" });
        await Frames(3);
        PressCtrlZ();
        await Frames(4);
        Check("undo in the editor is passed on as well", _model.Get("Outfit").AsGodotObject() == blue && _controller.Outfit == blue);
    }

    async Task AToolSourceIsListenedToNotCompared() {
        _controller.Reset();
        Commit(_toolModel, "Value", 5);
        Commit(_toolModel, "Other", 5);
        await Frames(3);
        Check("a tool source's signal runs its steps once, not again by comparing", _controller.MarkCount("tool") == 1);
        Check("a tool source without a change signal is compared", _controller.MarkCount("toolCompared") == 1);
    }

    async Task AResourceWithoutToolIsCompared() {
        _controller.Reset();
        var extra = _model.Get("Extra").AsGodotObject();
        Commit(extra, "Value", 2);
        await Frames(3);
        Check("a change inside a resource without tool is found by comparing", _controller.MarkCount("extraValue") == 1);
    }

    void TheEditorsConnectionsAreNotSaved() {
        var packed = new PackedScene();
        Check("the scene packs", packed.Pack(_root) == Error.Ok);
        Check("no connection of the editor ends up in the scene", packed.GetState().GetConnectionCount() == 0);
    }

    async Task AnotherSceneLetsGoOfEverything() {
        var editor = EditorInterface.Singleton;
        editor.OpenSceneFromPath(OtherScenePath);
        await Frames(5);
        Check("another scene in front lets go of this one's connections", _sync.RelayCount == 0);
        editor.OpenSceneFromPath(ScenePath);
        await Frames(5);
        Check("coming back connects again", _sync.RelayCount == 2);
    }

    // ---- helpers -------------------------------------------------------------------------------

    void Commit(GodotObject target, string property, Variant value) {
        _history.CreateAction($"test {property}", UndoRedo.MergeMode.Disable, target);
        _history.AddDoProperty(target, property, value);
        _history.AddUndoProperty(target, property, target.Get(property));
        _history.CommitAction();
    }

    static void PressCtrlZ() {
        foreach (var pressed in new[] { true, false }) {
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Z, PhysicalKeycode = Key.Z, CtrlPressed = true, Pressed = pressed });
        }
    }

    async Task Frames(int count = 1) {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    void Check(string name, bool ok) {
        _checks++;
        if (!ok) _failures.Add(name);
    }
}
#endif
