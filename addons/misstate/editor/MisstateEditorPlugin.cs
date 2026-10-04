#if TOOLS
using Godot;
using Misscore.Editor;

namespace Misstate.Editor;

/// <summary>
/// Entry point of the addon. Everything editor-side is built in <see cref="_EnterTree"/> and torn
/// down in <see cref="_ExitTree"/>.
/// <para>
/// A reload of the assembly does not run either of those again: Godot keeps the objects and rebuilds
/// only their C# side. That is why every connection here is a native method callable, and why
/// references to the addon's own classes are stored untyped (see <see cref="ReloadSafe"/>).
/// </para>
/// </summary>
[Tool]
public partial class MisstateEditorPlugin : EditorPlugin {
    const string DockTitle = "Misstate";
    const string MetaSection = "misstate";
    const string MetaKey = "open_machine";

    EditorDock _dock;
    GodotObject _panel;
    GodotObject _inspector;

    FsmEditorPanel Panel => ReloadSafe.Get<FsmEditorPanel>(ref _panel);
    FsmInspectorPlugin Inspector => ReloadSafe.Get<FsmInspectorPlugin>(ref _inspector);

    /// <summary>The open machine's blackboard, which node parameters in the Inspector link against.</summary>
    internal BlackboardPanel Blackboard => Panel?.Blackboard;

    public override void _EnterTree() {
        var panel = new FsmEditorPanel();
        _panel = panel;

        _dock = new EditorDock {
            Name = "MisstateDock",
            Title = DockTitle,
            LayoutKey = DockTitle,
            DefaultSlot = EditorDock.DockSlot.Bottom,
            AvailableLayouts = EditorDock.DockLayout.All,
            Closable = true,
        };
        _dock.AddChild(panel);
        AddDock(_dock);

        var inspector = new FsmInspectorPlugin();
        inspector.Attach(this);
        _inspector = inspector;
        AddInspectorPlugin(inspector);

        panel.Connect(FsmEditorPanel.SignalName.DirtyStateChanged, new Callable(this, MethodName.OnDirtyChanged));
        panel.Connect(FsmEditorPanel.SignalName.MachineOpened, new Callable(this, MethodName.OnMachineOpened));

        RestoreLastMachine();
    }

    void OnDirtyChanged(bool dirty) {
        if (_dock != null) _dock.Title = dirty ? $"{DockTitle} *" : DockTitle;
    }

    void OnMachineOpened(string path) {
        if (!string.IsNullOrEmpty(path)) Metadata()?.SetProjectMetadata(MetaSection, MetaKey, path);
    }

    /// <summary>Reopens whatever machine was open when the editor was last closed.</summary>
    void RestoreLastMachine() {
        var path = Metadata()?.GetProjectMetadata(MetaSection, MetaKey, "").AsString();
        if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path)) return;

        var machine = ResourceLoader.Load<Fsm>(path);
        if (machine != null) Panel?.OpenMachine(machine);
    }

    static EditorSettings Metadata() => EditorInterface.Singleton?.GetEditorSettings();

    public override void _ExitTree() {
        if (Inspector != null) {
            RemoveInspectorPlugin(Inspector);
            _inspector = null;
        }

        if (_dock != null) {
            // Nothing is saved here: when the editor quits, _GetUnsavedStatus has already asked.
            RemoveDock(_dock);
            _dock.QueueFree();
            _dock = null;
            _panel = null;
        }
    }

    /// <summary>
    /// The parts of the open machine too — states, actions, transitions, conditions: selecting one
    /// in the graph hands it to the Inspector, and if this returned false for it the editor would
    /// hide the dock again. Only of the open machine, though: nodes are shared with the other
    /// Missfits addons, and one that sits in a behavior tree is none of this dock's business.
    /// </summary>
    public override bool _Handles(GodotObject @object) => @object is Fsm || Panel?.Owns(@object) == true;

    public override void _Edit(GodotObject @object) {
        switch (@object) {
            case Fsm machine:
                OpenMachine(machine);
                break;
            case Resource part:
                Panel?.Highlight(part);
                break;
        }
    }

    public override void _MakeVisible(bool visible) {
        // Deliberately one-way: the dock stays where the user put it.
        if (visible) _dock?.MakeVisible();
    }

    /// <summary>Called before the project runs and before scenes are saved, so a run never uses stale machines.</summary>
    public override void _ApplyChanges() => Panel?.SaveUnsaved();

    /// <summary>Ctrl+S in the editor saves the machines along with the scene.</summary>
    public override void _SaveExternalData() => Panel?.SaveUnsaved();

    /// <summary>Lets the editor ask before quitting while machines are unsaved — whether open or not.</summary>
    public override string _GetUnsavedStatus(string forScene) {
        if (!string.IsNullOrEmpty(forScene) || Panel == null) return "";
        var paths = Panel.UnsavedPaths();
        return paths.Length == 0 ? "" : $"Save changes to the following state machine(s) before closing?\n{string.Join("\n", paths)}";
    }

    public void OpenMachine(Fsm machine) {
        if (Panel == null || machine == null) return;
        Panel.OpenMachine(machine);
        _dock?.MakeVisible();
    }
}
#endif