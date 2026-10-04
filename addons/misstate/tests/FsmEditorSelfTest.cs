#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;
using Misscore.Editor;
using Misstate.Editor;
using Misstate.Tests;

namespace Misstate;

/// <summary>
/// Headless self test for the graph editor, driven through the same signals GraphEdit emits when
/// you click. Exits with code 0 on success, 1 on failure.
/// <code>
/// godot --headless --path &lt;project&gt; res://addons/misstate/tests/editor_self_test.tscn
/// </code>
/// </summary>
public partial class FsmEditorSelfTest : Node {
    readonly List<string> _failures = [];
    int _checks;

    FsmEditorPanel _panel;
    FsmGraphEdit _graph;
    Fsm _machine;
    FsmState _idle;
    FsmState _work;
    FsmProbeAction _action;

    Resource _lastEdited;
    int _inspectorCalls;

    public override async void _Ready() {
        // The probes are hidden from the pickers in the editor; the tests create them through one.
        NodeTypeRegistry.IncludeTestTypes = true;
        BuildMachine();

        _panel = new FsmEditorPanel();
        AddChild(_panel);
        _panel.EditInInspector = resource => {
            _inspectorCalls++;
            _lastEdited = resource;
            _panel.Highlight(resource);
        };
        _panel.OpenMachine(_machine);
        _graph = _panel.Graph;
        await Settle();

        EditorSourcesDoNotWireDelegates();
        BoxesRowsAndWiresExist();
        await TheHeaderSitsInsideTheBox();
        await StatesAreAddedAndNamedApart();
        await ActionsAreAddedFromThePicker();
        await ActionsAreOrderedAndDeleted();
        await WiresMakeAndLeadTransitions();
        await TransitionsAreOrderedAndDeleted();
        await ConditionsAreAddedToATransition();
        await TheInitialStateIsMarked();
        await MovingABoxIsRemembered();
        await SelectingGoesToTheInspectorWithoutLooping();
        await InspectorEditsReachTheGraph();
        await UndoRestoresTheStructure();
        await DeletingAStateLeavesItsTransitionsFlagged();
        await ReroutesLeadWiresAround();
        await NodesLinkToTheBlackboardBesideTheGraph();
        await OnlyOnePanelClaimsAParameter();
        await SavingAndRevertingFollowTheFile();
        await TheGraphShowsWhereARunningMachineIs();
        await ANodeClassWithoutToolIsKeptOut();

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"misstate editor self test: {_checks - _failures.Count}/{_checks} checks passed");
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    void BuildMachine() {
        _action = new FsmProbeAction();
        _idle = new FsmState { Name = "Idle", GraphPosition = new Vector2(40, 40) };
        _work = new FsmState { Name = "Work", GraphPosition = new Vector2(360, 40) };
        _work.Actions.Add(_action);

        var start = new FsmTransition { TargetStateId = _work.Id };
        start.Conditions.Add(new FsmProbeCondition { AtLeast = 1 });
        _idle.Transitions.Add(start);
        _work.Transitions.Add(new FsmTransition { TargetStateId = _idle.Id, On = FsmTrigger.Finished });

        _machine = new Fsm();
        _machine.States.Add(_idle);
        _machine.States.Add(_work);
    }

    // ---- structure ---------------------------------------------------------------------------

    void BoxesRowsAndWiresExist() {
        Check("every state has a box", _graph.BoxFor(_idle.Id) != null && _graph.BoxFor(_work.Id) != null);
        Check("a box shows the state's name", Text(_idle, "Header/StateName") == "Idle");
        Check("every action has a row", Rows(_work, FsmRow.Action).Count == 1 && Rows(_idle, FsmRow.Action).Count == 0);
        Check("a row names its action", RowText(_work, FsmRow.Action, 0) == nameof(FsmProbeAction));
        Check("a box says how the state works through its actions", Text(_work, "Mode") == "sequence" && Text(_idle, "Mode") == "waits");
        Check("every transition has a row", Rows(_idle, FsmRow.Transition).Count == 1 && Rows(_work, FsmRow.Transition).Count == 1);
        Check("a row says where it leads and when",
            RowText(_idle, FsmRow.Transition, 0) == "→ Work" && RowText(_work, FsmRow.Transition, 0) == "→ Idle  when done");
        Check("every condition has a row below its transition",
            Rows(_idle, FsmRow.Condition).Count == 1 && RowText(_idle, FsmRow.Condition, 0) == $"if {nameof(FsmProbeCondition)}"
            && _graph.BoxFor(_idle.Id).GetChildren().OfType<FsmRow>().Select(r => r.Kind).SequenceEqual([FsmRow.Transition, FsmRow.Condition]));
        Check("every transition has a wire", Wires().SequenceEqual([$"{_idle.Id}:0>{_work.Id}", $"{_work.Id}:0>{_idle.Id}"]));
        Check("a node created in code got an id on opening", !string.IsNullOrEmpty(_action.Id));
        Check("a box has an outline of its own, upper edge included",
            _graph.BoxFor(_idle.Id).GetThemeStylebox("panel") is StyleBoxFlat { BorderWidthTop: > 0, CornerRadiusTopLeft: > 0 });
    }

    /// <summary>
    /// GraphNode draws its body below the title bar's actual height but places the rows by the
    /// bar's minimum height. The bar is unused here, so the two only agree while it stays flat.
    /// </summary>
    async System.Threading.Tasks.Task TheHeaderSitsInsideTheBox() {
        var box = _graph.BoxFor(_idle.Id);
        var bar = box.GetTitlebarHBox();
        Check("the unused title bar takes no height", bar.Size.Y == 0);

        // What a bar is left with once it has been laid out while its label still took up room.
        bar.Size = new Vector2(bar.Size.X, 23);
        box.QueueSort();
        await Settle();
        Check("and is flattened again should it ever have grown", bar.Size.Y == 0);
        Check("so the header starts below the upper edge of the body", box.GetNode<Control>("Header").Position.Y >= bar.Size.Y);

        // Zooming scales the box as drawn, so its text needs a font that survives scaling.
        var font = box.GetNode<Label>("Mode").GetThemeFont("font");
        Check("the text of a box is drawn from a distance field, so it stays sharp when zoomed",
            font is FontFile { MultichannelSignedDistanceField: true } or FontVariation { BaseFont: FontFile { MultichannelSignedDistanceField: true } });
        Check("the font the rest of the editor uses is left alone", !ReferenceEquals(font, _panel.GetThemeDefaultFont()));
    }

    async System.Threading.Tasks.Task StatesAreAddedAndNamedApart() {
        var first = _graph.AddState(new Vector2(40, 300));
        var second = _graph.AddState(new Vector2(360, 300));
        await Settle();

        Check("an added state lands in the machine", _machine.States.Contains(first) && _machine.States.Contains(second));
        Check("new states get distinct names", first.Name == "State" && second.Name == "State2");
        Check("and a box where it was put", _graph.BoxFor(first.Id)?.PositionOffset == new Vector2(40, 300));
        Check("an edit marks the machine unsaved", _panel.HasUnsavedChanges(_machine));

        _graph.DeleteStates([first.Id, second.Id]);
        await Settle();
        Check("states can be deleted again", _machine.States.Count == 2 && _graph.BoxFor(first.Id) == null);
    }

    // ---- actions -----------------------------------------------------------------------------

    async System.Threading.Tasks.Task ActionsAreAddedFromThePicker() {
        // The button in the box opens the same picker the behavior tree editor uses, narrowed to actions.
        var picker = _graph.Picker;
        var box = _graph.BoxFor(_idle.Id);
        box.GetNode<Button>("AddAction").EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();

        var offered = Offered(picker);
        Check("the '+ action' button opens the node picker for that state", picker.Visible && picker.Title == "Add action to Idle");
        Check("which lists the project's actions, in their group",
            offered.Contains(typeof(FsmProbeAction).FullName) && offered.Contains(typeof(BlackboardSetNode).FullName)
            && Groups(picker).SequenceEqual([NodeTypeRegistry.GroupAction]));
        Check("and nothing that cannot go there", offered.All(name => typeof(ActionNode).IsAssignableFrom(NodeTypeRegistry.FindByName(name).Type)));

        picker.EmitSignal(CreateNodeDialog.SignalName.TypeChosen, typeof(FsmProbeAction).FullName);
        picker.Hide();
        await Settle();

        Check("picking an action adds it to the state", _idle.Actions.Count == 1 && _idle.Actions[0] is FsmProbeAction);
        Check("with an id of its own", !string.IsNullOrEmpty(_idle.Actions[0].Id) && _idle.Actions[0].Id != _action.Id);
        Check("and a row in the box", Rows(_idle, FsmRow.Action).Count == 1 && Text(_idle, "Mode") == "sequence");

        _graph.DeleteRow(_idle.Id, FsmRow.Action, _idle.Actions[0].Id);
        await Settle();
        Check("an action can be deleted again", _idle.Actions.Count == 0 && Rows(_idle, FsmRow.Action).Count == 0);
    }

    async System.Threading.Tasks.Task ActionsAreOrderedAndDeleted() {
        var second = _graph.AddAction(_work.Id, typeof(FsmProbeAction));
        await Settle();
        Check("an added action goes below the others", _work.Actions.SequenceEqual([_action, second]));

        _graph.MoveRow(_work.Id, FsmRow.Action, second.Id, -1);
        await Settle();
        Check("an action can be moved up", _work.Actions.SequenceEqual([second, _action]));
        Check("and the rows follow", Rows(_work, FsmRow.Action)[0].Id == second.Id);

        _work.Mode = ListMode.Selector;
        _work.Parallel = true;
        _work.Repeat = true;
        _panel.OnInspectorEdited();
        await Settle();
        Check("mode, parallel and repeat show on the box", Text(_work, "Mode") == "selector · parallel · repeat");
        _work.Mode = ListMode.Sequence;
        _work.Parallel = false;
        _work.Repeat = false;

        _graph.DeleteRow(_work.Id, FsmRow.Action, second.Id);
        await Settle();
        Check("the others stay when one is deleted", _work.Actions.SequenceEqual([_action]) && Rows(_work, FsmRow.Action).Count == 1);
    }

    // ---- transitions -------------------------------------------------------------------------

    async System.Threading.Tasks.Task WiresMakeAndLeadTransitions() {
        // Port 1 of Idle is the spare one after its single transition.
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, _idle.Id, 1, _idle.Id, 0);
        await Settle();
        Check("a wire from the spare port adds a transition", _idle.Transitions.Count == 2 && _idle.Transitions[1].TargetStateId == _idle.Id);
        Check("a state can lead back to itself", Wires().Contains($"{_idle.Id}:1>{_idle.Id}"));

        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, _idle.Id, 1, _work.Id, 0);
        await Settle();
        Check("a wire from a transition's own port leads it elsewhere",
            _idle.Transitions.Count == 2 && _idle.Transitions[1].TargetStateId == _work.Id && Wires().Contains($"{_idle.Id}:1>{_work.Id}"));

        _graph.EmitSignal(GraphEdit.SignalName.ConnectionToEmpty, _work.Id, 1, new Vector2(500, 400));
        await Settle();
        var made = _machine.States.Last();
        Check("a wire dropped on empty canvas makes a state there", _machine.States.Count == 3 && made.GraphPosition != Vector2.Zero);
        Check("and leads the new transition to it", _work.Transitions.Count == 2 && _work.Transitions[1].TargetStateId == made.Id);

        _graph.DeleteStates([made.Id]);
        _graph.DeleteRow(_work.Id, FsmRow.Transition, _work.Transitions[1].Id);
        await Settle();
    }

    async System.Threading.Tasks.Task TransitionsAreOrderedAndDeleted() {
        var first = _idle.Transitions[0];
        var second = _idle.Transitions[1];

        _graph.MoveRow(_idle.Id, FsmRow.Transition, second.Id, -1);
        await Settle();
        Check("a transition can be moved up", _idle.Transitions[0] == second && _idle.Transitions[1] == first);
        Check("and the rows follow", Rows(_idle, FsmRow.Transition)[0].Id == second.Id);

        _graph.DeleteRow(_idle.Id, FsmRow.Transition, second.Id);
        await Settle();
        Check("a transition can be deleted", _idle.Transitions.Count == 1 && _idle.Transitions[0] == first
                                             && Rows(_idle, FsmRow.Transition).Count == 1);
    }

    async System.Threading.Tasks.Task ConditionsAreAddedToATransition() {
        var transition = _work.Transitions[0];

        // "Add condition…" in the row's menu opens the picker, narrowed to conditions.
        var picker = _graph.Picker;
        _graph.Call("OnRowMenu", _work.Id, FsmRow.Transition, transition.Id, Vector2.Zero);
        _graph.OnMenuIdPressed(FsmGraphEdit.MenuAddCondition);
        await Settle();
        var offered = Offered(picker);
        Check("the picker for a transition lists conditions only",
            picker.Visible && offered.Contains(typeof(FsmProbeCondition).FullName) && Groups(picker).SequenceEqual([NodeTypeRegistry.GroupCondition])
            && offered.All(name => typeof(ConditionNode).IsAssignableFrom(NodeTypeRegistry.FindByName(name).Type)));
        picker.EmitSignal(CreateNodeDialog.SignalName.TypeChosen, typeof(FsmProbeCondition).FullName);
        picker.Hide();
        await Settle();

        var first = transition.Conditions.FirstOrDefault();
        Check("a condition is added to its transition", transition.Conditions.Count == 1 && first is FsmProbeCondition && !string.IsNullOrEmpty(first.Id));
        Check("and gets a row of its own", Rows(_work, FsmRow.Condition).Count == 1 && RowText(_work, FsmRow.Condition, 0) == $"if {nameof(FsmProbeCondition)}");
        Check("the transition's row keeps its trigger", RowText(_work, FsmRow.Transition, 0) == "→ Idle  when done");
        Check("the wire stays on the transition's port", Wires().Contains($"{_work.Id}:0>{_idle.Id}"));

        var second = _graph.AddCondition(_work.Id, transition.Id, typeof(FsmProbeCondition));
        await Settle();
        Check("several conditions all have to hold", RowText(_work, FsmRow.Condition, 1) == $"and {nameof(FsmProbeCondition)}");
        transition.Mode = ListMode.Selector;
        _panel.OnInspectorEdited();
        await Settle();
        Check("or one of them, as a selector", RowText(_work, FsmRow.Condition, 1) == $"or {nameof(FsmProbeCondition)}");
        transition.Mode = ListMode.Sequence;

        _inspectorCalls = 0;
        Rows(_work, FsmRow.Condition)[1].EmitSignal(FsmRow.SignalName.Picked, FsmRow.Condition, second.Id);
        await Settle();
        Check("clicking a condition row puts that condition into the Inspector", ReferenceEquals(_lastEdited, second) && _inspectorCalls == 1);
        Check("and marks its row", _graph.BoxFor(_work.Id).PickedKind == FsmRow.Condition && _graph.BoxFor(_work.Id).PickedId == second.Id);

        _graph.MoveRow(_work.Id, FsmRow.Condition, second.Id, -1);
        await Settle();
        Check("conditions can be reordered", transition.Conditions.SequenceEqual([second, first]));

        _graph.DeleteRow(_work.Id, FsmRow.Condition, second.Id);
        _graph.DeleteRow(_work.Id, FsmRow.Condition, first.Id);
        await Settle();
        Check("and deleted again", transition.Conditions.Count == 0 && Rows(_work, FsmRow.Condition).Count == 0);
    }
    async System.Threading.Tasks.Task TheInitialStateIsMarked() {
        Check("the first state is marked as the initial one", Text(_idle, "Header/Initial") != "" && Text(_work, "Header/Initial") == "");
        _graph.SetInitial(_work.Id);
        await Settle();
        Check("another state can be made the initial one", _machine.InitialState == _work && Text(_work, "Header/Initial") != "" && Text(_idle, "Header/Initial") == "");
        _graph.SetInitial(_idle.Id);
        await Settle();
    }

    async System.Threading.Tasks.Task MovingABoxIsRemembered() {
        _graph.BoxFor(_work.Id).PositionOffset = new Vector2(500, 120);
        _graph.EmitSignal(GraphEdit.SignalName.EndNodeMove);
        await Settle();
        Check("moving a box moves the state", _work.GraphPosition == new Vector2(500, 120));
        Check("without rebuilding the graph", Wires().Count == 2);
    }

    // ---- inspector ---------------------------------------------------------------------------

    async System.Threading.Tasks.Task SelectingGoesToTheInspectorWithoutLooping() {
        _graph.Pick(_idle.Id);
        await Settle();

        _inspectorCalls = 0;
        _graph.Pick(_work.Id);
        await Settle();
        Check("selecting a box inspects its state", ReferenceEquals(_lastEdited, _work));
        Check("the inspector answering does not select again", _inspectorCalls == 1);

        _inspectorCalls = 0;
        Rows(_work, FsmRow.Action)[0].EmitSignal(FsmRow.SignalName.Picked, FsmRow.Action, _action.Id);
        await Settle();
        Check("clicking an action row inspects the action", ReferenceEquals(_lastEdited, _action) && _inspectorCalls == 1);
        Check("and keeps its state selected", _graph.BoxFor(_work.Id).Selected && _graph.BoxFor(_work.Id).PickedId == _action.Id);

        var transition = _work.Transitions[0];
        Rows(_work, FsmRow.Transition)[0].EmitSignal(FsmRow.SignalName.Picked, FsmRow.Transition, transition.Id);
        await Settle();
        Check("clicking a transition row inspects the transition", ReferenceEquals(_lastEdited, transition));

        _panel.Highlight(_idle.Transitions[0]);
        await Settle();
        Check("inspecting a transition elsewhere picks its row", _graph.BoxFor(_idle.Id).Selected
                                                                && _graph.BoxFor(_idle.Id).PickedId == _idle.Transitions[0].Id);
        _panel.Highlight(_idle.Transitions[0].Conditions[0]);
        await Settle();
        Check("inspecting a condition picks its row", _graph.BoxFor(_idle.Id).PickedKind == FsmRow.Condition
                                                     && _graph.BoxFor(_idle.Id).PickedId == _idle.Transitions[0].Conditions[0].Id);
        _panel.Highlight(_action);
        await Settle();
        Check("and inspecting an action picks its row", _graph.BoxFor(_work.Id).Selected && _graph.BoxFor(_work.Id).PickedId == _action.Id);
    }

    async System.Threading.Tasks.Task InspectorEditsReachTheGraph() {
        _work.Name = "Labour";
        _work.Transitions[0].On = FsmTrigger.Succeeded;
        _panel.OnInspectorEdited();
        await Settle();
        Check("a renamed state shows on its box", Text(_work, "Header/StateName") == "Labour");
        Check("and on the transitions that lead to it", RowText(_idle, FsmRow.Transition, 0).StartsWith("→ Labour"));
        Check("a changed trigger shows on its row", RowText(_work, FsmRow.Transition, 0) == "→ Idle  on success");

        var fresh = new FsmProbeAction();
        _work.Actions.Add(fresh);
        _panel.OnInspectorEdited();
        await Settle();
        Check("an action added in the Inspector gets a row", Rows(_work, FsmRow.Action).Count == 2);
        Check("and an id, so the blackboard can tell it apart", !string.IsNullOrEmpty(fresh.Id));

        _work.Name = "Work";
        _work.Actions.Remove(fresh);
        _panel.OnInspectorEdited();
        await Settle();

        Check("the panel knows which objects belong to the open machine",
            _panel.Owns(_machine) && _panel.Owns(_work) && _panel.Owns(_work.Transitions[0]) && _panel.Owns(_action)
            && _panel.Owns(_idle.Transitions[0].Conditions[0])
            && !_panel.Owns(new FsmState()) && !_panel.Owns(new FsmProbeAction()));
    }

    async System.Threading.Tasks.Task UndoRestoresTheStructure() {
        var before = _graph.TakeSnapshot();

        var added = _graph.AddState(new Vector2(40, 300));
        _graph.LeadTo(_work, 1, added);
        _graph.SetInitial(added.Id);
        var removed = _idle.Transitions[0];
        var condition = removed.Conditions[0];
        _graph.DeleteRow(_idle.Id, FsmRow.Condition, condition.Id);
        _graph.DeleteRow(_idle.Id, FsmRow.Transition, removed.Id);
        _graph.DeleteRow(_work.Id, FsmRow.Action, _action.Id);
        await Settle();

        _graph.RestoreSnapshot(before);
        await Settle();
        Check("undo takes an added state away again", _machine.States.SequenceEqual([_idle, _work]) && _graph.BoxFor(added.Id) == null);
        Check("and brings a deleted transition back as the same object", _idle.Transitions.Count == 1 && ReferenceEquals(_idle.Transitions[0], removed));
        Check("with the condition it had", removed.Conditions.SequenceEqual([condition]));
        Check("a deleted action comes back too", _work.Actions.SequenceEqual([_action]) && Rows(_work, FsmRow.Action).Count == 1);
        Check("with every wire where it was", Wires().SequenceEqual([$"{_idle.Id}:0>{_work.Id}", $"{_work.Id}:0>{_idle.Id}"]));
        Check("and the initial state it had", _machine.InitialState == _idle);
    }

    async System.Threading.Tasks.Task DeletingAStateLeavesItsTransitionsFlagged() {
        var before = _graph.TakeSnapshot();
        _graph.DeleteStates([_work.Id]);
        await Settle();

        var row = Rows(_idle, FsmRow.Transition)[0];
        Check("a transition to a deleted state stays", _idle.Transitions.Count == 1 && row.Text.StartsWith("→ ?"));
        Check("and warns that it leads nowhere", row.Warning.Contains("leads nowhere"));
        Check("with no wire left to draw", Wires().Count == 0);

        _graph.RestoreSnapshot(before);
        await Settle();
    }

    async System.Threading.Tasks.Task ReroutesLeadWiresAround() {
        var before = _graph.TakeSnapshot();
        var rejected = new List<string>();
        _graph.EditRejected += rejected.Add;

        // Idle's transition leads to Work; a reroute put into that wire takes its place.
        var transition = _idle.Transitions[0];
        var reroute = _graph.AddReroute(new Vector2(220, 60), _idle.Id, 0);
        await Settle();
        Check("a reroute put into a wire is where that wire now ends",
            _machine.Reroutes.SequenceEqual([reroute]) && transition.TargetStateId == reroute.Id && Wires().Contains($"{_idle.Id}:0>{reroute.Id}"));
        Check("and leads on to where the wire went", reroute.TargetId == _work.Id && Wires().Contains($"{reroute.Id}:0>{_work.Id}"));
        Check("it has a box of its own, centred on where it was put",
            _graph.RerouteBoxFor(reroute.Id)?.PositionOffset == new Vector2(220, 60) - RerouteBox.BodySize / 2);
        Check("the transition's row still names the state at the end", RowText(_idle, FsmRow.Transition, 0) == "→ Work"
                                                                         && Rows(_idle, FsmRow.Transition)[0].Warning == "");
        Check("a reroute is narrow, yet leaves room between its ports to drag it by",
            _graph.RerouteBoxFor(reroute.Id).Size.X > 2 * FsmGraphEdit.PortReach + 16 && _graph.RerouteBoxFor(reroute.Id).Size.Y < 30);

        // A second reroute into the wire that leaves the first.
        var next = _graph.AddReroute(new Vector2(300, 140), reroute.Id, 0);
        await Settle();
        Check("a reroute can be put into the wire that leaves another", reroute.TargetId == next.Id && next.TargetId == _work.Id
                                                                        && Wires().Contains($"{reroute.Id}:0>{next.Id}"));

        _graph.LeadOn(next, reroute.Id);
        await Settle();
        Check("a reroute cannot lead back to itself", next.TargetId == _work.Id && rejected.Count == 1);

        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, next.Id, 0, _idle.Id, 0);
        await Settle();
        Check("dragging its wire elsewhere takes every transition through it along",
            next.TargetId == _idle.Id && RowText(_idle, FsmRow.Transition, 0) == "→ Idle");

        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, _work.Id, 0, next.Id, 0);
        await Settle();
        Check("several wires may end at one reroute", _work.Transitions[0].TargetStateId == next.Id
                                                     && Wires().Count(w => w.EndsWith($">{next.Id}")) == 2);

        // Idle sits left of this reroute, Work right of the first one.
        var turned = _graph.RerouteBoxFor(next.Id);
        Check("a reroute that leads back to the left is turned round", turned.Flipped && !_graph.RerouteBoxFor(reroute.Id).Flipped);
        var leftEnd = turned.PositionOffset + turned.GetInputPortPosition(0);
        var rightEnd = turned.PositionOffset + turned.GetOutputPortPosition(0);
        var leaving = _graph.GetConnectionLine(rightEnd, new Vector2(40, 40));
        var arriving = _graph.GetConnectionLine(new Vector2(900, 40), leftEnd);
        Check("its wire leaves at the left end, heading left", leaving[0].IsEqualApprox(leftEnd) && leaving[1].X < leftEnd.X);
        Check("and wires arrive at its right end, from the right", arriving[^1].IsEqualApprox(rightEnd) && arriving[^2].X > rightEnd.X);
        Check("so the ends to grab are swapped too",
            _graph._IsInOutputHotzone(turned, 0, turned.Position + turned.GetInputPortPosition(0) - new Vector2(4, 0))
            && !_graph._IsInOutputHotzone(turned, 0, turned.Position + turned.GetOutputPortPosition(0) + new Vector2(4, 0))
            && _graph._IsInInputHotzone(turned, 0, turned.Position + turned.GetOutputPortPosition(0) + new Vector2(4, 0)));

        turned.PositionOffset = new Vector2(-200, 300);
        Check("moved to the other side of its target, it turns back", !turned.Flipped);

        _graph.RerouteBoxFor(next.Id).PositionOffset = new Vector2(500, 300);
        _graph.EmitSignal(GraphEdit.SignalName.EndNodeMove);
        await Settle();
        Check("moving a reroute is remembered", next.GraphPosition == new Vector2(500, 300));

        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest, new Godot.Collections.Array<StringName> { next.Id });
        await Settle();
        Check("deleting a reroute leaves the wires whole", _machine.Reroutes.SequenceEqual([reroute]) && reroute.TargetId == _idle.Id
                                                           && _work.Transitions[0].TargetStateId == _idle.Id && _graph.RerouteBoxFor(next.Id) == null);

        var loose = _graph.AddReroute(new Vector2(40, 400));
        await Settle();
        Check("a reroute added off any wire starts out loose", loose.TargetId == "" && _graph.RerouteBoxFor(loose.Id) != null);
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionToEmpty, loose.Id, 0, new Vector2(600, 400));
        await Settle();
        Check("a wire dropped on the canvas gives it a new state to lead to", _machine.FindState(loose.TargetId) != null && _machine.States.Count == 3);

        _graph.EditRejected -= rejected.Add;
        _graph.RestoreSnapshot(before);
        await Settle();
        Check("undo takes the reroutes away again", _machine.Reroutes.Count == 0 && transition.TargetStateId == _work.Id
                                                    && Wires().SequenceEqual([$"{_idle.Id}:0>{_work.Id}", $"{_work.Id}:0>{_idle.Id}"]));
    }

    // ---- blackboard --------------------------------------------------------------------------

    async System.Threading.Tasks.Task NodesLinkToTheBlackboardBesideTheGraph() {
        var blackboard = _panel.Blackboard;
        Check("the panel has a blackboard showing the machine", blackboard != null && ReferenceEquals(blackboard.Source, _machine));
        Check("which shares a split with the graph", blackboard?.GetParent() is HSplitContainer split && _graph.GetParent() == split);

        var count = blackboard.CreateEntryForParam(_action, nameof(FsmProbeAction.Counter), "count");
        await Settle();
        Check("an entry made for an action's parameter lands on the machine", count != null && _machine.Blackboard.Contains(count));
        Check("and the parameter is linked to it", _action.Counter.EntryId == count?.Id);

        blackboard.SetEntryType(count.Id, Variant.Type.String);
        await Settle();
        Check("a link that no longer fits warns on the action's row", Rows(_work, FsmRow.Action)[0].Warning.Contains("expects int"));

        blackboard.RemoveEntry(count.Id);
        blackboard.UnlinkParam(_action, nameof(FsmProbeAction.Counter));
        await Settle();
        Check("and the warning goes once the link is gone", Rows(_work, FsmRow.Action)[0].Warning == "");
    }

    /// <summary>
    /// Every Missfits addon brings a blackboard panel and an Inspector plugin of its own, and all of
    /// them see every node. A parameter must still get exactly one editor.
    /// </summary>
    async System.Threading.Tasks.Task OnlyOnePanelClaimsAParameter() {
        var other = new BlackboardPanel();
        AddChild(other);
        await Settle();

        var stranger = new FsmProbeAction();
        stranger.EnsureId();
        var mine = _panel.Blackboard;
        Check("a node of the open machine is claimed by that machine's panel only", mine.Claims(_action) && !other.Claims(_action));
        Check("a node in no open source goes to exactly one panel", mine.Claims(stranger) != other.Claims(stranger));
        Check("which is the one that offers a parameter editor",
            (BbParamEditorProperty.CreateFor(mine, _action, BbParams.HintString) != null)
            && BbParamEditorProperty.CreateFor(other, _action, BbParams.HintString) == null);
        Check("anything that is no parameter is left to the Inspector", BbParamEditorProperty.CreateFor(mine, _action, "") == null);

        other.QueueFree();
        await Settle();
    }

    // ---- saving ------------------------------------------------------------------------------

    async System.Threading.Tasks.Task SavingAndRevertingFollowTheFile() {
        const string path = "user://misstate_editor_machine.tres";
        ResourceSaver.Save(_machine, path);
        _machine.TakeOverPath(path);
        _panel.SaveUnsaved();
        Check("saving clears the unsaved mark", !_panel.HasUnsavedChanges(_machine) && _panel.UnsavedPaths().Length == 0);

        var added = _graph.AddState(new Vector2(40, 300), "Extra");
        _idle.Name = "Changed";
        _panel.OnInspectorEdited();
        await Settle();
        Check("an edit after saving marks it unsaved again", _panel.UnsavedPaths().SequenceEqual([path]));

        _panel.RevertMachine();
        await Settle();
        Check("reverting goes back to what the file holds", _machine.States.Count == 2 && _machine.FindStateByName("Extra") == null
                                                            && _machine.FindStateByName("Idle") != null);
        Check("with the actions the file holds", _machine.FindStateByName("Work")?.Actions.Count == 1);
        Check("keeps the machine resource itself", ReferenceEquals(_panel.Machine, _machine) && _graph.BoxFor(added.Id) == null);
        Check("and leaves nothing unsaved", !_panel.HasUnsavedChanges(_machine));
    }

    // ---- live debugging ----------------------------------------------------------------------

    /// <summary>
    /// The editor half of the debug channel, fed the messages a running game sends: the state the
    /// watched runner is in stands out, its actions take the colour of what they returned.
    /// </summary>
    async System.Threading.Tasks.Task TheGraphShowsWhereARunningMachineIs() {
        var router = new FsmDebugRouter();
        var path = _machine.ResourcePath;
        var idle = _machine.FindStateByName("Idle");
        var work = _machine.FindStateByName("Work");
        var picker = _panel.FindChildren("*", nameof(OptionButton), true, false).OfType<OptionButton>().First();

        Check("a runner announcing itself is accepted, in the form the editor is handed",
            router.Handle("misstate:register", [42L, path, "Enemy"], _panel) && router.Selected == 42);
        Check("and offered in the instance picker", picker.Visible && picker.ItemCount == 1 && picker.GetItemText(0) == "Enemy");

        router.Handle("misstate:state", [42L, work.Id, new[] { (byte) MissStatus.Running }, 1], _panel);
        var workBox = _graph.BoxFor(work.Id);
        var idleBox = _graph.BoxFor(idle.Id);
        Check("the state the machine is in stands out", workBox.IsCurrent && workBox.Modulate.A == 1f
                                                      && workBox.GetThemeStylebox("panel") is StyleBoxFlat outline && outline.BorderColor == FsmRow.Running);
        Check("the others fade", !idleBox.IsCurrent && idleBox.Modulate.A == FsmStateBox.DimmedAlpha);
        Check("its action shows what it last returned", workBox.Rows(FsmRow.Action).First().LiveStatus == MissStatus.Running);

        var overlay = _graph.WireOverlay;
        Check("the ways out of that state are highlighted as not taken yet",
            overlay.Wires.SequenceEqual([new LiveWire(work.Id, 0, idle.Id, Taken: false)]) && overlay.IsProcessing());
        Check("the overlay sits above the plain wires and below the boxes",
            overlay.GetIndex() == _graph.GetNode("_connection_layer").GetIndex() + 1 && overlay.GetIndex() < workBox.GetIndex());

        // The way in, through a reroute.
        var via = _graph.AddReroute(new Vector2(220, 60), idle.Id, 0);
        await Settle();
        router.Handle("misstate:state", [42L, work.Id, new[] { (byte) MissStatus.Running }, 2, idle.Transitions[0].Id], _panel);
        Check("the transition the machine came in by is highlighted as taken, reroutes included",
            overlay.Wires.Where(w => w.Taken).SequenceEqual([new LiveWire(idle.Id, 0, via.Id, true), new LiveWire(via.Id, 0, work.Id, true)])
            && overlay.Wires.Count(w => !w.Taken) == 1);
        _graph.DeleteBoxes([via.Id]);
        await Settle();
        Check("and follows an edit of the graph", overlay.Wires.Where(w => w.Taken).SequenceEqual([new LiveWire(idle.Id, 0, work.Id, true)]));
        router.Handle("misstate:state", [42L, work.Id, new[] { (byte) MissStatus.Running }, 3], _panel);

        _graph.AddState(new Vector2(40, 400), "Later");
        await Settle();
        Check("an edit that rebuilds the graph keeps the live picture",
            _graph.BoxFor(work.Id).IsCurrent && _graph.BoxFor(work.Id).Rows(FsmRow.Action).First().LiveStatus == MissStatus.Running);

        router.Handle("state", [42L, idle.Id, System.Array.Empty<byte>(), 2], _panel);
        Check("a change of state moves the highlight, in the bare form too", _graph.BoxFor(idle.Id).IsCurrent && !_graph.BoxFor(work.Id).IsCurrent);
        Check("and takes the colours off the actions of the state that was left", _graph.BoxFor(work.Id).Rows(FsmRow.Action).First().LiveStatus == null);

        router.Handle("misstate:state", [7L, work.Id, System.Array.Empty<byte>(), 1], _panel);
        Check("a runner the router was never told about makes it ask again", router.MissesRunners && _graph.BoxFor(idle.Id).IsCurrent);

        router.Handle("misstate:register", [43L, path, "Other enemy"], _panel);
        router.Handle("misstate:register", [44L, "user://another_machine.tres", "Stranger"], _panel);
        Check("further runners of the machine join the picker, runners of another one do not", picker.ItemCount == 2 && router.Selected == 42);
        router.Handle("misstate:state", [43L, work.Id, System.Array.Empty<byte>(), 1], _panel);
        router.Handle("misstate:state", [44L, work.Id, System.Array.Empty<byte>(), 1], _panel);
        Check("only the watched runner is shown", _graph.BoxFor(idle.Id).IsCurrent);

        router.Handle("misstate:unregister", [42L], _panel);
        Check("when the watched runner goes, the next one of the machine takes over", router.Selected == 43);
        Check("and the picture is cleared until it reports", !_graph.BoxFor(idle.Id).IsCurrent && _graph.BoxFor(idle.Id).Modulate.A == 1f);

        router.Handle("misstate:state", [43L, work.Id, new[] { (byte) MissStatus.Success }, 3], _panel);
        Check("which it then does", _graph.BoxFor(work.Id).IsCurrent && _graph.BoxFor(work.Id).Rows(FsmRow.Action).First().LiveStatus == MissStatus.Success);

        router.Reset(_panel);
        Check("when the game stops, the graph looks as it does while editing",
            !_graph.BoxFor(work.Id).IsCurrent && _graph.BoxFor(idle.Id).Modulate.A == 1f && !picker.Visible && overlay.Wires.Count == 0
            && _graph.BoxFor(work.Id).GetThemeStylebox("panel") is StyleBoxFlat plain && plain.BorderColor != FsmRow.Running);
    }

    // ---- node classes the editor cannot work with --------------------------------------------

    /// <summary>
    /// The editor only runs tool scripts: a node whose class lacks <c>[Tool]</c> is a mere
    /// placeholder there once its file is loaded again. It is not offered, and a file using it is
    /// not opened — with a message naming the class, instead of an exception.
    /// </summary>
    async System.Threading.Tasks.Task ANodeClassWithoutToolIsKeptOut() {
        var info = NodeTypeRegistry.Find(typeof(FsmProbeNoToolAction));
        Check("the registry knows which node classes are tool scripts",
            info is { IsTool: false } && NodeTypeRegistry.Find(typeof(FsmProbeAction)) is { IsTool: true });

        var picker = _graph.Picker;
        _graph.BoxFor(_machine.States[0].Id).GetNode<Button>("AddAction").EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        var entry = Items(Listing(picker).GetRoot()).FirstOrDefault(i => i.GetMetadata(0).AsString() == typeof(FsmProbeNoToolAction).FullName);
        Check("the picker lists such a class but does not let it be picked",
            entry != null && !entry.IsSelectable(0) && entry.GetTooltipText(0).Contains("[Tool]"));
        picker.Hide();

        const string path = "user://misstate_no_tool.tres";
        var state = new FsmState { Name = "Broken" };
        state.Actions.Add(new FsmProbeNoToolAction());
        var broken = new Fsm();
        broken.States.Add(state);
        ResourceSaver.Save(broken, path);
        broken.TakeOverPath(path);

        Check("a file using such a class is recognised", ToolScripts.MissingIn(path).SequenceEqual([nameof(FsmProbeNoToolAction)]));
        Check("a file using only tool scripts is not", ToolScripts.MissingIn(_machine.ResourcePath).Count == 0);

        var open = _panel.Machine;
        _panel.OpenMachine(broken);
        await Settle();
        Check("the panel does not open it", ReferenceEquals(_panel.Machine, open));
        Check("and says which class is missing [Tool]",
            _panel.GetChildren().OfType<Label>().Last().Text.Contains(nameof(FsmProbeNoToolAction)));
    }

    // ---- source rules ------------------------------------------------------------------------

    /// <summary>
    /// A delegate-backed signal connection dies with the assembly reload that pressing play causes.
    /// The plugin classes cannot be constructed outside the editor to check their connections, so
    /// the rule is held at the source level: no <c>+=</c> in them, and no <c>Callable.From</c>
    /// anywhere under editor/.
    /// </summary>
    void EditorSourcesDoNotWireDelegates() {
        const string editor = "res://addons/misstate/editor";
        string[] plugins = [$"{editor}/MisstateEditorPlugin.cs", $"{editor}/FsmInspectorPlugin.cs", $"{editor}/debug/MisstateDebuggerPlugin.cs"];

        var offenders = new List<string>();
        var scanned = 0;
        var files = DirAccess.GetFilesAt(editor).Select(f => $"{editor}/{f}")
            .Concat(DirAccess.GetFilesAt($"{editor}/debug").Select(f => $"{editor}/debug/{f}"));
        foreach (var path in files.Where(f => f.EndsWith(".cs"))) {
            using var source = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (source == null) continue;
            scanned++;

            var lines = source.GetAsText().Split('\n');
            for (var i = 0; i < lines.Length; i++) {
                var line = lines[i].Trim();
                if (line.StartsWith("//")) continue;
                if (line.Contains("Callable.From") || (plugins.Contains(path) && line.Contains("+="))) offenders.Add($"{path}:{i + 1}");
            }
        }
        foreach (var offender in offenders) GD.PrintErr($"      delegate wiring at {offender}");

        Check("the source check read the editor sources", scanned >= 6);
        Check("no editor source wires a signal through a delegate", offenders.Count == 0);
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>Full names of the types a picker lists, top to bottom.</summary>
    static List<string> Offered(CreateNodeDialog picker) => [.. Items(Listing(picker).GetRoot())
        .Select(item => item.GetMetadata(0).AsString()).Where(name => !string.IsNullOrEmpty(name))];

    /// <summary>The group headers a picker shows.</summary>
    static List<string> Groups(CreateNodeDialog picker) {
        var groups = new List<string>();
        for (var item = Listing(picker).GetRoot()?.GetFirstChild(); item != null; item = item.GetNext()) groups.Add(item.GetText(0));
        return groups;
    }

    static Tree Listing(CreateNodeDialog picker) => picker.FindChildren("*", nameof(Tree), true, false).OfType<Tree>().First();

    static IEnumerable<TreeItem> Items(TreeItem parent) {
        for (var item = parent?.GetFirstChild(); item != null; item = item.GetNext()) {
            yield return item;
            foreach (var nested in Items(item)) yield return nested;
        }
    }

    string Text(FsmState state, string path) => _graph.BoxFor(state.Id)?.GetNodeOrNull<Label>(path)?.Text;

    List<FsmRow> Rows(FsmState state, string kind)
        => [.. _graph.BoxFor(state.Id).Rows(kind).Where(r => !r.IsQueuedForDeletion())];

    string RowText(FsmState state, string kind, int index) => Rows(state, kind)[index].Text;

    List<string> Wires() => [.. _graph.GetConnectionList()
        .Select(c => $"{c["from_node"].AsStringName()}:{c["from_port"].AsInt32()}>{c["to_node"].AsStringName()}")
        .OrderBy(w => w.Contains($"{_work.Id}:") ? 1 : 0).ThenBy(w => w)];

    async System.Threading.Tasks.Task Settle() {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    void Check(string what, bool condition) {
        _checks++;
        if (!condition) _failures.Add(what);
    }
}
#endif
