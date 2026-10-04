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
    internal const int MenuAddReroute = 4;
    internal const int MenuDeleteReroute = 5;
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

    /// <summary>The wire the canvas menu was opened on — the box and port it leaves — or null for none.</summary>
    StringName _menuWireFrom;
    int _menuWirePort;

    /// <summary>
    /// How far into a box a port can be grabbed. GraphEdit's own reach would cover a reroute from
    /// end to end, leaving nowhere to drag it by.
    /// </summary>
    public const int PortReach = 10;

    /// <summary>How close to a wire a right-click has to be to mean that wire.</summary>
    const float WireReach = 10f;

    /// <summary>What the graph was last built from, so an edit made elsewhere is noticed.</summary>
    string _builtFrom = "";

    /// <summary>Where a running game last reported the machine to be, kept so a rebuild can show it again.</summary>
    bool _live;
    string _liveState = "";
    byte[] _liveStatuses = [];
    string _liveEnteredBy = "";

    GodotObject _wireOverlay;

    /// <summary>Paints the wires of a running machine. See <see cref="FsmWireOverlay"/>.</summary>
    public FsmWireOverlay WireOverlay => ReloadSafe.Get<FsmWireOverlay>(ref _wireOverlay);

    public override void _Ready() {
        RightDisconnects = false;
        ShowArrangeButton = false;
        MinimapEnabled = false;
        AddThemeConstantOverride("port_hotzone_inner_extent", PortReach);

        // Items are filled in per right-click.
        _menu = new PopupMenu { Name = "Menu" };
        AddChild(_menu, false, InternalMode.Back);
        _menu.Connect(PopupMenu.SignalName.IdPressed, new Callable(this, MethodName.OnMenuIdPressed));

        // Internal, so it never shows up in the child scans that drive the graph.
        var picker = new CreateNodeDialog { Name = "Picker" };
        _picker = picker;
        AddChild(picker, false, InternalMode.Back);
        picker.Connect(CreateNodeDialog.SignalName.TypeChosen, new Callable(this, MethodName.OnTypeChosen));

        // An ordinary child, not an internal one — see PlaceWireOverlay.
        var overlay = new FsmWireOverlay();
        _wireOverlay = overlay;
        AddChild(overlay);
        PlaceWireOverlay();

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

    IEnumerable<FsmRerouteBox> RerouteBoxes() => GetChildren().OfType<FsmRerouteBox>();

    public FsmRerouteBox RerouteBoxFor(string rerouteId)
        => string.IsNullOrEmpty(rerouteId) ? null : RerouteBoxes().FirstOrDefault(b => b.Name == rerouteId);

    /// <summary>Whether a wire can end at this id: a state or a reroute that has a box.</summary>
    bool HasBox(string id) => BoxFor(id) != null || RerouteBoxFor(id) != null;

    void ClearGraph() {
        ClearConnections();

        // RemoveChild before QueueFree: a doomed box is still a child until the end of the frame,
        // and Godot would rename its replacement to "<id>@2", breaking every ConnectNode that follows.
        foreach (var box in GetChildren().OfType<GraphNode>().ToList()) {
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
        var selectedReroutes = RerouteBoxes().Where(b => b.Selected).Select(b => b.Name.ToString()).ToList();

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
                box.Connect(GraphElement.SignalName.PositionOffsetChanged, new Callable(this, MethodName.OnBoxMoved));

                // A machine built in code has no positions yet; spread it out rather than piling it up.
                box.PositionOffset = state.GraphPosition != Vector2.Zero || machine.States.Count == 1
                    ? state.GraphPosition
                    : new Vector2(60 + 280 * (spot % 4), 60 + 220 * (spot / 4));
                spot++;
            }

            foreach (var reroute in machine.Reroutes) {
                if (reroute == null || HasBox(reroute.Id)) continue;

                var box = new FsmRerouteBox { Name = reroute.Id };
                AddChild(box);
                box.Bind(reroute);
                box.Connect(FsmRerouteBox.SignalName.MenuRequested, new Callable(this, MethodName.OnRerouteMenu));
                box.Connect(GraphElement.SignalName.PositionOffsetChanged, new Callable(this, MethodName.OnBoxMoved));
                box.PositionOffset = reroute.GraphPosition;
            }

            foreach (var state in machine.States) {
                if (state == null) continue;
                var port = 0;
                foreach (var transition in state.Transitions) {
                    if (transition == null) continue;
                    if (HasBox(transition.TargetStateId)) ConnectNode(state.Id, port, transition.TargetStateId, 0);
                    port++;
                }
            }
            foreach (var reroute in machine.Reroutes) {
                if (reroute != null && HasBox(reroute.TargetId)) ConnectNode(reroute.Id, 0, reroute.TargetId, 0);
            }
        }

        _builtFrom = Shape();
        RefreshBoxes();
        UpdateFlips();
        _rebuilding = false;

        if (BoxFor(selectedState) is { } again) {
            again.Selected = true;
            again.ShowPicked(pickedKind, pickedId);
        }
        foreach (var name in selectedReroutes) {
            if (RerouteBoxFor(name) is { } reroute) reroute.Selected = true;
        }

        // A paused game sends nothing further, so an edit that rebuilds the graph has to repaint it.
        if (_live) PaintLive();
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
                $"{t.Id}>{t.TargetStateId}[{string.Join(" ", t.Conditions.Where(c => c != null).Select(c => c.Id))}]"))}"))
            + "#" + string.Join("|", machine.Reroutes.Where(r => r != null).Select(r => $"{r.Id}>{r.TargetId}"));
    }

    // ---- live debugging ----------------------------------------------------------------------

    /// <summary>Shows where a running machine is: its current state and what that state's actions last returned.</summary>
    /// <param name="enteredBy">Id of the transition that brought the machine into the state, or empty.</param>
    public void ShowLive(string stateId, byte[] statuses, string enteredBy = "") {
        _live = true;
        _liveState = stateId ?? "";
        _liveStatuses = statuses ?? [];
        _liveEnteredBy = enteredBy ?? "";
        PaintLive();
    }

    void PaintLive() {
        foreach (var box in Boxes()) box.ShowLive(box.Name == _liveState, _liveStatuses);

        PlaceWireOverlay();
        WireOverlay?.ShowWires(LiveWires());
    }

    public void ClearLive() {
        if (!_live) return;
        _live = false;
        foreach (var box in Boxes()) box.ClearLive();
        WireOverlay?.ClearWires();
    }

    /// <summary>
    /// The wires to highlight: every way out of the current state, and the way the machine came in —
    /// each followed through its reroutes. A transition that was edited away since the game started
    /// is simply not found.
    /// </summary>
    List<LiveWire> LiveWires() {
        var wires = new List<LiveWire>();
        var machine = Machine;
        if (machine == null) return wires;

        foreach (var state in machine.States) {
            if (state == null) continue;
            var port = 0;
            foreach (var transition in state.Transitions) {
                if (transition == null) continue;

                var taken = transition.Id == _liveEnteredBy && machine.Destination(transition.TargetStateId)?.Id == _liveState;
                if (taken) Follow(wires, state.Id, port, transition.TargetStateId, taken: true);
                if (state.Id == _liveState) Follow(wires, state.Id, port, transition.TargetStateId, taken: false);
                port++;
            }
        }
        return wires;
    }

    void Follow(List<LiveWire> wires, string from, int port, string targetId, bool taken) {
        for (var hops = 0; hops <= Machine.Reroutes.Count && HasBox(targetId); hops++) {
            wires.Add(new LiveWire(from, port, targetId, taken));
            if (Machine.FindReroute(targetId) is not { } reroute) return;
            (from, port, targetId) = (reroute.Id, 0, reroute.TargetId);
        }
    }

    /// <summary>
    /// Keeps the highlighted wires on top of the plain ones but beneath the boxes. GraphEdit draws
    /// its wires in <c>_connection_layer</c>, an ordinary first child, so the overlay is an ordinary
    /// child right after it; boxes are added behind it, never in front.
    /// </summary>
    void PlaceWireOverlay() {
        var overlay = WireOverlay;
        var layer = GetChildren().FirstOrDefault(c => c.Name == "_connection_layer");
        if (overlay == null || layer == null || overlay.GetParent() != this) return;
        if (overlay.GetIndex() != layer.GetIndex() + 1) MoveChild(overlay, layer.GetIndex() + 1);
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
        foreach (var reroute in RerouteBoxes()) reroute.Selected = false;
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

        // On a wire, a reroute is put into that wire; anywhere else it starts out loose.
        var wire = GetClosestConnectionAtPoint(atPosition, WireReach);
        _menuWireFrom = wire.Count > 0 ? wire["from_node"].AsStringName() : null;
        _menuWirePort = wire.Count > 0 ? wire["from_port"].AsInt32() : 0;

        _menu.Clear();
        _menu.AddItem("Add state", MenuAddState);
        _menu.AddItem(_menuWireFrom != null ? "Add reroute to this wire" : "Add reroute", MenuAddReroute);
        PopupAt(_menu, GetScreenPosition() + atPosition);
    }

    void OnRerouteMenu(StringName boxName, Vector2 screenPosition) {
        if (RerouteBoxFor(boxName) == null) return;

        _menuState = boxName;
        _menuRow = null;
        _menuPosition = screenPosition;
        _menu.Clear();
        _menu.AddItem("Delete", MenuDeleteReroute);
        PopupAt(_menu, screenPosition);
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
            case MenuAddReroute: AddReroute(_menuPosition, _menuWireFrom, _menuWirePort); break;
            case MenuDeleteReroute: DeleteBoxes([_menuState]); break;
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
    public void DeleteStates(IEnumerable<StringName> boxNames) => DeleteBoxes(boxNames);

    /// <summary>
    /// Deletes states and reroutes in one step. What led to a deleted reroute leads on to where the
    /// reroute led, so taking one out of a wire leaves the wire whole.
    /// </summary>
    public void DeleteBoxes(IEnumerable<StringName> boxNames) {
        var names = boxNames.ToList();
        var states = names.Select(n => BoxFor(n)?.State).Where(s => s != null).ToList();
        var reroutes = names.Select(n => RerouteBoxFor(n)?.Reroute).Where(r => r != null).ToList();
        if (states.Count + reroutes.Count == 0) return;

        var what = (states.Count, reroutes.Count) switch {
            (1, 0) => $"delete state {states[0].Name}",
            (_, 0) => "delete states",
            (0, 1) => "delete reroute",
            (0, _) => "delete reroutes",
            _ => "delete",
        };
        Commit($"Misstate: {what}", () => {
            foreach (var reroute in reroutes) Bypass(reroute);
            foreach (var state in states) Machine.States.Remove(state);
        });
    }

    void Bypass(FsmReroute reroute) {
        foreach (var transition in Machine.States.Where(s => s != null).SelectMany(s => s.Transitions)) {
            if (transition?.TargetStateId == reroute.Id) transition.TargetStateId = reroute.TargetId;
        }
        foreach (var other in Machine.Reroutes) {
            if (other != null && other.TargetId == reroute.Id) other.TargetId = reroute.TargetId;
        }
        Machine.Reroutes.Remove(reroute);
    }

    void OnDeleteNodesRequest(Godot.Collections.Array<StringName> nodes) {
        if (Machine == null) return;

        // Delete with a row picked removes that action, transition or condition, not its state.
        var picked = Boxes().FirstOrDefault(b => b.Selected && b.PickedId != "");
        if (picked != null && nodes.Count == 1 && nodes[0] == picked.Name) {
            DeleteRow(picked.Name, picked.PickedKind, picked.PickedId);
            return;
        }
        DeleteBoxes(nodes);
    }

    void OnEndNodeMove() {
        if (Machine == null || _rebuilding) return;

        Commit("Misstate: move", () => {
            foreach (var box in Boxes()) {
                if (box.State != null) box.State.GraphPosition = box.PositionOffset;
            }
            foreach (var box in RerouteBoxes()) {
                if (box.Reroute != null) box.Reroute.GraphPosition = box.PositionOffset;
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
        if (!HasBox(toNode)) return;

        if (BoxFor(fromNode)?.State is { } from) LeadTo(from, (int) fromPort, toNode);
        else if (RerouteBoxFor(fromNode)?.Reroute is { } reroute) LeadOn(reroute, toNode);
    }

    /// <summary>Dropping a wire on empty canvas makes a new state there, and leads the transition to it.</summary>
    void OnConnectionToEmpty(StringName fromNode, long fromPort, Vector2 releasePosition) {
        var from = BoxFor(fromNode)?.State;
        var reroute = RerouteBoxFor(fromNode)?.Reroute;
        if (from == null && reroute == null) return;

        var state = new FsmState { Name = UniqueName("State"), GraphPosition = (releasePosition + ScrollOffset) / Zoom };
        Commit($"Misstate: add state {state.Name}", () => {
            Machine.States.Add(state);
            if (from != null) Lead(from, (int) fromPort, state.Id);
            else reroute.TargetId = state.Id;
        });
    }

    /// <summary>Leads the transition behind <paramref name="port"/> to a state, adding one for the spare port.</summary>
    public void LeadTo(FsmState from, int port, FsmState to) => LeadTo(from, port, to.Id);

    /// <summary>As above, to a state or a reroute by id.</summary>
    public void LeadTo(FsmState from, int port, string targetId) {
        var existing = TransitionAt(from, port);
        if (existing != null && existing.TargetStateId == targetId) return;

        Commit(existing == null ? $"Misstate: add transition to {TargetName(targetId)}" : $"Misstate: lead transition to {TargetName(targetId)}",
            () => Lead(from, port, targetId));
    }

    static void Lead(FsmState from, int port, string targetId) {
        if (TransitionAt(from, port) is { } transition) transition.TargetStateId = targetId;
        else from.Transitions.Add(new FsmTransition { TargetStateId = targetId });
    }

    string TargetName(string targetId) => Machine.FindState(targetId)?.Name ?? "a reroute";

    // ---- reroutes ----------------------------------------------------------------------------

    /// <summary>
    /// Adds a reroute, centred on <paramref name="position"/>. Given the box and port a wire leaves,
    /// the reroute is put into that wire: the wire now ends at it, and it leads on to where the wire went.
    /// </summary>
    public FsmReroute AddReroute(Vector2 position, StringName wireFrom = null, int wirePort = 0) {
        if (Machine == null) return null;

        var transition = BoxFor(wireFrom)?.State is { } state ? TransitionAt(state, wirePort) : null;
        var before = RerouteBoxFor(wireFrom)?.Reroute;

        var reroute = new FsmReroute {
            GraphPosition = position - (FsmRerouteBox.BodySize / 2),
            TargetId = transition?.TargetStateId ?? before?.TargetId ?? "",
        };
        Commit("Misstate: add reroute", () => {
            Machine.Reroutes.Add(reroute);
            if (transition != null) transition.TargetStateId = reroute.Id;
            else if (before != null) before.TargetId = reroute.Id;
        });
        return reroute;
    }

    /// <summary>A double-click on a wire puts a reroute into it.</summary>
    public override void _GuiInput(InputEvent @event) {
        if (@event is not InputEventMouseButton { Pressed: true, DoubleClick: true, ButtonIndex: MouseButton.Left } click) return;
        if (Machine == null) return;

        var wire = GetClosestConnectionAtPoint(click.Position, WireReach);
        if (wire.Count == 0) return;

        AddReroute((click.Position + ScrollOffset) / Zoom, wire["from_node"].AsStringName(), wire["from_port"].AsInt32());
        AcceptEvent();
    }

    // GraphEdit only knows wires that leave a box on the right and arrive on the left. A reroute
    // that leads back to the left is turned round instead (FsmRerouteBox.Flipped), which takes
    // three things: knowing when, drawing its wires from the other end, and grabbing them there.

    bool _updatingFlips;

    void OnBoxMoved() => UpdateFlips();

    /// <summary>Turns every reroute to face what it leads to.</summary>
    void UpdateFlips() {
        if (_updatingFlips) return;
        _updatingFlips = true;
        foreach (var box in RerouteBoxes().ToList()) {
            var flipped = LeadsLeft(box);
            if (flipped == box.Flipped) continue;

            box.Flipped = flipped;
            // GraphEdit works out the wires of a box again when it has moved; this asks for just that.
            box.EmitSignal(GraphElement.SignalName.PositionOffsetChanged);
        }
        _updatingFlips = false;
    }

    bool LeadsLeft(FsmRerouteBox box) {
        var targetId = box.Reroute?.TargetId;
        var centre = box.PositionOffset.X + FsmRerouteBox.BodySize.X / 2;
        if (BoxFor(targetId) is { } state) return state.PositionOffset.X < centre;
        if (RerouteBoxFor(targetId) is { } next) return next.PositionOffset.X < box.PositionOffset.X;
        return false;
    }

    /// <summary>
    /// Wires are drawn with corners instead of GraphEdit's curves. A wire leaves a flipped reroute
    /// at its left end heading left, and arrives at one at its right end. The ends are told by
    /// where they are: GraphEdit passes nothing but the two positions.
    /// </summary>
    public override Vector2[] _GetConnectionLine(Vector2 fromPosition, Vector2 toPosition) {
        var (leaves, arrives) = (1f, 1f);
        foreach (var box in RerouteBoxes()) {
            if (!box.Flipped) continue;

            var left = box.GetInputPortPosition(0) * Zoom;
            var right = box.GetOutputPortPosition(0) * Zoom;
            // Wires that exist are measured from the graph's origin, one being dragged from the view's.
            foreach (var origin in new[] { box.PositionOffset * Zoom, box.Position }) {
                if (leaves > 0 && fromPosition.DistanceSquaredTo(origin + right) < 1f) (fromPosition, leaves) = (origin + left, -1f);
                if (arrives > 0 && toPosition.DistanceSquaredTo(origin + left) < 1f) (toPosition, arrives) = (origin + right, -1f);
            }
        }

        return ElbowLine(fromPosition, leaves, toPosition, arrives, WireStub * Zoom, LaneOf(fromPosition, toPosition) * WireLane * Zoom);
    }

    /// <summary>
    /// Which lane a wire out of a state takes, so that the wires of one state turn side by side
    /// instead of on top of each other: the port nearest to where the wire is going turns first.
    /// </summary>
    int LaneOf(Vector2 fromPosition, Vector2 toPosition) {
        foreach (var box in Boxes()) {
            var ports = box.GetOutputPortCount();
            for (var port = 0; port < ports; port++) {
                var at = box.GetOutputPortPosition(port) * Zoom;
                if (fromPosition.DistanceSquaredTo(box.PositionOffset * Zoom + at) >= 1f && fromPosition.DistanceSquaredTo(box.Position + at) >= 1f) continue;
                return toPosition.Y > fromPosition.Y ? ports - 1 - port : port;
            }
        }
        return 0;
    }

    /// <summary>How far apart the wires of one state turn.</summary>
    const float WireLane = 8f;

    /// <summary>How far a wire runs straight out of a port, and into one, before it may turn.</summary>
    const float WireStub = 18f;

    /// <summary>
    /// A wire of horizontal and vertical runs only, as in the Missbehave graph. It leaves its port
    /// heading <paramref name="leaves"/> (1 for right, -1 for left) and arrives heading
    /// <paramref name="arrives"/>, with a straight bit at either end so it never turns right at a port.
    /// </summary>
    /// <param name="lane">How much further than the stub this wire runs before its first turn.</param>
    static Vector2[] ElbowLine(Vector2 from, float leaves, Vector2 to, float arrives, float stub, float lane = 0) {
        var start = from + new Vector2(leaves * (stub + lane), 0);
        var end = to - new Vector2(arrives * stub, 0);
        var points = new List<Vector2> { from };

        if (leaves != arrives) {
            // Out and back in from the same side: one turn-round, past whichever end sticks out further.
            var x = leaves > 0 ? Mathf.Max(start.X, end.X) : Mathf.Min(start.X, end.X);
            points.Add(new Vector2(x, from.Y));
            points.Add(new Vector2(x, to.Y));
        }
        else if ((end.X - start.X) * leaves >= 0) {
            // The target lies ahead: one step up or down, close to where the wire starts — halfway
            // would often be behind a box that sits in between.
            points.Add(start);
            points.Add(new Vector2(start.X, to.Y));
        }
        else {
            // The target lies behind: out, back across above both ends — a state's input sits at its
            // top, so that clears the boxes — and in from the far side.
            var y = Mathf.Min(from.Y, to.Y) - 3 * stub - lane;
            points.Add(start);
            points.Add(new Vector2(start.X, y));
            points.Add(new Vector2(end.X, y));
            points.Add(end);
        }

        points.Add(to);
        return [.. points.Where((point, i) => i == 0 || !point.IsEqualApprox(points[i - 1]))];
    }

    public override bool _IsInInputHotzone(GodotObject inNode, int inPort, Vector2 mousePosition)
        => inNode is GraphNode node && InHotzone(node, inPort, mousePosition, input: true);

    public override bool _IsInOutputHotzone(GodotObject inNode, int inPort, Vector2 mousePosition)
        => inNode is GraphNode node && InHotzone(node, inPort, mousePosition, input: false);

    /// <summary>
    /// Whether the mouse is where a port can be grabbed — as GraphEdit decides it, but with the ends
    /// of a flipped reroute swapped. The mouse position comes divided by the zoom.
    /// </summary>
    bool InHotzone(GraphNode node, int port, Vector2 mouse, bool input) {
        var reroute = node as FsmRerouteBox;
        var onLeft = input != (reroute?.Flipped ?? false);
        if (port < 0 || port >= (input ? node.GetInputPortCount() : node.GetOutputPortCount())) return false;

        // Only a reroute is ever flipped, and it has the one port at either end.
        var local = onLeft ? node.GetInputPortPosition(input ? port : 0) : node.GetOutputPortPosition(input ? 0 : port);
        var at = (local * Zoom + node.Position) / Zoom;
        var inner = GetThemeConstant("port_hotzone_inner_extent");
        var outer = GetThemeConstant("port_hotzone_outer_extent");
        var height = node.GetThemeIcon("port")?.GetHeight() ?? 10;
        var zone = new Rect2(at.X - (onLeft ? outer : inner), at.Y - height / 2f, inner + outer, height);
        if (!zone.HasPoint(mouse)) return false;

        // What a box shows wins over the ports beside it: a click on a row is a click on that row.
        foreach (var box in Boxes()) {
            var within = (mouse * Zoom - box.Position) / Zoom;
            if (!new Rect2(Vector2.Zero, box.Size).HasPoint(within)) continue;
            if (box.GetChildren().OfType<Control>().Any(child => child.Visible && child.GetRect().HasPoint(within))) return false;
        }
        return true;
    }

    /// <summary>Leads a reroute on to a state or another reroute — unless that would send its wires round in a circle.</summary>
    public void LeadOn(FsmReroute reroute, string targetId) {
        if (reroute == null || reroute.TargetId == targetId) return;

        var seen = 0;
        for (var at = targetId; Machine.FindReroute(at) is { } next && seen <= Machine.Reroutes.Count; at = next.TargetId, seen++) {
            if (!ReferenceEquals(next, reroute)) continue;
            EmitSignal(SignalName.EditRejected, "A reroute cannot lead back to itself.");
            return;
        }

        Commit($"Misstate: lead reroute to {TargetName(targetId)}", () => reroute.TargetId = targetId);
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
    /// transitions in order with where each leads and what it checks — and its reroutes. The objects themselves travel
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

        var reroutes = new Godot.Collections.Array();
        foreach (var reroute in Machine.Reroutes) {
            if (reroute == null) continue;
            reroutes.Add(new Godot.Collections.Dictionary {
                { "reroute", reroute },
                { "pos", reroute.GraphPosition },
                { "target", reroute.TargetId },
            });
        }

        return new Godot.Collections.Dictionary {
            { "machine", Machine },
            { "initial", Machine.InitialStateId },
            { "states", states },
            { "reroutes", reroutes },
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

        var reroutes = new Godot.Collections.Array<FsmReroute>();
        foreach (var item in snapshot.TryGetValue("reroutes", out var kept) ? kept.AsGodotArray() : []) {
            var data = item.AsGodotDictionary();
            if (data["reroute"].AsGodotObject() is not FsmReroute reroute) continue;

            reroute.GraphPosition = data["pos"].AsVector2();
            reroute.TargetId = data["target"].AsString();
            reroutes.Add(reroute);
        }
        Machine.Reroutes = reroutes;

        QueueRebuild();
        EmitSignal(SignalName.MachineDirtied);
    }
}
#endif
