#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;
using Misscore.Editor;

namespace Misstate.Editor;

/// <summary>
/// The Misstate dock: toolbar, the graph with the machine's blackboard beside it, and a status line.
/// <para>
/// All wiring uses native method callables and Godot signals, never <c>+=</c> or C# delegate
/// fields: pressing play reloads the assembly, and anything delegate-based comes back dead.
/// </para>
/// </summary>
[Tool]
public partial class FsmEditorPanel : VBoxContainer {
    /// <summary>The dirty flag changed, so the dock title can show an asterisk.</summary>
    [Signal]
    public delegate void DirtyStateChangedEventHandler(bool dirty);

    /// <summary>A machine was opened, so the debugger can follow it.</summary>
    [Signal]
    public delegate void MachineOpenedEventHandler(string resourcePath);

    /// <summary>The user picked a different running instance to watch.</summary>
    [Signal]
    public delegate void InstanceRequestedEventHandler(long runnerId);

    // Untyped so an assembly reload can restore them — see ReloadSafe.
    GodotObject _machine;
    GodotObject _graph;
    GodotObject _blackboard;

    public Fsm Machine => ReloadSafe.Get<Fsm>(ref _machine);
    public FsmGraphEdit Graph => ReloadSafe.Get<FsmGraphEdit>(ref _graph);
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
    /// Machines edited since they were last saved. Holding them here also keeps an unsaved machine
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
        Name = "Misstate";
        CustomMinimumSize = new Vector2(0, 320);
        SizeFlagsVertical = SizeFlags.ExpandFill;

        AddChild(BuildToolbar());

        var split = new HSplitContainer { Name = "Split", SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);

        _graph = new FsmGraphEdit { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(Graph);
        Graph.Connect(FsmGraphEdit.SignalName.MachineDirtied, new Callable(this, MethodName.MarkDirty));
        Graph.Connect(FsmGraphEdit.SignalName.SelectionMoved, new Callable(this, MethodName.OnSelectionMoved));
        Graph.Connect(FsmGraphEdit.SignalName.EditRejected, new Callable(this, MethodName.OnRejected));
        Graph.Connect(FsmGraphEdit.SignalName.MachineRequested, new Callable(this, MethodName.OnMachineRequested));

        _blackboard = new BlackboardPanel {
            UndoPrefix = "Misstate",
            NotOpenHint = "Open this machine in Misstate to link entries",
        };
        split.AddChild(Blackboard);
        Blackboard.Connect(BlackboardPanel.SignalName.BlackboardEdited, new Callable(this, MethodName.OnBlackboardEdited));
        Blackboard.Connect(BlackboardPanel.SignalName.EditRejected, new Callable(this, MethodName.OnRejected));
        Blackboard.Connect(BlackboardPanel.SignalName.SourceRequested, new Callable(this, MethodName.OnMachineRequested));

        _status = new Label();
        _status.AddThemeFontSizeOverride("font_size", 11);
        AddChild(_status);

        // What a state runs and what a transition checks are edited in the Inspector, nested
        // resources included, and none of that tells the machine. Every such edit is an undo step,
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
            TooltipText = "Which running instance of this machine to show live",
            Visible = false,
            Flat = true,
        };
        _instances.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnInstanceSelected));
        bar.AddChild(_instances);

        bar.AddChild(Tool("Add state", "Add a state to the machine", MethodName.AddStatePressed));

        _saveButton = Tool("Save", "Save the machine resource (Ctrl+S)", MethodName.Save);
        bar.AddChild(_saveButton);

        _revertButton = Tool("Revert", "Discard the unsaved changes and go back to the saved machine", MethodName.RevertPressed);
        _revertButton.Disabled = true;
        bar.AddChild(_revertButton);
        _revertDialog = new ConfirmationDialog { Title = "Revert state machine", OkButtonText = "Discard changes" };
        _revertDialog.Connect(AcceptDialog.SignalName.Confirmed, new Callable(this, MethodName.RevertMachine));
        bar.AddChild(_revertDialog);

        _blackboardToggle = new Button {
            Text = "Blackboard",
            TooltipText = "Show or hide the machine's blackboard",
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

    void AddStatePressed() {
        if (Machine == null) return;
        // Beside the last state, so a new one never lands on top of another.
        var count = Machine.States.Count;
        Graph.AddState((Graph.ScrollOffset + new Vector2(60 + 40 * (count % 6), 60 + 40 * (count % 6))) / Graph.Zoom);
    }

    void OnRejected(string message) => SetStatus(message, warning: true);

    void OnBlackboardToggled(bool visible) => Blackboard.Visible = visible;

    void OnBlackboardEdited() {
        Graph.RefreshBoxes();
        MarkDirty();
    }

    // ---- opening -----------------------------------------------------------------------------

    public void OpenMachine(Fsm machine) {
        if (machine == null || ReferenceEquals(machine, Machine)) return;

        // An action or condition that is no tool script is only a placeholder in the editor.
        if (ToolScripts.Explain(machine.ResourcePath) is { } problem) {
            GD.PushWarning($"misstate: {problem}");
            SetStatus(problem, warning: true);
            return;
        }

        // No saving on the way out: the machine left behind keeps its unsaved edits in memory.
        // What was shown live belongs to the previous machine; the debugger resends for this one.
        ClearLive();
        _machine = machine;
        Graph.LoadMachine(machine);
        Blackboard.ShowSource(machine);

        RememberHistoryOf(machine);
        _title.Text = string.IsNullOrEmpty(machine.ResourcePath) ? "(unsaved machine)" : machine.ResourcePath;
        ShowDirtyState();
        SetStatus(Describe());
        EmitSignal(SignalName.MachineOpened, machine.ResourcePath);
    }

    /// <summary>Undo or redo went back to an edit of a machine that is not open.</summary>
    void OnMachineRequested(Resource machine) => OpenMachine(machine as Fsm);

    // ---- live debugging ----------------------------------------------------------------------

    /// <summary>Shows where the watched runner's machine is. 0xFF in the statuses means the action was not ticked.</summary>
    public void ShowLive(string stateId, byte[] statuses) => Graph?.ShowLive(stateId, statuses);

    public void ClearLive() => Graph?.ClearLive();

    void OnInstanceSelected(long index) => EmitSignal(SignalName.InstanceRequested, _instances.GetItemMetadata((int) index).AsInt64());

    /// <summary>Refreshes the instance picker from the runners the debugger knows about.</summary>
    public void OnRunnersChanged(IReadOnlyList<FsmRunnerInfo> runners, long selected) {
        if (_instances == null) return;

        _instances.Clear();
        var matching = 0;
        foreach (var runner in runners) {
            if (Machine != null && runner.MachinePath != Machine.ResourcePath) continue;

            _instances.AddItem(runner.ActorName);
            _instances.SetItemMetadata(_instances.ItemCount - 1, runner.Id);
            if (runner.Id == selected) _instances.Selected = _instances.ItemCount - 1;
            matching++;
        }
        _instances.Visible = matching > 0;

        // Nothing open but something is running: pick the running machine up instead of showing an
        // instance list over an empty canvas.
        if (Machine == null && runners.Count > 0) {
            _pendingAutoOpen = runners[0].MachinePath;
            CallDeferred(MethodName.OpenPendingMachine);
            return;
        }

        if (matching == 0 && runners.Count > 0) SetStatus("The running game is not using this machine.", warning: true);
        else if (matching > 1) SetStatus($"{matching} running instances — showing the selected one.");
    }

    public void OpenPendingMachine() {
        var path = _pendingAutoOpen;
        _pendingAutoOpen = null;
        if (string.IsNullOrEmpty(path) || Machine != null || !ResourceLoader.Exists(path)) return;

        if (ResourceLoader.Load<Fsm>(path) is { } machine) OpenMachine(machine);
    }

    void ShowEmptyState() {
        _title.Text = "—";
        SetStatus("Open an Fsm resource from the FileSystem dock to edit it.");
    }

    // ---- edits made elsewhere ----------------------------------------------------------------

    void OnEditorHistoryChanged() {
        if (Machine == null || !Engine.IsEditorHint()) return;

        var edited = EditorInterface.Singleton.GetInspector()?.GetEditedObject();
        if (!Owns(edited)) return;

        // The graph is brought up to date either way: re-reading costs little, and typing into a
        // field merges every keystroke into one undo step, so the history does not move again
        // after the first letter.
        var moved = HistoryMoved(edited);
        var changed = ContentChanged(edited);
        Graph.SyncWithMachine();
        if (moved || changed) MarkDirty();
    }

    /// <summary>
    /// Whether what the Inspector shows holds other values than when it was last looked at. This is
    /// what catches the keystrokes merged into an undo step that already existed — including one
    /// that was there before the machine was last saved.
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
    /// the project, does it too — and none of those may mark the machine unsaved.
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

    /// <summary>Whether an object is the open machine or a part of it.</summary>
    public bool Owns(GodotObject edited) {
        var machine = Machine;
        if (machine == null || edited == null) return false;
        return edited switch {
            Fsm fsm => ReferenceEquals(fsm, machine),
            FsmState state => machine.States.Contains(state),
            FsmTransition transition => machine.States.Any(s => s != null && s.Transitions.Contains(transition)),
            MissNode node => machine.AllNodes().Contains(node),
            _ => false,
        };
    }

    /// <summary>The Inspector changed the machine or a part of it: bring the graph up to date.</summary>
    public void OnInspectorEdited() {
        if (Machine == null) return;
        Graph.SyncWithMachine();
        MarkDirty();
    }

    // ---- selection ---------------------------------------------------------------------------

    /// <summary>
    /// Selects the box of a state — and the row of a transition — typically because the editor
    /// started inspecting that resource. The early-out when it is already what is shown breaks the
    /// cycle of the Inspector answering a selection with another one.
    /// </summary>
    public void Highlight(Resource resource) {
        if (Machine == null || _syncingSelection || ReferenceEquals(Graph.SelectedResource(), resource)) return;
        if (!Locate(resource, out var state, out var kind, out var id) || Graph.BoxFor(state.Id) is not { } box) return;
        RememberHistoryOf(resource);

        if (box.Selected && box.PickedKind == kind && box.PickedId == id) return;

        _syncingSelection = true;
        Graph.Pick(state.Id, kind, id);
        _syncingSelection = false;
    }

    /// <summary>Where a part of the machine shows in the graph: its state, and the row within it.</summary>
    bool Locate(Resource resource, out FsmState state, out string kind, out string id) {
        (state, kind, id) = (null, "", "");
        foreach (var candidate in Machine.States) {
            if (candidate == null) continue;
            state = candidate;

            if (ReferenceEquals(candidate, resource)) return true;
            if (resource is MissNode node && candidate.Actions.Contains(node)) {
                (kind, id) = (FsmRow.Action, node.Id);
                return true;
            }
            foreach (var transition in candidate.Transitions) {
                if (transition == null) continue;
                if (ReferenceEquals(transition, resource)) {
                    (kind, id) = (FsmRow.Transition, transition.Id);
                    return true;
                }
                if (resource is MissNode condition && transition.Conditions.Contains(condition)) {
                    (kind, id) = (FsmRow.Condition, condition.Id);
                    return true;
                }
            }
        }
        state = null;
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
        if (Machine == null || _syncingSelection) return;

        var target = Graph.SelectedResource() ?? Machine;
        RememberHistoryOf(target);
        _syncingSelection = true;
        if (EditInInspector != null) EditInInspector(target);
        else if (Engine.IsEditorHint()) EditorInterface.Singleton.EditResource(target);
        _syncingSelection = false;
    }

    // ---- saving ------------------------------------------------------------------------------

    void MarkDirty() {
        if (Machine != null && !_unsaved.Contains(Machine)) _unsaved.Add(Machine);
        ShowDirtyState();
        SetStatus(Describe());
    }

    void ShowDirtyState() {
        var dirty = HasUnsavedChanges(Machine);
        _saveButton.Disabled = !dirty;
        // A machine without a file has nothing to go back to.
        _revertButton.Disabled = !dirty || string.IsNullOrEmpty(Machine?.ResourcePath);
        EmitSignal(SignalName.DirtyStateChanged, dirty);
    }

    public bool HasUnsavedChanges(Fsm machine) => machine != null && _unsaved.Contains(machine);

    /// <summary>Paths of every machine with unsaved edits, open or not.</summary>
    public string[] UnsavedPaths() => [.. Unsaved().Select(m => string.IsNullOrEmpty(m.ResourcePath) ? "(unsaved machine)" : m.ResourcePath)];

    List<Fsm> Unsaved() => [.. _unsaved.Select(m => m.AsGodotObject()).OfType<Fsm>()];

    /// <summary>Saves every machine with unsaved edits — before the project runs, or when the editor saves.</summary>
    public void SaveUnsaved() {
        foreach (var machine in Unsaved()) SaveMachine(machine);
    }

    void Save() => SaveMachine(Machine);

    void SaveMachine(Fsm machine) {
        if (machine == null) return;

        if (string.IsNullOrEmpty(machine.ResourcePath)) {
            SetStatus("This machine has no file yet — save it from the FileSystem dock first.", warning: true);
            return;
        }

        var error = ResourceSaver.Save(machine, machine.ResourcePath);
        if (error != Error.Ok) {
            SetStatus($"Saving {machine.ResourcePath} failed: {error}", warning: true);
            return;
        }

        _unsaved.Remove(machine);
        ShowDirtyState();
        SetStatus($"Saved {machine.ResourcePath}");
    }

    void RevertPressed() {
        if (!HasUnsavedChanges(Machine)) return;
        _revertDialog.DialogText = $"Discard all unsaved changes to {Machine.ResourcePath}?";
        _revertDialog.PopupCentered();
    }

    /// <summary>
    /// Puts the open machine back to its file. The machine resource itself is kept — scenes and
    /// runners point at it — and takes over the states and entries as the file holds them. Not an
    /// undo step of its own; undo still reaches the discarded edits.
    /// </summary>
    public void RevertMachine() {
        var machine = Machine;
        if (machine == null || string.IsNullOrEmpty(machine.ResourcePath)) return;

        // Ignore: a fresh copy of the file, not the cached (edited) machine.
        var saved = ResourceLoader.Load<Fsm>(machine.ResourcePath, "", ResourceLoader.CacheMode.Ignore);
        if (saved == null) {
            SetStatus($"Reverting failed: {machine.ResourcePath} could not be read.", warning: true);
            return;
        }

        machine.States = saved.States;
        machine.InitialStateId = saved.InitialStateId;
        machine.Description = saved.Description;
        machine.Blackboard = saved.Blackboard;

        _unsaved.Remove(machine);
        Graph.LoadMachine(machine);
        Blackboard.ShowSource(machine);
        machine.NotifyPropertyListChanged();
        machine.EmitChanged();

        ShowDirtyState();
        SetStatus($"Discarded the unsaved changes to {machine.ResourcePath}");
    }

    void Validate() {
        if (Machine == null) return;

        var problems = Machine.Validate();
        if (problems.Length == 0) {
            SetStatus("No problems found.");
            return;
        }

        foreach (var problem in problems) GD.PushWarning($"misstate: {problem}");
        SetStatus($"{problems.Length} problem(s) — see the Output panel.", warning: true);
    }

    // ---- status ------------------------------------------------------------------------------

    string Describe() {
        if (Machine == null) return "No machine open.";
        var states = Machine.States.Count(s => s != null);
        var transitions = Machine.States.Where(s => s != null).Sum(s => s.Transitions.Count(t => t != null));
        return $"{states} state(s), {transitions} transition(s)";
    }

    void SetStatus(string message, bool warning = false) {
        if (_status == null) return;
        _status.Text = message;
        _status.AddThemeColorOverride("font_color", warning ? new Color("#e0b400") : new Color(1, 1, 1, 0.55f));
    }

    public override void _ShortcutInput(InputEvent @event) {
        if (Machine == null || !IsVisibleInTree()) return;
        if (@event is not InputEventKey { Pressed: true, Keycode: Key.S, CtrlPressed: true }) return;

        Save();
        AcceptEvent();
    }
}
#endif