#if TOOLS
using Godot;
using Misscore.Editor;

namespace Misspeak.Editor;

/// <summary>
/// Entry point of the addon. Everything editor-side is built in <see cref="_EnterTree"/> and torn
/// down in <see cref="_ExitTree"/>: the dock with the dialogue graph, the Inspector plugin, and the
/// translation parser that puts the texts of dialogues into a translation template.
/// <para>
/// A reload of the assembly does not run either of those again: Godot keeps the objects and rebuilds
/// only their C# side. That is why every connection here is a native method callable, and why
/// references to the addon's own classes are stored untyped (see <see cref="ReloadSafe"/>).
/// </para>
/// </summary>
[Tool]
public partial class MisspeakEditorPlugin : EditorPlugin {
    const string DockTitle = "Misspeak";
    const string MetaSection = "misspeak";
    const string MetaKey = "open_dialogue";

    EditorDock _dock;
    GodotObject _panel;
    GodotObject _inspector;
    GodotObject _parser;
    GodotObject _debugger;

    DialogueEditorPanel Panel => ReloadSafe.Get<DialogueEditorPanel>(ref _panel);
    DialogueInspectorPlugin Inspector => ReloadSafe.Get<DialogueInspectorPlugin>(ref _inspector);
    MisspeakDebuggerPlugin Debugger => ReloadSafe.Get<MisspeakDebuggerPlugin>(ref _debugger);

    /// <summary>The open dialogue's blackboard, which node parameters in the Inspector link against.</summary>
    internal BlackboardPanel Blackboard => Panel?.Blackboard;

    /// <summary>The open dialogue, if that line is one of its lines; else null.</summary>
    internal Dialogue OpenDialogueOf(GodotObject line) => line is DialogueLine && Panel?.Owns(line) == true ? Panel.Dialogue : null;

    public override void _EnterTree() {
        var parser = new DialogueTranslationParser();
        _parser = parser;
        AddTranslationParserPlugin(parser);

        var panel = new DialogueEditorPanel();
        _panel = panel;

        _dock = new EditorDock {
            Name = "MisspeakDock",
            Title = DockTitle,
            LayoutKey = DockTitle,
            DefaultSlot = EditorDock.DockSlot.Bottom,
            AvailableLayouts = EditorDock.DockLayout.All,
            Closable = true,
        };
        _dock.AddChild(panel);
        AddDock(_dock);

        var inspector = new DialogueInspectorPlugin();
        inspector.Attach(this);
        _inspector = inspector;
        AddInspectorPlugin(inspector);

        var debugger = new MisspeakDebuggerPlugin();
        debugger.Attach(panel);
        _debugger = debugger;
        AddDebuggerPlugin(debugger);

        panel.Connect(DialogueEditorPanel.SignalName.InstanceRequested, new Callable(this, MethodName.OnInstanceRequested));
        panel.Connect(DialogueEditorPanel.SignalName.DirtyStateChanged, new Callable(this, MethodName.OnDirtyChanged));
        panel.Connect(DialogueEditorPanel.SignalName.DialogueOpened, new Callable(this, MethodName.OnDialogueOpened));

        RestoreLastDialogue();
    }

    void OnDirtyChanged(bool dirty) {
        if (_dock != null) _dock.Title = dirty ? $"{DockTitle} *" : DockTitle;
    }

    void OnInstanceRequested(long runnerId) => Debugger?.WatchInstance(runnerId);

    void OnDialogueOpened(string path) {
        Debugger?.WatchSource();
        if (!string.IsNullOrEmpty(path)) Metadata()?.SetProjectMetadata(MetaSection, MetaKey, path);
    }

    /// <summary>Reopens whatever dialogue was open when the editor was last closed.</summary>
    void RestoreLastDialogue() {
        var path = Metadata()?.GetProjectMetadata(MetaSection, MetaKey, "").AsString();
        if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path)) return;

        if (ResourceLoader.Load(path) is Dialogue dialogue) Panel?.OpenDialogue(dialogue);
    }

    static EditorSettings Metadata() => EditorInterface.Singleton?.GetEditorSettings();

    public override void _ExitTree() {
        if (ReloadSafe.Get<DialogueTranslationParser>(ref _parser) is { } parser) RemoveTranslationParserPlugin(parser);
        _parser = null;

        if (Debugger != null) {
            RemoveDebuggerPlugin(Debugger);
            _debugger = null;
        }

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
    /// The parts of the open dialogue too — sections, lines, options, actions, conditions: selecting
    /// one in the graph hands it to the Inspector, and if this returned false for it the editor
    /// would hide the dock again. Only of the open dialogue, though: nodes are shared with the other
    /// Missfits addons, and one that sits in a behavior tree is none of this dock's business.
    /// </summary>
    public override bool _Handles(GodotObject @object) => @object is Dialogue || Panel?.Owns(@object) == true;

    public override void _Edit(GodotObject @object) {
        switch (@object) {
            case Dialogue dialogue:
                OpenDialogue(dialogue);
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

    /// <summary>Called before the project runs and before scenes are saved, so a run never uses stale dialogues.</summary>
    public override void _ApplyChanges() => Panel?.SaveUnsaved();

    /// <summary>Ctrl+S in the editor saves the dialogues along with the scene.</summary>
    public override void _SaveExternalData() => Panel?.SaveUnsaved();

    /// <summary>Lets the editor ask before quitting while dialogues are unsaved — whether open or not.</summary>
    public override string _GetUnsavedStatus(string forScene) {
        if (!string.IsNullOrEmpty(forScene) || Panel == null) return "";
        var paths = Panel.UnsavedPaths();
        return paths.Length == 0 ? "" : $"Save changes to the following dialogue(s) before closing?\n{string.Join("\n", paths)}";
    }

    public void OpenDialogue(Dialogue dialogue) {
        if (Panel == null || dialogue == null) return;
        Panel.OpenDialogue(dialogue);
        _dock?.MakeVisible();
    }
}
#endif
