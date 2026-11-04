#if TOOLS
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Godot.Collections;

namespace Misswire.Tests;

/// <summary>
/// Phase 0 of misswire: finds out how placeholders, the editor's undo history and the inspector
/// behave, inside the real editor. It reports what it sees instead of asserting, then quits.
/// <code>
/// godot --headless -e --path &lt;project&gt; -- --misswire-spike
/// </code>
/// </summary>
[Tool]
public partial class MisswireEditorSpike : Node {
    const string ScenePath = "res://addons/misswire/tests/spike/spike_scene.tscn";

    int _versions;
    int _histories;
    int _outfitChanged;
    int _modelSignal;

    public override async void _Ready() {
        var editor = EditorInterface.Singleton;
        await Frames(10);
        while (editor.GetResourceFilesystem().IsScanning()) await Frames(1);

        editor.OpenSceneFromPath(ScenePath);
        await Frames(10);
        var root = editor.GetEditedSceneRoot();
        Report("edited scene", root?.SceneFilePath);
        if (root == null) { GetTree().Quit(1); return; }

        var model = root.GetNode("Model");
        var controller = root.GetNode("Controller");
        var modelScript = model.GetScript().As<Script>();
        var controllerScript = controller.GetScript().As<Script>();

        Section("placeholders");
        Report("model: C# type", model.GetType().FullName);
        Report("model: native class", model.GetClass());
        Report("model: script is tool", modelScript.IsTool());
        Report("controller: C# type", controller.GetType().FullName);
        Report("controller: script is tool", controllerScript.IsTool());
        Report("controller is SpikeController", controller is SpikeController);

        Section("reading a placeholder");
        var outfitValue = model.Get("Outfit");
        Report("model.Get(Outfit)", Describe(outfitValue));
        Report("model.Get(Outfit) is the controller's outfit",
            outfitValue.AsGodotObject() is { } o1 && controller.Get("Outfit").AsGodotObject() is { } o2 && o1.GetInstanceId() == o2.GetInstanceId());
        Report("model.Get(Health), not set in the scene", Describe(model.Get("Health")));
        Report("model.Get(Partner), a node export", Describe(model.Get("Partner")));
        Report("model.Get(Extra), a resource without tool", Describe(model.Get("Extra")));
        Report("model.Get(Extra).Get(Value)", model.Get("Extra").AsGodotObject() is { } extra ? Describe(extra.Get("Value")) : "-");
        model.Set("Health", 7);
        Report("model.Get(Health) after Set(7)", Describe(model.Get("Health")));

        Section("script lists of a placeholder");
        Report("signals", Names(modelScript.GetScriptSignalList()));
        Report("methods", Names(modelScript.GetScriptMethodList()));
        Report("properties", Names(modelScript.GetScriptPropertyList()));
        Report("model.HasMethod(Heal)", model.HasMethod("Heal"));
        Report("model.HasSignal(OutfitChanged)", model.HasSignal("OutfitChanged"));

        Section("calling and connecting a placeholder");
        Report("model.Call(Heal, 3)", Describe(model.Call("Heal", 3)));
        Report("model.Get(Health) after the call", Describe(model.Get("Health")));
        Report("model.Connect(OutfitChanged)", model.Connect("OutfitChanged", new Callable(this, MethodName.OnModelSignal)));
        Report("model.EmitSignal(OutfitChanged)", model.EmitSignal("OutfitChanged"));
        Report("the handler ran", _modelSignal);
        model.Disconnect("OutfitChanged", new Callable(this, MethodName.OnModelSignal));

        Section("calling a tool target");
        controller.Call("ApplyOutfit");
        Report("controller.AppliedCount after ApplyOutfit", Describe(controller.Get("AppliedCount")));

        Section("undo history and changed");
        var history = editor.GetEditorUndoRedo();
        history.Connect(EditorUndoRedoManager.SignalName.VersionChanged, new Callable(this, MethodName.OnVersionChanged));
        history.Connect(EditorUndoRedoManager.SignalName.HistoryChanged, new Callable(this, MethodName.OnHistoryChanged));
        var outfit = (Resource) outfitValue.AsGodotObject();
        outfit.Connect(Resource.SignalName.Changed, new Callable(this, MethodName.OnOutfitChanged));

        await Measure("action: node property", () => Commit(history, model, "Health", 9));
        await Measure("action: export of a sub-resource", () => Commit(history, outfit, "Body", "b2"));
        await Measure("action: _Set property of a sub-resource", () => Commit(history, outfit, "Hair", "h2"));
        Report("outfit Hair now", Describe(outfit.Get("Hair")));
        await Measure("undo of the last action", () => history.GetHistoryUndoRedo(history.GetObjectHistoryId(outfit)).Undo());
        Report("outfit Hair after undo", Describe(outfit.Get("Hair")));
        await Measure("ctrl+z as a key press", () => PressShortcut(Key.Z, false));
        Report("outfit Body after ctrl+z", Describe(outfit.Get("Body")));
        await Measure("ctrl+shift+z as a key press", () => PressShortcut(Key.Z, true));
        Report("outfit Body after redo", Describe(outfit.Get("Body")));

        Section("the inspector");
        editor.EditNode(model);
        await Frames(3);
        await Measure("inspector: node property", () => InspectorSet("Health", 11));
        Report("model.Get(Health)", Describe(model.Get("Health")));
        await Measure("inspector: 5 edits of one property in one frame", () => {
            for (var i = 0; i < 5; i++) InspectorSet("Health", 20 + i);
        });
        editor.EditResource(outfit);
        await Frames(3);
        await Measure("inspector: export of a sub-resource", () => InspectorSet("Body", "b3"));
        await Measure("inspector: _Set property of a sub-resource", () => InspectorSet("Hair", "h3"));
        Report("outfit Hair now", Describe(outfit.Get("Hair")));

        history.Disconnect(EditorUndoRedoManager.SignalName.VersionChanged, new Callable(this, MethodName.OnVersionChanged));
        history.Disconnect(EditorUndoRedoManager.SignalName.HistoryChanged, new Callable(this, MethodName.OnHistoryChanged));
        outfit.Disconnect(Resource.SignalName.Changed, new Callable(this, MethodName.OnOutfitChanged));
        GD.Print("misswire spike: done");
        GetTree().Quit(0);
    }

    async Task Measure(string label, System.Action act) {
        int versions = _versions, histories = _histories, changed = _outfitChanged;
        string Counts() => $"{_versions - versions} version_changed, {_histories - histories} history_changed, {_outfitChanged - changed} changed";
        act();
        var sameFrame = Counts();
        await Frames(3);
        Report(label, $"same frame: {sameFrame} | after 3 frames: {Counts()}");
    }

    static void Commit(EditorUndoRedoManager history, GodotObject target, string property, Variant value) {
        history.CreateAction($"spike {property}", UndoRedo.MergeMode.Disable, target);
        history.AddDoProperty(target, property, value);
        history.AddUndoProperty(target, property, target.Get(property));
        history.CommitAction();
    }

    /// <summary>Edits a property the way a click in the inspector does: through its EditorProperty.</summary>
    static void InspectorSet(string property, Variant value) {
        var editorProperty = Find(EditorInterface.Singleton.GetInspector(), property);
        if (editorProperty == null) { Report($"inspector has no row for {property}", ""); return; }
        editorProperty.EmitChanged(property, value);
    }

    static EditorProperty Find(Node node, string property) {
        if (node is EditorProperty p && p.GetEditedProperty() == property) return p;
        foreach (var child in node.GetChildren()) {
            if (Find(child, property) is { } found) return found;
        }
        return null;
    }

    static void PressShortcut(Key key, bool shift) {
        foreach (var pressed in new[] { true, false }) {
            Input.ParseInputEvent(new InputEventKey {
                Keycode = key, PhysicalKeycode = key, CtrlPressed = true, ShiftPressed = shift, Pressed = pressed,
            });
        }
    }

    void OnVersionChanged() => _versions++;
    void OnHistoryChanged() => _histories++;
    void OnOutfitChanged() => _outfitChanged++;
    void OnModelSignal() => _modelSignal++;

    async Task Frames(int count) {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    static string Describe(Variant value) => value.VariantType switch {
        Variant.Type.Object => value.AsGodotObject() is { } obj
            ? $"Object {obj.GetType().FullName} ({obj.GetClass()}{(obj is Node n ? $" '{n.Name}'" : "")})"
            : "Object null",
        _ => $"{value.VariantType} {value}",
    };

    static string Names(Array<Dictionary> list) => string.Join(", ", list.Select(d => d["name"].AsString()));

    static void Section(string name) => GD.Print($"SPIKE ---- {name}");
    static void Report(string label, object value) => GD.Print($"SPIKE {label}: {value}");
}
#endif
