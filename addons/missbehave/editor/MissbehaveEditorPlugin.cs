#if TOOLS
using Godot;
using Misscore.Editor;

namespace Missbehave.Editor;

/// <summary>
/// Entry point of the addon. Everything editor-side is built in <see cref="_EnterTree"/> and torn
/// down in <see cref="_ExitTree"/>.
/// <para>
/// A reload of the assembly (pressing play triggers one) does not run either of those again: Godot
/// keeps the objects and rebuilds only their C# side, restoring Variant-compatible fields. That is
/// why every connection here is a native method callable and every cross-object notification is a
/// Godot signal — both survive a reload, whereas <c>+=</c> handlers and delegate fields do not —
/// and why references to the addon's own classes are stored untyped (see <see cref="ReloadSafe"/>).
/// </para>
/// </summary>
[Tool]
public partial class MissbehaveEditorPlugin : EditorPlugin {
    const string DockTitle = "Missbehave";
    const string MetaSection = "missbehave";
    const string MetaKey = "open_tree";

    EditorDock _dock;
    GodotObject _panel;
    GodotObject _inspector;
    GodotObject _debugger;

    BehaviorTreeEditorPanel Panel => ReloadSafe.Get<BehaviorTreeEditorPanel>(ref _panel);
    BehaviorTreeInspectorPlugin Inspector => ReloadSafe.Get<BehaviorTreeInspectorPlugin>(ref _inspector);
    MissbehaveDebuggerPlugin Debugger => ReloadSafe.Get<MissbehaveDebuggerPlugin>(ref _debugger);

    /// <summary>The open tree's blackboard, which node parameters in the Inspector link against.</summary>
    internal BlackboardPanel Blackboard => Panel?.Blackboard;

    public override void _EnterTree() {
        NodeTypeRegistry.Refresh();

        var panel = new BehaviorTreeEditorPanel();
        _panel = panel;

        // A real editor dock rather than a bottom-panel control: it starts at the bottom, but the
        // editor's own dock menu can float it into a separate window or move it to a side slot,
        // and the layout key makes that choice persist with the editor layout.
        _dock = new EditorDock {
            Name = "MissbehaveDock",
            Title = DockTitle,
            LayoutKey = DockTitle,
            DefaultSlot = EditorDock.DockSlot.Bottom,
            AvailableLayouts = EditorDock.DockLayout.All,
            Closable = true,
            DockIcon = ResourceLoader.Load<Texture2D>("res://addons/missbehave/icons/tree.svg"),
        };
        _dock.AddChild(panel);
        AddDock(_dock);

        var inspector = new BehaviorTreeInspectorPlugin();
        inspector.Attach(this);
        _inspector = inspector;
        AddInspectorPlugin(inspector);

        var debugger = new MissbehaveDebuggerPlugin();
        debugger.Attach(panel);
        _debugger = debugger;
        AddDebuggerPlugin(debugger);

        panel.Connect(BehaviorTreeEditorPanel.SignalName.DirtyStateChanged, new Callable(this, MethodName.OnDirtyChanged));
        panel.Connect(BehaviorTreeEditorPanel.SignalName.TreeOpened, new Callable(this, MethodName.OnTreeOpened));
        panel.Connect(BehaviorTreeEditorPanel.SignalName.InstanceRequested, new Callable(this, MethodName.OnInstanceRequested));

        RestoreLastTree();    }

    void OnDirtyChanged(bool dirty) {
        if (_dock != null) _dock.Title = dirty ? $"{DockTitle} *" : DockTitle;
    }

    void OnInstanceRequested(long runnerId) => Debugger?.WatchInstance(runnerId);

    void OnTreeOpened(string path) {
        Debugger?.WatchTree();
        RememberLastTree(path);
    }

    /// <summary>Reopens whatever tree was open when the editor was last closed.</summary>
    void RestoreLastTree() {
        var path = Metadata()?.GetProjectMetadata(MetaSection, MetaKey, "").AsString();
        if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path)) return;

        var tree = ResourceLoader.Load<BehaviorTree>(path);
        if (tree != null) Panel?.OpenTree(tree);
    }

    void RememberLastTree(string path) {
        if (string.IsNullOrEmpty(path)) return;
        Metadata()?.SetProjectMetadata(MetaSection, MetaKey, path);
    }

    static EditorSettings Metadata() => EditorInterface.Singleton?.GetEditorSettings();

    public override void _ExitTree() {
        if (Debugger != null) {
            RemoveDebuggerPlugin(Debugger);
            _debugger = null;
        }

        if (Inspector != null) {
            RemoveInspectorPlugin(Inspector);
            _inspector = null;
        }

        if (_dock != null) {
            // Nothing is saved here: when the editor quits, _GetUnsavedStatus has already asked, and
            // saving anyway would overrule a "Don't save".
            RemoveDock(_dock);
            _dock.QueueFree();
            _dock = null;
            _panel = null;
        }
    }

    /// <summary>
    /// Also handles individual nodes: selecting one in the graph hands it to the inspector, and if
    /// this returned false for it the editor would immediately hide our dock again.
    /// </summary>
    public override bool _Handles(GodotObject @object) => @object is BehaviorTree or ABehaviorNode;

    public override void _Edit(GodotObject @object) {
        switch (@object) {
            case BehaviorTree tree:
                OpenTree(tree);
                break;
            case ABehaviorNode node:
                Panel?.HighlightNode(node);
                break;
        }
    }

    public override void _MakeVisible(bool visible) {
        // Deliberately one-way: the dock stays where the user put it, and they close it when they
        // want to. Hiding on _MakeVisible(false) would make it flicker away on every inspector churn.
        if (visible) _dock?.MakeVisible();
    }

    /// <summary>Called before the project runs and before scenes are saved, so a run never uses stale trees.</summary>
    public override void _ApplyChanges() => Panel?.SaveUnsavedTrees();

    /// <summary>Ctrl+S in the editor saves the behavior trees along with the scene.</summary>
    public override void _SaveExternalData() => Panel?.SaveUnsavedTrees();

    /// <summary>Lets the editor ask before quitting while trees are unsaved — whether open or not.</summary>
    public override string _GetUnsavedStatus(string forScene) {
        if (!string.IsNullOrEmpty(forScene) || Panel == null) return "";
        var paths = Panel.UnsavedTreePaths();
        return paths.Length == 0 ? "" : $"Save changes to the following behavior tree(s) before closing?\n{string.Join("\n", paths)}";
    }

    public void OpenTree(BehaviorTree tree) {
        if (Panel == null || tree == null) return;
        Panel.OpenTree(tree);
        _dock?.MakeVisible();
    }
}
#endif
