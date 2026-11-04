#if TOOLS
using System.Linq;
using Godot;
using Misscore.Editor;

namespace Misswire.Editor;

/// <summary>
/// Entry point of the addon: runs the bindings of the edited scene in the editor
/// (<see cref="MissEditorSync"/>), so that a change shows right away.
/// <para>
/// A reload of the assembly does not run <see cref="_EnterTree"/> again: Godot keeps the objects
/// and rebuilds only their C# side. That is why every connection here is a native method callable,
/// why the sync is stored untyped (see <see cref="ReloadSafe"/>), and why a reload starts a sync of
/// its own: a script may have gained or lost <c>[Tool]</c>.
/// </para>
/// <para>
/// Started with <c>-- --misswire-editor-test</c> or <c>-- --misswire-spike</c>, it also loads that
/// test from the tests folder by path, so the addon still compiles when the tests are left out of a
/// release.
/// </para>
/// </summary>
[Tool]
public partial class MisswireEditorPlugin : EditorPlugin, ISerializationListener {
    static readonly (string Arg, string Path)[] Tests = [
        ("--misswire-editor-test", "res://addons/misswire/tests/editor/MisswireEditorSelfTest.cs"),
        ("--misswire-graph-test", "res://addons/misswire/tests/editor/MisswireGraphSelfTest.cs"),
        ("--misswire-spike", "res://addons/misswire/tests/spike/MisswireEditorSpike.cs"),
    ];

    const string DockTitle = "Misswire";

    GodotObject _sync;
    GodotObject _panel;
    EditorDock _dock;

    public MissEditorSync Sync => ReloadSafe.Get<MissEditorSync>(ref _sync);
    public MisswirePanel Panel => ReloadSafe.Get<MisswirePanel>(ref _panel);

    public override void _EnterTree() {
        var sync = new MissEditorSync();
        _sync = sync;
        var panel = new MisswirePanel { Name = "MisswirePanel" };
        _panel = panel;
        _dock = new EditorDock {
            Name = "MisswireDock",
            Title = DockTitle,
            LayoutKey = DockTitle,
            DefaultSlot = EditorDock.DockSlot.Bottom,
            AvailableLayouts = EditorDock.DockLayout.All,
            Closable = true,
        };
        _dock.AddChild(panel);
        AddDock(_dock);

        foreach (var (signal, method, target) in Connections(sync, panel)) target.Connect(signal, new Callable(method.Target, method.Name));
        sync.Request();

        StartTest();
    }

    /// <summary>What the plugin listens to: every edit, undo and change of scene, for the sync and for the graph.</summary>
    (StringName Signal, (GodotObject Target, StringName Name) Method, GodotObject Source)[] Connections(MissEditorSync sync, MisswirePanel panel) {
        var history = GetUndoRedo();
        return [
            (EditorUndoRedoManager.SignalName.HistoryChanged, (sync, MissEditorSync.MethodName.OnEdited), history),
            (EditorUndoRedoManager.SignalName.VersionChanged, (sync, MissEditorSync.MethodName.OnEdited), history),
            (SignalName.SceneChanged, (sync, MissEditorSync.MethodName.OnSceneChanged), this),
            (EditorUndoRedoManager.SignalName.HistoryChanged, (panel, MisswirePanel.MethodName.OnEdited), history),
            (EditorUndoRedoManager.SignalName.VersionChanged, (panel, MisswirePanel.MethodName.OnEdited), history),
            (SignalName.SceneChanged, (panel, MisswirePanel.MethodName.OnSceneChanged), this),
        ];
    }

    public override void _ExitTree() {
        var sync = Sync;
        var panel = Panel;
        if (sync != null && panel != null) {
            foreach (var (signal, method, source) in Connections(sync, panel)) {
                var callable = new Callable(method.Target, method.Name);
                if (source.IsConnected(signal, callable)) source.Disconnect(signal, callable);
            }
        }
        sync?.Shutdown();
        _sync = null;

        if (_dock != null) {
            RemoveDock(_dock);
            _dock.QueueFree();
            _dock = null;
        }
        _panel = null;
    }

    /// <summary>A mediator, and whatever of its graph the Inspector shows, keeps the dock up.</summary>
    public override bool _Handles(GodotObject @object) => MissEditorSync.IsMediator(@object) || Panel?.Owns(@object) == true;

    public override void _Edit(GodotObject @object) {
        if (MissEditorSync.IsMediator(@object)) Panel?.Open((Node) @object);
    }

    public override void _MakeVisible(bool visible) {
        // Deliberately one-way: the dock stays where the user put it.
        if (visible) _dock?.MakeVisible();
    }

    public void OnBeforeSerialize() { }

    public void OnAfterDeserialize() => CallDeferred(MethodName.AfterReload);

    void AfterReload() {
        Sync?.Request();
        Panel?.AfterReload();
    }

    void StartTest() {
        var args = OS.GetCmdlineUserArgs();
        foreach (var (arg, path) in Tests) {
            if (!args.Contains(arg)) continue;
            if (ResourceLoader.Load<Script>(path)?.Call("new").AsGodotObject() is not Node test) {
                GD.PrintErr($"misswire: the test is missing at {path}");
                GetTree().Quit(1);
                return;
            }
            test.Set("Plugin", this);
            EditorInterface.Singleton.GetBaseControl().AddChild(test);
        }
    }
}
#endif
