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

    /// <summary>A machine was opened.</summary>
    [Signal]
    public delegate void MachineOpenedEventHandler(string resourcePath);

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

    /// <summary>
    /// Machines edited since they were last saved. Holding them here also keeps an unsaved machine
    /// in memory while another one is open. Untyped so an assembly reload restores it.
    /// </summary>
    Godot.Collections.Array _unsaved = [];

    bool _syncingSelection;
    bool _syncQueued;

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

        // No saving on the way out: the machine left behind keeps its unsaved edits in memory.
        _machine = machine;
        Graph.LoadMachine(machine);
        Blackboard.ShowSource(machine);

        _title.Text = string.IsNullOrEmpty(machine.ResourcePath) ? "(unsaved machine)" : machine.ResourcePath;
        ShowDirtyState();
        SetStatus(Describe());
        EmitSignal(SignalName.MachineOpened, machine.ResourcePath);
    }

    /// <summary>Undo or redo went back to an edit of a machine that is not open.</summary>
    void OnMachineRequested(Resource machine) => OpenMachine(machine as Fsm);

    void ShowEmptyState() {
        _title.Text = "—";
        SetStatus("Open an Fsm resource from the FileSystem dock to edit it.");
    }

    // ---- edits made elsewhere ----------------------------------------------------------------

    void OnEditorHistoryChanged() {
        if (Machine == null || !Engine.IsEditorHint()) return;
        if (Owns(EditorInterface.Singleton.GetInspector()?.GetEditedObject())) OnInspectorEdited();
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