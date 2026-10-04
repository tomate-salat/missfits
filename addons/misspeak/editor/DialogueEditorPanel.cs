#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;
using Misscore.Editor;

namespace Misspeak.Editor;

/// <summary>
/// The Misspeak dock: toolbar, the graph with the dialogue's blackboard beside it, and a status line.
/// <para>
/// All wiring uses native method callables and Godot signals, never <c>+=</c> or C# delegate
/// fields: pressing play reloads the assembly, and anything delegate-based comes back dead.
/// </para>
/// </summary>
[Tool]
public partial class DialogueEditorPanel : VBoxContainer, IRunnerDebugView {
    /// <summary>The dirty flag changed, so the dock title can show an asterisk.</summary>
    [Signal]
    public delegate void DirtyStateChangedEventHandler(bool dirty);

    /// <summary>A dialogue was opened, so the debugger can follow it.</summary>
    [Signal]
    public delegate void DialogueOpenedEventHandler(string resourcePath);

    /// <summary>The user picked a different running instance to watch.</summary>
    [Signal]
    public delegate void InstanceRequestedEventHandler(long runnerId);

    // Untyped so an assembly reload can restore them — see ReloadSafe.
    GodotObject _dialogue;
    GodotObject _graph;
    GodotObject _blackboard;

    public Dialogue Dialogue => ReloadSafe.Get<Dialogue>(ref _dialogue);
    public DialogueGraphEdit Graph => ReloadSafe.Get<DialogueGraphEdit>(ref _graph);
    public BlackboardPanel Blackboard => ReloadSafe.Get<BlackboardPanel>(ref _blackboard);

    /// <summary>
    /// Test seam: when set, receives what would otherwise go to the editor's Inspector. Deliberately
    /// not relied upon in the editor — it is a plain delegate and would be null again after a reload.
    /// </summary>
    public Action<Resource> EditInInspector;

    Label _title;
    Label _status;
    Button _saveButton;
    Button _revertButton;
    ConfirmationDialog _revertDialog;
    Button _blackboardToggle;
    OptionButton _instances;
    string _pendingAutoOpen;

    /// <summary>
    /// Dialogues edited since they were last saved. Holding them here also keeps an unsaved dialogue
    /// in memory while another one is open. Untyped so an assembly reload restores it.
    /// </summary>
    Godot.Collections.Array _unsaved = [];

    bool _syncingSelection;
    bool _syncQueued;

    /// <summary>Where each undo history stood when last looked at, by history id. A Godot dictionary so a reload keeps it.</summary>
    Godot.Collections.Dictionary _historyVersions = [];

    /// <summary>What the object in the Inspector held when last looked at, and which object that was.</summary>
    ulong _stampedObject;
    string _stamp = "";

    public override void _Ready() {
        Name = "Misspeak";
        CustomMinimumSize = new Vector2(0, 320);
        SizeFlagsVertical = SizeFlags.ExpandFill;

        AddChild(BuildToolbar());

        var split = new HSplitContainer { Name = "Split", SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);

        _graph = new DialogueGraphEdit { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(Graph);
        Graph.Connect(DialogueGraphEdit.SignalName.DialogueDirtied, new Callable(this, MethodName.MarkDirty));
        Graph.Connect(DialogueGraphEdit.SignalName.SelectionMoved, new Callable(this, MethodName.OnSelectionMoved));
        Graph.Connect(DialogueGraphEdit.SignalName.EditRejected, new Callable(this, MethodName.OnRejected));
        Graph.Connect(DialogueGraphEdit.SignalName.DialogueRequested, new Callable(this, MethodName.OnDialogueRequested));

        _blackboard = new BlackboardPanel {
            UndoPrefix = "Misspeak",
            NotOpenHint = "Open this dialogue in Misspeak to link entries",
        };
        split.AddChild(Blackboard);
        Blackboard.Connect(BlackboardPanel.SignalName.BlackboardEdited, new Callable(this, MethodName.OnBlackboardEdited));
        Blackboard.Connect(BlackboardPanel.SignalName.EditRejected, new Callable(this, MethodName.OnRejected));
        Blackboard.Connect(BlackboardPanel.SignalName.SourceRequested, new Callable(this, MethodName.OnDialogueRequested));

        _status = new Label();
        _status.AddThemeFontSizeOverride("font_size", 11);
        AddChild(_status);

        // What a line says, runs and checks, and what an option checks, are edited in the Inspector, nested
        // resources included, and none of that tells the dialogue. Every such edit is an undo step,
        // though, so the editor's history is what to listen to. Godot removes the connection when
        // this panel is freed.
        if (Engine.IsEditorHint()) {
            EditorInterface.Singleton?.GetEditorUndoRedo()?.Connect(EditorUndoRedoManager.SignalName.HistoryChanged,
                new Callable(this, MethodName.OnEditorHistoryChanged));
        }

        ShowEmptyState();
    }

    Control BuildToolbar() {
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 4);

        _title = new Label { Text = "—", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bar.AddChild(_title);

        _instances = new OptionButton {
            TooltipText = "Which running instance of this dialogue to show live",
            Visible = false,
            Flat = true,
        };
        _instances.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnInstanceSelected));
        bar.AddChild(_instances);

        bar.AddChild(Tool("Add section", "Add a section to the dialogue", MethodName.AddSectionPressed));

        _saveButton = Tool("Save", "Save the dialogue resource (Ctrl+S)", MethodName.Save);
        bar.AddChild(_saveButton);

        _revertButton = Tool("Revert", "Discard the unsaved changes and go back to the saved dialogue", MethodName.RevertPressed);
        _revertButton.Disabled = true;
        bar.AddChild(_revertButton);
        _revertDialog = new ConfirmationDialog { Title = "Revert dialogue", OkButtonText = "Discard changes" };
        _revertDialog.Connect(AcceptDialog.SignalName.Confirmed, new Callable(this, MethodName.RevertDialogue));
        bar.AddChild(_revertDialog);

        _blackboardToggle = new Button {
            Text = "Blackboard",
            TooltipText = "Show or hide the dialogue's blackboard",
            Flat = true,
            ToggleMode = true,
            ButtonPressed = true,
        };
        _blackboardToggle.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.OnBlackboardToggled));
        bar.AddChild(_blackboardToggle);

        bar.AddChild(Tool("Validate", "List problems", MethodName.Validate));
        return bar;
    }

    Button Tool(string text, string tooltip, StringName method) {
        var button = new Button { Text = text, TooltipText = tooltip, Flat = true };
        button.Connect(BaseButton.SignalName.Pressed, new Callable(this, method));
        return button;
    }

    void AddSectionPressed() {
        if (Dialogue == null) return;
        // Beside the last section, so a new one never lands on top of another.
        var count = Dialogue.Sections.Count;
        Graph.AddSection((Graph.ScrollOffset + new Vector2(60 + 40 * (count % 6), 60 + 40 * (count % 6))) / Graph.Zoom);
    }

    void OnRejected(string message) => SetStatus(message, warning: true);

    void OnBlackboardToggled(bool visible) => Blackboard.Visible = visible;

    void OnBlackboardEdited() {
        Graph.RefreshBoxes();
        MarkDirty();
    }

    // ---- opening -----------------------------------------------------------------------------

    public void OpenDialogue(Dialogue dialogue) {
        if (dialogue == null || ReferenceEquals(dialogue, Dialogue)) return;

        // An action or condition that is no tool script is only a placeholder in the editor.
        if (ToolScripts.Explain(dialogue.ResourcePath) is { } problem) {
            GD.PushWarning($"misspeak: {problem}");
            SetStatus(problem, warning: true);
            return;
        }

        // No saving on the way out: the dialogue left behind keeps its unsaved edits in memory.
        // What was shown live belongs to the previous dialogue; the debugger resends for this one.
        ClearLive();
        _dialogue = dialogue;
        Graph.LoadDialogue(dialogue);
        Blackboard.ShowSource(dialogue);

        RememberHistoryOf(dialogue);
        _title.Text = string.IsNullOrEmpty(dialogue.ResourcePath) ? "(unsaved dialogue)" : dialogue.ResourcePath;
        ShowDirtyState();
        SetStatus(Describe());
        EmitSignal(SignalName.DialogueOpened, dialogue.ResourcePath);
    }

    /// <summary>Undo or redo went back to an edit of a dialogue that is not open.</summary>
    void OnDialogueRequested(Resource dialogue) => OpenDialogue(dialogue as Dialogue);

    // ---- live debugging ----------------------------------------------------------------------

    /// <summary>Path of the open dialogue, for the debugger: empty for one without a file, null for none.</summary>
    public string WatchedPath => Dialogue?.ResourcePath;

    /// <summary>What the game reported: the section's id, the line's, what the dialogue waits for, the tick, and the option that led there.</summary>
    public void ShowLive(Godot.Collections.Array state) {
        if (state.Count < 2) return;
        Graph?.ShowLive(state[0].AsString(), state[1].AsString(), state.Count > 4 ? state[4].AsString() : "");
    }

    public void ClearLive() => Graph?.ClearLive();

    void OnInstanceSelected(long index) => EmitSignal(SignalName.InstanceRequested, _instances.GetItemMetadata((int) index).AsInt64());

    /// <summary>Refreshes the instance picker from the runners the debugger knows about.</summary>
    public void OnRunnersChanged(IReadOnlyList<RunnerDebugInfo> runners, long selected) {
        if (_instances == null) return;

        _instances.Clear();
        var matching = 0;
        foreach (var runner in runners) {
            if (Dialogue != null && runner.SourcePath != Dialogue.ResourcePath) continue;

            _instances.AddItem(runner.ActorName);
            _instances.SetItemMetadata(_instances.ItemCount - 1, runner.Id);
            if (runner.Id == selected) _instances.Selected = _instances.ItemCount - 1;
            matching++;
        }
        // One runner is the rule for dialogues; a picker with a single entry would only be in the way.
        _instances.Visible = matching > 1;

        // Nothing open but something is running: pick the running dialogue up instead of showing
        // an empty canvas.
        if (Dialogue == null && runners.Count > 0) {
            _pendingAutoOpen = runners[0].SourcePath;
            CallDeferred(MethodName.OpenPendingDialogue);
            return;
        }

        if (matching == 0 && runners.Count > 0) SetStatus("The running game is not using this dialogue.", warning: true);
        else if (matching > 1) SetStatus($"{matching} running instances — showing the selected one.");
    }

    public void OpenPendingDialogue() {
        var path = _pendingAutoOpen;
        _pendingAutoOpen = null;
        if (string.IsNullOrEmpty(path) || Dialogue != null || !ResourceLoader.Exists(path)) return;

        if (ResourceLoader.Load(path) is Dialogue dialogue) OpenDialogue(dialogue);
    }

    void ShowEmptyState() {
        _title.Text = "—";
        SetStatus("Open a Dialogue resource from the FileSystem dock to edit it.");
    }

    // ---- edits made elsewhere ----------------------------------------------------------------

    void OnEditorHistoryChanged() {
        if (Dialogue == null || !Engine.IsEditorHint()) return;

        var edited = EditorInterface.Singleton.GetInspector()?.GetEditedObject();
        if (!Owns(edited)) return;

        // The graph is brought up to date either way: re-reading costs little, and typing into a
        // field merges every keystroke into one undo step, so the history does not move again
        // after the first letter.
        var moved = HistoryMoved(edited);
        var changed = ContentChanged(edited);
        Graph.SyncWithDialogue();
        if (moved || changed) MarkDirty();
    }

    /// <summary>
    /// Whether what the Inspector shows holds other values than when it was last looked at. This is
    /// what catches the keystrokes merged into an undo step that already existed — including one
    /// that was there before the dialogue was last saved.
    /// </summary>
    bool ContentChanged(GodotObject edited) {
        var stamp = StampOf(edited);
        var same = _stampedObject == edited.GetInstanceId() && _stamp == stamp;
        var known = _stampedObject == edited.GetInstanceId();
        _stampedObject = edited.GetInstanceId();
        _stamp = stamp;
        return known && !same;
    }

    /// <summary>The stored values of an object in one string; other objects in them count by identity.</summary>
    static string StampOf(GodotObject target) {
        var parts = new System.Text.StringBuilder();
        foreach (var property in target.GetPropertyList()) {
            if (((PropertyUsageFlags) property["usage"].AsInt64() & PropertyUsageFlags.Storage) == 0) continue;
            var name = property["name"].AsStringName();
            parts.Append(name).Append('=').Append(StampOf(target.Get(name))).Append(';');
        }
        return parts.ToString();
    }

    static string StampOf(Variant value) => value.VariantType switch {
        Variant.Type.Object => value.AsGodotObject() is { } obj ? $"#{obj.GetInstanceId()}" : "null",
        Variant.Type.Array => $"[{string.Join(",", value.AsGodotArray().Select(StampOf))}]",
        _ => GD.VarToStr(value),
    };

    /// <summary>
    /// Whether the undo history an object belongs to has gained or lost a step since it was last
    /// looked at. The editor announces a change of history for more than edits — saving, or running
    /// the project, does it too — and none of those may mark the dialogue unsaved.
    /// </summary>
    bool HistoryMoved(GodotObject edited) {
        var manager = EditorInterface.Singleton.GetEditorUndoRedo();
        var history = manager.GetObjectHistoryId(edited);
        var version = (long) manager.GetHistoryUndoRedo(history).GetVersion();

        var known = _historyVersions.TryGetValue(history, out var seen);
        _historyVersions[history] = version;
        return !known || seen.AsInt64() != version;
    }

    /// <summary>Takes note of where an object's undo history stands, so only later steps count as edits.</summary>
    void RememberHistoryOf(GodotObject edited) {
        if (edited == null || !Engine.IsEditorHint()) return;
        var manager = EditorInterface.Singleton.GetEditorUndoRedo();
        var history = manager.GetObjectHistoryId(edited);
        _historyVersions[history] = (long) manager.GetHistoryUndoRedo(history).GetVersion();
        _stampedObject = edited.GetInstanceId();
        _stamp = StampOf(edited);
    }

    /// <summary>Whether an object is the open dialogue or a part of it.</summary>
    public bool Owns(GodotObject edited) {
        var dialogue = Dialogue;
        if (dialogue == null || edited == null) return false;
        return edited switch {
            Dialogue whole => ReferenceEquals(whole, dialogue),
            DialogueSection section => dialogue.Sections.Contains(section),
            DialogueLine line => dialogue.Sections.Any(s => s != null && s.Lines.Contains(line)),
            DialogueOption option => dialogue.Sections.Any(s => s != null && s.Options.Contains(option)),
            MissNode node => dialogue.AllNodes().Contains(node),
            _ => false,
        };
    }

    /// <summary>The Inspector changed the dialogue or a part of it: bring the graph up to date.</summary>
    public void OnInspectorEdited() {
        if (Dialogue == null) return;
        Graph.SyncWithDialogue();
        MarkDirty();
    }

    // ---- selection ---------------------------------------------------------------------------

    /// <summary>
    /// Selects the box of a section — and the row of a line or an option — typically because the editor
    /// started inspecting that resource. The early-out when it is already what is shown breaks the
    /// cycle of the Inspector answering a selection with another one.
    /// </summary>
    public void Highlight(Resource resource) {
        if (Dialogue == null || _syncingSelection || ReferenceEquals(Graph.SelectedResource(), resource)) return;
        if (!Locate(resource, out var section, out var kind, out var id) || Graph.BoxFor(section.Id) is not { } box) return;
        RememberHistoryOf(resource);

        if (box.Selected && box.PickedKind == kind && box.PickedId == id) return;

        _syncingSelection = true;
        Graph.Pick(section.Id, kind, id);
        _syncingSelection = false;
    }

    /// <summary>Where a part of the dialogue shows in the graph: its section, and the row within it.</summary>
    bool Locate(Resource resource, out DialogueSection section, out string kind, out string id) {
        (section, kind, id) = (null, "", "");
        foreach (var candidate in Dialogue.Sections) {
            if (candidate == null) continue;
            section = candidate;
            if (ReferenceEquals(candidate, resource)) return true;

            foreach (var line in candidate.Lines) {
                if (line == null) continue;
                if (ReferenceEquals(line, resource)) (kind, id) = (SpeakRow.Line, line.Id);
                else if (resource is MissNode action && line.Actions.Contains(action)) (kind, id) = (SpeakRow.Action, action.Id);
                else if (resource is MissNode gate && line.Conditions.Contains(gate)) (kind, id) = (SpeakRow.LineCondition, gate.Id);
                if (id != "") return true;
            }
            foreach (var option in candidate.Options) {
                if (option == null) continue;
                if (ReferenceEquals(option, resource)) (kind, id) = (SpeakRow.Option, option.Id);
                else if (resource is MissNode condition && option.Conditions.Contains(condition)) (kind, id) = (SpeakRow.OptionCondition, condition.Id);
                if (id != "") return true;
            }
        }
        section = null;
        return false;
    }

    /// <summary>
    /// Selection signals arrive one per box, in no guaranteed order. So rather than reacting per
    /// signal, read the actual selection once the dust has settled.
    /// </summary>
    void OnSelectionMoved() {
        if (_syncingSelection || _syncQueued) return;
        _syncQueued = true;
        CallDeferred(MethodName.SyncInspector);
    }

    public void SyncInspector() {
        _syncQueued = false;
        if (Dialogue == null || _syncingSelection) return;

        var target = Graph.SelectedResource() ?? Dialogue;
        RememberHistoryOf(target);
        _syncingSelection = true;
        if (EditInInspector != null) EditInInspector(target);
        else if (Engine.IsEditorHint()) EditorInterface.Singleton.EditResource(target);
        _syncingSelection = false;
    }

    // ---- saving ------------------------------------------------------------------------------

    void MarkDirty() {
        if (Dialogue != null && !_unsaved.Contains(Dialogue)) _unsaved.Add(Dialogue);
        ShowDirtyState();
        SetStatus(Describe());
    }

    void ShowDirtyState() {
        var dirty = HasUnsavedChanges(Dialogue);
        _saveButton.Disabled = !dirty;
        // A dialogue without a file has nothing to go back to.
        _revertButton.Disabled = !dirty || string.IsNullOrEmpty(Dialogue?.ResourcePath);
        EmitSignal(SignalName.DirtyStateChanged, dirty);
    }

    public bool HasUnsavedChanges(Dialogue dialogue) => dialogue != null && _unsaved.Contains(dialogue);

    /// <summary>Paths of every dialogue with unsaved edits, open or not.</summary>
    public string[] UnsavedPaths() => [.. Unsaved().Select(m => string.IsNullOrEmpty(m.ResourcePath) ? "(unsaved dialogue)" : m.ResourcePath)];

    List<Dialogue> Unsaved() => [.. _unsaved.Select(m => m.AsGodotObject()).OfType<Dialogue>()];

    /// <summary>Saves every dialogue with unsaved edits — before the project runs, or when the editor saves.</summary>
    public void SaveUnsaved() {
        foreach (var dialogue in Unsaved()) SaveDialogue(dialogue);
    }

    void Save() => SaveDialogue(Dialogue);

    void SaveDialogue(Dialogue dialogue) {
        if (dialogue == null) return;

        if (string.IsNullOrEmpty(dialogue.ResourcePath)) {
            SetStatus("This dialogue has no file yet — save it from the FileSystem dock first.", warning: true);
            return;
        }

        var error = ResourceSaver.Save(dialogue, dialogue.ResourcePath);
        if (error != Error.Ok) {
            SetStatus($"Saving {dialogue.ResourcePath} failed: {error}", warning: true);
            return;
        }

        _unsaved.Remove(dialogue);
        ShowDirtyState();
        SetStatus($"Saved {dialogue.ResourcePath}");
    }

    void RevertPressed() {
        if (!HasUnsavedChanges(Dialogue)) return;
        _revertDialog.DialogText = $"Discard all unsaved changes to {Dialogue.ResourcePath}?";
        _revertDialog.PopupCentered();
    }

    /// <summary>
    /// Puts the open dialogue back to its file. The dialogue resource itself is kept — scenes and
    /// runners point at it — and takes over the states and entries as the file holds them. Not an
    /// undo step of its own; undo still reaches the discarded edits.
    /// </summary>
    public void RevertDialogue() {
        var dialogue = Dialogue;
        if (dialogue == null || string.IsNullOrEmpty(dialogue.ResourcePath)) return;

        // Ignore: a fresh copy of the file, not the cached (edited) dialogue.
        var saved = ResourceLoader.Load<Dialogue>(dialogue.ResourcePath, "", ResourceLoader.CacheMode.Ignore);
        if (saved == null) {
            SetStatus($"Reverting failed: {dialogue.ResourcePath} could not be read.", warning: true);
            return;
        }

        dialogue.Sections = saved.Sections;
        dialogue.Reroutes = saved.Reroutes;
        dialogue.StartSectionId = saved.StartSectionId;
        dialogue.SpeakerAsTranslationContext = saved.SpeakerAsTranslationContext;
        dialogue.Description = saved.Description;
        dialogue.Blackboard = saved.Blackboard;

        _unsaved.Remove(dialogue);
        Graph.LoadDialogue(dialogue);
        Blackboard.ShowSource(dialogue);
        dialogue.NotifyPropertyListChanged();
        dialogue.EmitChanged();

        ShowDirtyState();
        SetStatus($"Discarded the unsaved changes to {dialogue.ResourcePath}");
    }

    void Validate() {
        if (Dialogue == null) return;

        var problems = Dialogue.Validate();
        if (problems.Length == 0) {
            SetStatus("No problems found.");
            return;
        }

        foreach (var problem in problems) GD.PushWarning($"misspeak: {problem}");
        SetStatus($"{problems.Length} problem(s) — see the Output panel.", warning: true);
    }

    // ---- status ------------------------------------------------------------------------------

    string Describe() {
        if (Dialogue == null) return "No dialogue open.";
        var sections = Dialogue.Sections.Where(s => s != null).ToList();
        return $"{sections.Count} section(s), {sections.Sum(s => s.Lines.Count(l => l != null))} line(s)";
    }

    void SetStatus(string message, bool warning = false) {
        if (_status == null) return;
        _status.Text = message;
        _status.AddThemeColorOverride("font_color", warning ? new Color("#e0b400") : new Color(1, 1, 1, 0.55f));
    }

    public override void _ShortcutInput(InputEvent @event) {
        if (Dialogue == null || !IsVisibleInTree()) return;
        if (@event is not InputEventKey { Pressed: true, Keycode: Key.S, CtrlPressed: true }) return;

        Save();
        AcceptEvent();
    }
}
#endif