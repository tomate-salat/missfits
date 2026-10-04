#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;
using Misscore.Editor;

namespace Misstate.Editor;

/// <summary>
/// The authoring surface of a state machine: states as boxes with their actions listed inside,
/// transitions as wires from a row of the state they leave to the state they lead to.
/// <para>
/// Every structural change goes through <see cref="Commit"/>, which records a before/after snapshot
/// with the editor's undo manager. The properties of a state, an action, a transition or a
/// condition are edited in the Inspector, which keeps its own undo steps.
/// </para>
/// <para>
/// Reload safety, as everywhere in the Missfits editors: signals are connected with native method
/// callables, outgoing notifications are real Godot signals, and references to the addon's own
/// classes are stored untyped (see <see cref="ReloadSafe"/>).
/// </para>
/// </summary>
[Tool]
public partial class FsmGraphEdit : GraphEdit {
    /// <summary>The machine resource was changed and needs saving.</summary>
    [Signal]
    public delegate void MachineDirtiedEventHandler();

    /// <summary>What is selected changed. Listeners read the actual selection themselves.</summary>
    [Signal]
    public delegate void SelectionMovedEventHandler();


    /// <summary>An edit was refused, with a short message saying why.</summary>
    [Signal]
    public delegate void EditRejectedEventHandler(string reason);

    /// <summary>
    /// Undo or redo reached an edit of a machine other than the open one. Expected to open that
    /// machine right away, before the signal returns.
    /// </summary>
    [Signal]
    public delegate void MachineRequestedEventHandler(Resource machine);

    // Untyped so an assembly reload can restore them — see ReloadSafe.
    GodotObject _machine;
    GodotObject _picker;

    public Fsm Machine => ReloadSafe.Get<Fsm>(ref _machine);

    /// <summary>The node picker shared with the other Missfits editors, narrowed to actions or to conditions.</summary>
    public CreateNodeDialog Picker => ReloadSafe.Get<CreateNodeDialog>(ref _picker);

    internal const int MenuAddState = 0;
    internal const int MenuSetInitial = 1;
    internal const int MenuDeleteState = 2;
    internal const int MenuAddAction = 3;
    internal const int MenuRowUp = 10;
    internal const int MenuRowDown = 11;
    internal const int MenuRowDelete = 12;
    internal const int MenuAddCondition = 13;

    PopupMenu _menu;
    bool _rebuilding;
    bool _rebuildQueued;
    Vector2 _menuPosition;
    StringName _menuState;
    string _menuKind;
    string _menuRow;


    /// <summary>What the graph was last built from, so an edit made elsewhere is noticed.</summary>
    string _builtFrom = "";

    public override void _Ready() {
        RightDisconnects = false;
        ShowArrangeButton = false;
        MinimapEnabled = false;

        // Items are filled in per right-click.
        _menu = new PopupMenu { Name = "Menu" };
        AddChild(_menu, false, InternalMode.Back);
        _menu.Connect(PopupMenu.SignalName.IdPressed, new Callable(this, MethodName.OnMenuIdPressed));

        // Internal, so it never shows up in the child scans that drive the graph.
        var picker = new CreateNodeDialog { Name = "Picker" };
        _picker = picker;
        AddChild(picker, false, InternalMode.Back);
        picker.Connect(CreateNodeDialog.SignalName.TypeChosen, new Callable(this, MethodName.OnTypeChosen));

        Wire(GraphEdit.SignalName.ConnectionRequest, MethodName.OnConnectionRequest);
        Wire(GraphEdit.SignalName.ConnectionToEmpty, MethodName.OnConnectionToEmpty);
        Wire(GraphEdit.SignalName.PopupRequest, MethodName.OnPopupRequest);
        Wire(GraphEdit.SignalName.DeleteNodesRequest, MethodName.OnDeleteNodesRequest);
        Wire(GraphEdit.SignalName.EndNodeMove, MethodName.OnEndNodeMove);
        Wire(GraphEdit.SignalName.NodeSelected, MethodName.OnNodeSelected);
        Wire(GraphEdit.SignalName.NodeDeselected, MethodName.OnNodeDeselected);
    }

    void Wire(StringName signal, StringName method) => Connect(signal, new Callable(this, method));

    // ---- loading -----------------------------------------------------------------------------

    public void LoadMachine(Fsm machine) {
        _machine = machine;
        machine?.EnsureNodeIds();
        RebuildGraph();
    }

    IEnumerable<FsmStateBox> Boxes() => GetChildren().OfType<FsmStateBox>();

    public FsmStateBox BoxFor(string stateId)
        => string.IsNullOrEmpty(stateId) ? null : Boxes().FirstOrDefault(b => b.Name == stateId);

    void ClearGraph() {
        ClearConnections();

        // RemoveChild before QueueFree: a doomed box is still a child until the end of the frame,
        // and Godot would rename its replacement to "<id>@2", breaking every ConnectNode that follows.
        foreach (var box in Boxes().ToList()) {
            RemoveChild(box);
            box.QueueFree();
        }
    }

    /// <summary>
    /// Rebuilds every box and wire from the machine, keeping what was selected.
    /// <para>
    /// Never call this straight out of a GraphEdit signal handler — freeing the boxes GraphEdit is
    /// working with crashes the editor. Use <see cref="QueueRebuild"/> from anything signal-driven.
    /// </para>
    /// </summary>
    public void RebuildGraph() {
        _rebuildQueued = false;
        _rebuilding = true;

        var selected = Boxes().FirstOrDefault(b => b.Selected);
        var selectedState = selected?.Name.ToString();
        var pickedKind = selected?.PickedKind ?? "";
        var pickedId = selected?.PickedId ?? "";

        ClearGraph();

        var machine = Machine;
        if (machine != null) {
            var spot = 0;
            foreach (var state in machine.States) {
                if (state == null || BoxFor(state.Id) != null) continue;

                // Named before entering the tree so it can never collide with a leftover sibling.
                var box = new FsmStateBox { Name = state.Id };
                AddChild(box);
                box.Bind(state);
                box.Connect(FsmStateBox.SignalName.MenuRequested, new Callable(this, MethodName.OnBoxMenu));
                box.Connect(FsmStateBox.SignalName.RowPicked, new Callable(this, MethodName.OnRowPicked));
                box.Connect(FsmStateBox.SignalName.RowMenuRequested, new Callable(this, MethodName.OnRowMenu));
                box.Connect(FsmStateBox.SignalName.AddActionRequested, new Callable(this, MethodName.OnAddActionRequested));

                // A machine built in code has no positions yet; spread it out rather than piling it up.
                box.PositionOffset = state.GraphPosition != Vector2.Zero || machine.States.Count == 1
                    ? state.GraphPosition
                    : new Vector2(60 + 280 * (spot % 4), 60 + 220 * (spot / 4));
                spot++;
            }

            foreach (var state in machine.States) {
                if (state == null) continue;
                var port = 0;
                foreach (var transition in state.Transitions) {
                    if (transition == null) continue;
                    if (BoxFor(transition.TargetStateId) != null) ConnectNode(state.Id, port, transition.TargetStateId, 0);
                    port++;
                }
            }
        }

        _builtFrom = Shape();
        RefreshBoxes();
        _rebuilding = false;

        if (BoxFor(selectedState) is { } again) {
            again.Selected = true;
            again.ShowPicked(pickedKind, pickedId);
        }
    }

    void QueueRebuild() {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        CallDeferred(MethodName.RebuildGraph);
    }

    /// <summary>Re-reads names, summaries and warnings without rebuilding.</summary>
    public void RefreshBoxes() {
        foreach (var box in Boxes()) box.Refresh(Machine);
    }

    /// <summary>
    /// Brings the graph up to date after something outside it changed the machine — the Inspector,
    /// say. Rebuilds only when states, actions or transitions came, went or moved; otherwise just
    /// re-reads.
    /// </summary>
    public void SyncWithMachine() {
        Machine?.EnsureNodeIds();
        if (Shape() != _builtFrom) QueueRebuild();
        else if (!_rebuildQueued) RefreshBoxes();
    }

    /// <summary>The machine's structure in one string: which states, actions and transitions, leading where.</summary>
    string Shape() {
        var machine = Machine;
        if (machine == null) return "";
        return string.Join("|", machine.States.Where(s => s != null).Select(s =>
            $"{s.Id}:{string.Join(",", s.Actions.Where(a => a != null).Select(a => a.Id))}"
            + $":{string.Join(",", s.Transitions.Where(t => t != null).Select(t =>
                $"{t.Id}>{t.TargetStateId}[{string.Join(" ", t.Conditions.Where(c => c != null).Select(c => c.Id))}]"))}"));
    }

    // ---- selection ---------------------------------------------------------------------------

    void OnNodeSelected(Node node) {
        if (!_rebuilding) EmitSignal(SignalName.SelectionMoved);
    }

    void OnNodeDeselected(Node node) {
        if (!_rebuilding) EmitSignal(SignalName.SelectionMoved);
    }

    void OnRowPicked(StringName boxName, string kind, string id) => Pick(boxName, kind, id);

    /// <summary>Selects a state's box and, within it, a row — or the state itself, with an empty id.</summary>
    public void Pick(StringName boxName, string kind = "", string id = "") {
        var box = BoxFor(boxName);
        if (box == null) return;

        foreach (var other in Boxes()) other.Selected = ReferenceEquals(other, box);
        box.ShowPicked(kind, id);
        EmitSignal(SignalName.SelectionMoved);
    }

    /// <summary>What is selected: an action, a transition or a condition, else its state, else nothing.</summary>
    public Resource SelectedResource() {
        var box = Boxes().FirstOrDefault(b => b.Selected);
        var state = box?.State;
        if (state == null) return null;

        return box.PickedKind switch {
            FsmRow.Action => (Resource) state.Actions.FirstOrDefault(a => a != null && a.Id == box.PickedId) ?? state,
            FsmRow.Transition => (Resource) state.Transitions.FirstOrDefault(t => t != null && t.Id == box.PickedId) ?? state,
            FsmRow.Condition => (Resource) NodesAround(state, FsmRow.Condition, box.PickedId)?.FirstOrDefault(c => c != null && c.Id == box.PickedId) ?? state,
            _ => state,
        };
    }

    // ---- menus -------------------------------------------------------------------------------

    void OnPopupRequest(Vector2 atPosition) {
        if (Machine == null) return;

        _menuPosition = (atPosition + ScrollOffset) / Zoom;
        _menuState = null;
        _menuRow = null;
        _menu.Clear();
        _menu.AddItem("Add state", MenuAddState);
        PopupAt(_menu, GetScreenPosition() + atPosition);
    }

    void OnBoxMenu(StringName boxName, Vector2 screenPosition) {
        var box = BoxFor(boxName);
        if (box == null) return;

        Pick(boxName);
        _menuState = boxName;
        _menuRow = null;
        _menuPosition = screenPosition;
        _menu.Clear();
        _menu.AddItem("Add action…", MenuAddAction);
        _menu.AddSeparator();
        _menu.AddItem("Make this the initial state", MenuSetInitial);
        _menu.SetItemDisabled(_menu.ItemCount - 1, ReferenceEquals(Machine.InitialState, box.State));
        _menu.AddSeparator();
        _menu.AddItem("Delete", MenuDeleteState);
        PopupAt(_menu, screenPosition);
    }

    void OnRowMenu(StringName boxName, string kind, string id, Vector2 screenPosition) {
        var state = BoxFor(boxName)?.State;
        if (!Place(state, kind, id, out var index, out var count)) return;

        _menuState = boxName;
        _menuKind = kind;
        _menuRow = id;
        _menuPosition = screenPosition;
        _menu.Clear();
        _menu.AddItem("Move up", MenuRowUp);
        _menu.SetItemDisabled(_menu.ItemCount - 1, index == 0);
        _menu.AddItem("Move down", MenuRowDown);
        _menu.SetItemDisabled(_menu.ItemCount - 1, index == count - 1);

        if (kind == FsmRow.Transition) {
            _menu.AddSeparator();
            _menu.AddItem("Add condition…", MenuAddCondition);
        }

        _menu.AddSeparator();
        _menu.AddItem("Delete", MenuRowDelete);
        PopupAt(_menu, screenPosition);
    }
    static void PopupAt(PopupMenu menu, Vector2 screenPosition) {
        menu.Position = (Vector2I) screenPosition;
        menu.ResetSize();
        menu.Popup();
    }

    internal void OnMenuIdPressed(long id) {
        switch (id) {
            case MenuAddState: AddState(_menuPosition); break;
            case MenuAddAction: OfferTypes(typeof(ActionNode), FsmRow.Action, _menuPosition); break;
            case MenuSetInitial: SetInitial(_menuState); break;
            case MenuDeleteState: DeleteStates([_menuState]); break;
            case MenuRowUp: MoveRow(_menuState, _menuKind, _menuRow, -1); break;
            case MenuRowDown: MoveRow(_menuState, _menuKind, _menuRow, 1); break;
            case MenuRowDelete: DeleteRow(_menuState, _menuKind, _menuRow); break;
            case MenuAddCondition: OfferTypes(typeof(ConditionNode), FsmRow.Transition, _menuPosition); break;
        }
    }

    void OnAddActionRequested(StringName boxName, Vector2 screenPosition) {
        Pick(boxName);
        _menuState = boxName;
        OfferTypes(typeof(ActionNode), FsmRow.Action, screenPosition);
    }

    /// <summary>
    /// Opens the node picker, showing only what fits: actions for the state in <see cref="_menuState"/>,
    /// or conditions for its transition in <see cref="_menuRow"/>.
    /// </summary>
    void OfferTypes(Type kind, string forKind, Vector2 screenPosition) {
        var state = BoxFor(_menuState)?.State;
        if (state == null) return;

        _menuKind = forKind;
        var stateName = string.IsNullOrEmpty(state.Name) ? "state" : state.Name;
        if (forKind == FsmRow.Action) Picker.OpenFor($"Add action to {stateName}", "Add", kind);
        else Picker.OpenFor($"Add condition to a transition of {stateName}", "Add", kind);
    }

    internal void OnTypeChosen(string typeName) {
        var type = NodeTypeRegistry.FindByName(typeName)?.Type;
        if (type == null) return;

        if (_menuKind == FsmRow.Action) AddAction(_menuState, type);
        else AddCondition(_menuState, _menuRow, type);
    }
    // ---- states ------------------------------------------------------------------------------

    public FsmState AddState(Vector2 position, string name = null) {
        if (Machine == null) return null;

        var state = new FsmState { Name = UniqueName(string.IsNullOrWhiteSpace(name) ? "State" : name.Trim()), GraphPosition = position };
        Commit($"Misstate: add state {state.Name}", () => Machine.States.Add(state));
        return state;
    }

    string UniqueName(string wanted) {
        if (Machine.FindStateByName(wanted) == null) return wanted;
        for (var i = 2; ; i++) {
            if (Machine.FindStateByName($"{wanted}{i}") == null) return $"{wanted}{i}";
        }
    }

    public void SetInitial(StringName boxName) {
        var state = BoxFor(boxName)?.State;
        if (state == null || ReferenceEquals(Machine.InitialState, state)) return;
        Commit($"Misstate: start in {state.Name}", () => Machine.InitialStateId = state.Id, structural: false);
    }

    /// <summary>
    /// Deletes states. Transitions that led to them are kept and flagged as leading nowhere, rather
    /// than silently taking their conditions with them.
    /// </summary>
    public void DeleteStates(IEnumerable<StringName> boxNames) {
        var doomed = boxNames.Select(n => BoxFor(n)?.State).Where(s => s != null).ToList();
        if (doomed.Count == 0) return;

        Commit(doomed.Count == 1 ? $"Misstate: delete state {doomed[0].Name}" : "Misstate: delete states", () => {
            foreach (var state in doomed) Machine.States.Remove(state);
        });
    }

    void OnDeleteNodesRequest(Godot.Collections.Array<StringName> nodes) {
        if (Machine == null) return;

        // Delete with a row picked removes that action, transition or condition, not its state.
        var picked = Boxes().FirstOrDefault(b => b.Selected && b.PickedId != "");
        if (picked != null && nodes.Count == 1 && nodes[0] == picked.Name) {
            DeleteRow(picked.Name, picked.PickedKind, picked.PickedId);
            return;
        }
        DeleteStates(nodes);
    }

    void OnEndNodeMove() {
        if (Machine == null || _rebuilding) return;

        Commit("Misstate: move", () => {
            foreach (var box in Boxes()) {
                if (box.State != null) box.State.GraphPosition = box.PositionOffset;
            }
        }, structural: false);
    }

    // ---- actions -----------------------------------------------------------------------------

    public MissNode AddAction(StringName boxName, Type type) {
        var state = BoxFor(boxName)?.State;
        if (state == null || type == null) return null;

        var action = NodeTypeRegistry.Create(type);
        Commit($"Misstate: add {NodeAttributes.NameOf(type)} to {state.Name}", () => state.Actions.Add(action));
        return action;
    }

    // ---- transitions -------------------------------------------------------------------------

    /// <summary>
    /// A wire was dragged from an output port onto a state: from a transition's own port it
    /// retargets that transition, from the spare port it adds a new one.
    /// </summary>
    void OnConnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort) {
        var from = BoxFor(fromNode)?.State;
        var to = BoxFor(toNode)?.State;
        if (from == null || to == null) return;
        LeadTo(from, (int) fromPort, to);
    }

    /// <summary>Dropping a wire on empty canvas makes a new state there, and leads the transition to it.</summary>
    void OnConnectionToEmpty(StringName fromNode, long fromPort, Vector2 releasePosition) {
        var from = BoxFor(fromNode)?.State;
        if (from == null) return;

        var state = new FsmState { Name = UniqueName("State"), GraphPosition = (releasePosition + ScrollOffset) / Zoom };
        Commit($"Misstate: add state {state.Name}", () => {
            Machine.States.Add(state);
            Lead(from, (int) fromPort, state);
        });
    }

    /// <summary>Leads the transition behind <paramref name="port"/> to a state, adding one for the spare port.</summary>
    public void LeadTo(FsmState from, int port, FsmState to) {
        var existing = TransitionAt(from, port);
        if (existing != null && existing.TargetStateId == to.Id) return;

        Commit(existing == null ? $"Misstate: add transition to {to.Name}" : $"Misstate: lead transition to {to.Name}",
            () => Lead(from, port, to));
    }

    static void Lead(FsmState from, int port, FsmState to) {
        if (TransitionAt(from, port) is { } transition) transition.TargetStateId = to.Id;
        else from.Transitions.Add(new FsmTransition { TargetStateId = to.Id });
    }

    /// <summary>The transition behind an output port, or null for the spare port after the last one.</summary>
    static FsmTransition TransitionAt(FsmState state, int port) {
        var transitions = state.Transitions.Where(t => t != null).ToList();
        return port >= 0 && port < transitions.Count ? transitions[port] : null;
    }

    public MissNode AddCondition(StringName boxName, string transitionId, Type type) {
        var state = BoxFor(boxName)?.State;
        var transition = state?.Transitions.FirstOrDefault(t => t != null && t.Id == transitionId);
        if (transition == null || type == null) return null;

        var condition = NodeTypeRegistry.Create(type);
        Commit($"Misstate: add condition {NodeAttributes.NameOf(type)}", () => transition.Conditions.Add(condition));
        return condition;
    }

    // ---- rows --------------------------------------------------------------------------------

    /// <summary>
    /// The list of nodes a row sits in: the state's actions for an action row, the conditions of
    /// the transition it belongs to for a condition row. Null for anything else.
    /// </summary>
    static Godot.Collections.Array<MissNode> NodesAround(FsmState state, string kind, string id) => kind switch {
        FsmRow.Action => state?.Actions,
        FsmRow.Condition => state?.Transitions.FirstOrDefault(t => t != null && t.Conditions.Any(c => c != null && c.Id == id))?.Conditions,
        _ => null,
    };

    /// <summary>Where a row sits among its siblings, and how many of them there are.</summary>
    static bool Place(FsmState state, string kind, string id, out int index, out int count) {
        (index, count) = (-1, 0);
        if (state == null || string.IsNullOrEmpty(id)) return false;

        if (kind == FsmRow.Transition) {
            count = state.Transitions.Count;
            for (var i = 0; i < count && index < 0; i++) {
                if (state.Transitions[i]?.Id == id) index = i;
            }
        }
        else if (NodesAround(state, kind, id) is { } nodes) {
            count = nodes.Count;
            for (var i = 0; i < count && index < 0; i++) {
                if (nodes[i]?.Id == id) index = i;
            }
        }
        return index >= 0;
    }

    /// <summary>
    /// Actions run, transitions are considered and conditions are checked top to bottom — so the
    /// order of the rows matters.
    /// </summary>
    public void MoveRow(StringName boxName, string kind, string id, int by) {
        var state = BoxFor(boxName)?.State;
        if (!Place(state, kind, id, out var index, out var count)) return;
        var target = index + by;
        if (target < 0 || target >= count) return;

        Commit($"Misstate: reorder {kind}", () => {
            if (kind == FsmRow.Transition) Shift(state.Transitions, index, target);
            else Shift(NodesAround(state, kind, id), index, target);
        });
    }

    static void Shift<[MustBeVariant] T>(Godot.Collections.Array<T> list, int from, int to) {
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
    }

    public void DeleteRow(StringName boxName, string kind, string id) {
        var state = BoxFor(boxName)?.State;
        if (!Place(state, kind, id, out var index, out _)) return;

        Commit($"Misstate: delete {kind}", () => {
            if (kind == FsmRow.Transition) state.Transitions.RemoveAt(index);
            else NodesAround(state, kind, id).RemoveAt(index);
        });
    }
    // ---- undo/redo ---------------------------------------------------------------------------

    /// <summary>
    /// Applies <paramref name="mutate"/> and records it as one undoable action. Undo and redo
    /// restore a snapshot of the structure rather than replaying the edit.
    /// </summary>
    /// <param name="structural">False for edits that leave every box, row and wire where it is.</param>
    void Commit(string actionName, Action mutate, bool structural = true) {
        var before = TakeSnapshot();
        mutate();
        var after = TakeSnapshot();

        var undoRedo = Engine.IsEditorHint() ? EditorInterface.Singleton?.GetEditorUndoRedo() : null;
        if (undoRedo != null) {
            // Not marked unsaved in Godot's history: saving the machine cannot mark that history
            // saved again. The panel tracks unsaved machines.
            undoRedo.CreateAction(actionName, UndoRedo.MergeMode.Disable, Machine, false, false);
            undoRedo.AddDoMethod(this, MethodName.RestoreSnapshot, after);
            undoRedo.AddUndoMethod(this, MethodName.RestoreSnapshot, before);

            // The mutation is already applied, so do not let CommitAction replay it.
            undoRedo.CommitAction(false);
        }

        if (structural) QueueRebuild();
        else RefreshBoxes();

        EmitSignal(SignalName.MachineDirtied);
    }

    /// <summary>
    /// The structure of the machine: its states in order, where each sits, its actions in order, its
    /// transitions in order with where each leads and what it checks. The objects themselves travel
    /// along, so undo can bring a deleted one back as the same object.
    /// </summary>
    internal Godot.Collections.Dictionary TakeSnapshot() {
        var states = new Godot.Collections.Array();
        foreach (var state in Machine.States) {
            if (state == null) continue;

            var transitions = new Godot.Collections.Array();
            foreach (var transition in state.Transitions) {
                if (transition == null) continue;
                transitions.Add(new Godot.Collections.Dictionary {
                    { "transition", transition },
                    { "target", transition.TargetStateId },
                    { "conditions", Nodes(transition.Conditions) },
                });
            }
            states.Add(new Godot.Collections.Dictionary {
                { "state", state },
                { "pos", state.GraphPosition },
                { "actions", Nodes(state.Actions) },
                { "transitions", transitions },
            });
        }

        return new Godot.Collections.Dictionary {
            { "machine", Machine },
            { "initial", Machine.InitialStateId },
            { "states", states },
        };
    }

    static Godot.Collections.Array Nodes(IEnumerable<MissNode> nodes) {
        var kept = new Godot.Collections.Array();
        foreach (var node in nodes) {
            if (node != null) kept.Add(node);
        }
        return kept;
    }

    static Godot.Collections.Array<MissNode> NodesFrom(Variant kept) {
        var nodes = new Godot.Collections.Array<MissNode>();
        foreach (var item in kept.AsGodotArray()) {
            if (item.AsGodotObject() is MissNode node) nodes.Add(node);
        }
        return nodes;
    }

    public void RestoreSnapshot(Godot.Collections.Dictionary snapshot) {
        // Undo is shared by every machine edited this session: an edit of another machine is shown
        // by opening that machine, never applied to whichever one happens to be open.
        if (snapshot.TryGetValue("machine", out var owner) && owner.AsGodotObject() is Fsm other && !ReferenceEquals(other, Machine)) {
            EmitSignal(SignalName.MachineRequested, other);
            if (!ReferenceEquals(other, Machine)) return;
        }
        if (Machine == null) return;

        var states = new Godot.Collections.Array<FsmState>();
        foreach (var item in snapshot["states"].AsGodotArray()) {
            var data = item.AsGodotDictionary();
            if (data["state"].AsGodotObject() is not FsmState state) continue;

            state.GraphPosition = data["pos"].AsVector2();
            state.Actions = NodesFrom(data["actions"]);

            var transitions = new Godot.Collections.Array<FsmTransition>();
            foreach (var entry in data["transitions"].AsGodotArray()) {
                var kept = entry.AsGodotDictionary();
                if (kept["transition"].AsGodotObject() is not FsmTransition transition) continue;
                transition.TargetStateId = kept["target"].AsString();
                transition.Conditions = NodesFrom(kept["conditions"]);
                transitions.Add(transition);
            }
            state.Transitions = transitions;
            states.Add(state);
        }
        Machine.States = states;
        Machine.InitialStateId = snapshot["initial"].AsString();

        QueueRebuild();
        EmitSignal(SignalName.MachineDirtied);
    }
}
#endif
