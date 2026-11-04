#if TOOLS
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Misscore.Editor;
using Misswire.Editor;

namespace Misswire.Tests;

/// <summary>
/// Self test of the graph in the dock, inside the real editor, driven through the same signals
/// GraphEdit emits when you drag a wire. Exits with code 0 on success, 1 on failure.
/// <code>
/// godot --headless -e --path &lt;project&gt; -- --misswire-graph-test
/// </code>
/// </summary>
[Tool]
public partial class MisswireGraphSelfTest : Node {
    const string ScenePath = "res://addons/misswire/tests/editor/editor_test_scene.tscn";

    /// <summary>Set by the plugin that started the test.</summary>
    [Export]
    public GodotObject Plugin { get; set; }

    readonly List<string> _failures = [];
    readonly List<string> _rejections = [];
    int _checks;

    MisswirePanel _panel;
    MisswireGraphEdit _graph;
    Node _mediator;
    Node _model;
    EditorProbeController _controller;

    public override async void _Ready() {
        var editor = EditorInterface.Singleton;
        await Frames(10);
        while (editor.GetResourceFilesystem().IsScanning()) await Frames();

        _panel = ((MisswireEditorPlugin) Plugin).Panel;
        editor.OpenSceneFromPath(ScenePath);
        await Frames(5);
        var root = editor.GetEditedSceneRoot();
        _mediator = root.GetNode("Mediator");
        _model = root.GetNode("Model");
        _controller = root.GetNode<EditorProbeController>("Controller");
        _graph = _panel.Graph;
        _graph.EditRejected += reason => _rejections.Add(reason);

        Check("the dock shows the scene's mediator", _graph.Mediator == _mediator);

        await NodesGetABoxWithTheirMembers(root);
        await AWireFromAPropertyToAMethodMakesABinding();
        await UndoAndRedoTakeTheBindingAwayAndBack();
        await TheImplicitTriggerIsADashedWire();
        await TheSparePortAddsAStep();
        await StepsAreReorderedAndDeleted();
        await WiresAreTakenOffAndLedElsewhere();
        await AWireIntoNothingMakesABindingWithoutSteps();
        await AResourceBoxOffersTheResourcesSignals();
        await ProblemsShowAtTheRow();
        await PickingAStepShowsItInTheInspector();
        await MovingABoxIsRemembered();
        await DeletingABindingKeepsTheBoxesItUses();
        await AResourcesPropertiesAreWiredLikeANodes();
        await ANodeCanHaveMoreThanOneBox();
        await BoxesAreGrouped();
        await TheMemberWindowGroupsByClass();
        await DraggingAShownNodeInAgainAddsAnotherBox();
        await DeleteTakesThePickedStep();
        await ValuesComeFromTheBindingOrAPort();
        await AConstantCanBeANodeOfTheScene();
        await AConstantCanBeAResource();
        await AResourceBoxSaysWhatReplacesItsResource();
        await ShortcutsDuplicateAndAddMembers();
        TheGraphIsSavedWithTheScene(root);
        await Screenshot();

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"misswire graph self test: {_checks - _failures.Count}/{_checks} checks passed");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    MissBindingGraph Data => _graph.Graph;
    WireNodeBox Box(string node, string property = "") => _graph.NodeBox($"../{node}", property);

    async Task NodesGetABoxWithTheirMembers(Node root) {
        _graph.AddNodes([_model, root.GetNode("Controller"), root.GetNode("NoToolTarget")], new Vector2(40, 40));
        await Frames(3);
        var model = Box("Model");
        var controller = Box("Controller");
        Check("each node gets a box", model != null && controller != null && Box("NoToolTarget") != null);
        Check("a property can be read and set", model?.OutputPort(WireMember.Property, "Outfit") >= 0 && model.InputPort(WireMember.Property, "Outfit") >= 0);
        Check("a signal can be listened to", model?.OutputPort(WireMember.Signal, "OutfitChanged") >= 0 && model.InputPort(WireMember.Signal, "OutfitChanged") < 0);
        Check("a method can be called", controller?.InputPort(WireMember.Method, "ApplyOutfit") >= 0 && controller.OutputPort(WireMember.Method, "ApplyOutfit") < 0);
        Check("no row for the script's own category", model?.Rows().All(r => r.Id != "EditorProbeModel") == true);
        Check("a method row shows its parameters", controller?.Rows().FirstOrDefault(r => r.Id == "Note")?.Text == "Note(value: Variant)"
            && controller.Rows().FirstOrDefault(r => r.Id == "ApplyOutfit")?.Text == "ApplyOutfit()");
        Check("so does a signal row", model?.Rows().FirstOrDefault(r => r.Id == "Hit")?.Text == "» Hit(amount: int)");
        Check("a property row shows its type, quieter than its name", model?.Rows().FirstOrDefault(r => r.Id == "Health")?.Text == "Health: int"
            && model.Rows().First(r => r.Id == "Health").RichText.Contains("[color=")
            && model.Rows().First(r => r.Id == "Outfit").Text == "Outfit: EditorProbeOutfit ▸");
        Check("a box says when its node has no [Tool]", model?.Subtitle.Contains("no [Tool]") == true && controller?.Subtitle.Contains("no [Tool]") == false);
        Check("the scene now has a graph", Data is { Boxes.Count: 3 });
    }

    async Task AWireFromAPropertyToAMethodMakesABinding() {
        var applies = _controller.Applies;
        Drag(Box("Model"), WireMember.Property, "Outfit", Box("Controller"), WireMember.Method, "ApplyOutfit");
        await Frames(4);
        var binding = Data.Bindings.FirstOrDefault();
        Check("the wire makes a binding", Data.Bindings.Count == 1 && binding is { Kind: MissSourceKind.Property } && binding.Property == "Outfit");
        Check("the change signal is left to the naming convention", binding != null && binding.ChangeSignal.IsEmpty && MissWiring.ChangeSignalOf(_model, binding) == "OutfitChanged");
        Check("so the box has no warning", _graph.BindingBox(binding?.Id)?.Warning == "");
        Check("with one step calling the method", binding?.Steps.Count == 1 && binding.Steps[0] is { Kind: MissStepKind.Call } s && s.Member == "ApplyOutfit" && s.Target == "../Controller");
        Check("a property binding is active in the editor right away", binding is { EditorActive: true, PushInitial: true });
        Check("the binding has a box, wired on both sides", _graph.BindingBox(binding?.Id) != null && _graph.GetConnectionList().Count == 2);
        Check("and runs in the editor at once", _controller.Applies == applies + 1);
    }

    async Task TheImplicitTriggerIsADashedWire() {
        var binding = Data.Bindings[0];
        bool Dashed() => _graph.DashOverlay.Wires.Any(w => w.From == Box("Model").Name && w.To == _graph.BindingBox(binding.Id).Name
            && w.FromPort == Box("Model").OutputPort(WireMember.Signal, "OutfitChanged")
            && w.ToPort == _graph.BindingBox(binding.Id).InputPort(WireBindingBox.Trigger, ""));
        Check("the change signal found by its name is a dashed wire into the triggers", Dashed());
        _graph.EditBinding(binding.Id, MisswireGraphEdit.MenuFollowChanges);
        await Frames(3);
        Check("not following changes, the dashed wire is gone", !Dashed() && _graph.DashOverlay.Wires.Count == 0);
        _graph.EditBinding(binding.Id, MisswireGraphEdit.MenuFollowChanges);
        await Frames(3);
        Check("and back again", Dashed());
    }

    async Task UndoAndRedoTakeTheBindingAwayAndBack() {
        var id = Data.Bindings[0].Id;
        PressZ(shift: false);
        await Frames(4);
        Check("undo takes the binding away", Data.Bindings.Count == 0 && _graph.BindingBox(id) == null);
        PressZ(shift: true);
        await Frames(4);
        Check("redo brings it back", Data.Bindings.Count == 1 && _graph.BindingBox(id) != null);
    }

    async Task TheSparePortAddsAStep() {
        var binding = Data.Bindings[0];
        var box = _graph.BindingBox(binding.Id);
        var spare = box.OutputPort(WireBindingBox.Spare, "");
        var target = Box("Controller");
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, box.Name, spare, target.Name, target.InputPort(WireMember.Property, "Outfit"));
        await Frames(4);
        binding = Data.Bindings[0];
        Check("the spare port adds a step after the others", binding.Steps.Count == 2 && binding.Steps[1] is { Kind: MissStepKind.Set } s && s.Member == "Outfit");
        Check("each step has its own wire", _graph.GetConnectionList().Count == 3);
    }

    async Task StepsAreReorderedAndDeleted() {
        var id = Data.Bindings[0].Id;
        _graph.EditStep(id, 1, MisswireGraphEdit.MenuStepUp);
        await Frames(3);
        Check("a step moves up", Data.Bindings[0].Steps.Select(s => s.Member.ToString()).SequenceEqual(["Outfit", "ApplyOutfit"]));
        _graph.EditStep(id, 0, MisswireGraphEdit.MenuDeleteStep);
        await Frames(3);
        Check("a step is deleted", Data.Bindings[0].Steps.Count == 1 && Data.Bindings[0].Steps[0].Member == "ApplyOutfit");
    }

    /// <summary>As GraphEdit reports it: grabbing a wire's end at its input, then letting go of it somewhere.</summary>
    async Task WiresAreTakenOffAndLedElsewhere() {
        var binding = Data.Bindings[0];
        var box = _graph.BindingBox(binding.Id);
        var controller = Box("Controller");
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, box.Name, box.OutputPort(WireBindingBox.Spare, ""), controller.Name,
            controller.InputPort(WireMember.Property, "Outfit"));
        await Frames(3);
        box = _graph.BindingBox(binding.Id);
        controller = Box("Controller");
        var count = Data.Bindings.Count;

        _graph.EmitSignal(GraphEdit.SignalName.DisconnectionRequest, box.Name, 1, controller.Name, controller.InputPort(WireMember.Property, "Outfit"));
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, box.Name, 1, controller.Name, controller.InputPort(WireMember.Method, "Note"));
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionDragEnded);
        await Frames(3);
        var steps = Data.Bindings[0].Steps;
        Check("a step's wire led to another input changes the step", steps.Count == 2 && steps[1] is { Kind: MissStepKind.Call } s && s.Member == "Note");

        box = _graph.BindingBox(binding.Id);
        controller = Box("Controller");
        _graph.EmitSignal(GraphEdit.SignalName.DisconnectionRequest, box.Name, 1, controller.Name, controller.InputPort(WireMember.Method, "Note"));
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionToEmpty, box.Name, 1, new Vector2(700, 300));
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionDragEnded);
        await Frames(3);
        Check("a step's wire let go of in empty space takes the step away", Data.Bindings[0].Steps.Count == 1);

        var model = Box("Model");
        var port = model.OutputPort(WireMember.Property, "Outfit");
        box = _graph.BindingBox(binding.Id);
        _graph.EmitSignal(GraphEdit.SignalName.DisconnectionRequest, model.Name, port, box.Name, 0);
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionToEmpty, model.Name, port, new Vector2(700, 300));
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionDragEnded);
        await Frames(3);
        box = _graph.BindingBox(binding.Id);
        Check("a source's wire let go of leaves the binding without a source, and makes no new one",
            Data.Bindings.Count == count && Data.Bindings[0].Source.IsEmpty && box?.Warning.Contains("no source") == true);
        PressZ(shift: false);
        await Frames(4);
        Check("undo gives the source back", Data.Bindings[0].Source == "../Model");

        box = _graph.BindingBox(binding.Id);
        controller = Box("Controller");
        _graph.DeleteWire(box.Name, 0, controller.Name, 0);
        await Frames(3);
        Check("the wire menu deletes a step's wire", Data.Bindings[0].Steps.Count == 0);
        PressZ(shift: false);
        await Frames(4);
        Check("and undo brings it back", Data.Bindings[0].Steps.Count == 1);
    }

    async Task AWireIntoNothingMakesABindingWithoutSteps() {
        var model = Box("Model");
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionToEmpty, model.Name, model.OutputPort(WireMember.Signal, "Hit"), new Vector2(500, 400));
        await Frames(3);
        var binding = Data.Bindings.LastOrDefault();
        Check("a wire into nothing makes a binding there", Data.Bindings.Count == 2 && binding is { Kind: MissSourceKind.Signal, Steps.Count: 0 } && binding.Signal == "Hit");
        Check("a signal binding is not active in the editor", binding is { EditorActive: false });
        Check("it sits where the wire was let go", binding != null && binding.GraphPosition.IsEqualApprox((new Vector2(500, 400) + _graph.ScrollOffset) / _graph.Zoom));
    }

    async Task AResourceBoxOffersTheResourcesSignals() {
        _graph.ShowResource(new MisswireGraphEdit.BoxRef("../Model", "", ""), "Outfit");
        await Frames(3);
        var box = Box("Model", "Outfit");
        Check("a resource gets a box of its own", box != null && box.OutputPort(WireMember.Signal, "changed") >= 0);
        Check("it names the resource it holds", box?.Subtitle.StartsWith("EditorProbeOutfit") == true);
        var applies = _controller.Applies;
        Drag(box, WireMember.Signal, "changed", Box("Controller"), WireMember.Method, "ApplyOutfit");
        await Frames(4);
        var binding = Data.Bindings.LastOrDefault();
        Check("its signal makes a resource signal binding", binding is { Kind: MissSourceKind.ResourceSignal } && binding.Property == "Outfit"
            && binding.Signal == "changed" && MissWiring.ChangeSignalOf(_model, binding) == "OutfitChanged");
        Check("the new binding sends its first value in the editor", _controller.Applies == applies + 1);
    }

    async Task ProblemsShowAtTheRow() {
        Drag(Box("Model"), WireMember.Property, "Health", Box("NoToolTarget"), WireMember.Method, "Poke");
        await Frames(4);
        var box = _graph.BindingBox(Data.Bindings.Last().Id);
        Check("a binding without a change signal says what that means", box?.Warning.Contains("no change signal") == true);
        Check("a call on a node without [Tool] says it runs only in the game",
            box?.Rows().FirstOrDefault(r => r.Kind == WireBindingBox.Step)?.Warning.Contains("[Tool]") == true);
    }

    async Task PickingAStepShowsItInTheInspector() {
        var binding = Data.Bindings[0];
        var box = _graph.BindingBox(binding.Id);
        box.EmitSignal(WireBox.SignalName.RowPicked, box.Name, WireBindingBox.Step, "0");
        await Frames(2);
        Check("picking a step shows it in the Inspector", EditorInterface.Singleton.GetInspector().GetEditedObject() == binding.Steps[0]);
        Check("and the dock stays, because it owns what is inspected", _panel.Owns(binding.Steps[0]));
    }

    async Task MovingABoxIsRemembered() {
        var box = Box("Controller");
        foreach (var other in _graph.GetChildren().OfType<GraphNode>()) other.Selected = false;
        box.Selected = true;
        box.PositionOffset = new Vector2(900, 120);
        _graph.EmitSignal(GraphEdit.SignalName.EndNodeMove);
        await Frames(3);
        Check("a moved box keeps its place", Data.Boxes.FirstOrDefault(i => i.Node == "../Controller" && i.Property.IsEmpty)?.Position == new Vector2(900, 120));
        Check("and is still there after the rebuild", Box("Controller")?.PositionOffset == new Vector2(900, 120));
    }

    async Task DeletingABindingKeepsTheBoxesItUses() {
        var count = Data.Bindings.Count;
        var last = Data.Bindings.Last().Id;
        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest, new Godot.Collections.Array<StringName> { _graph.BindingBox(last).Name });
        await Frames(3);
        Check("deleting a binding box deletes the binding", Data.Bindings.Count == count - 1 && Data.Bindings.All(b => b.Id != last));

        _rejections.Clear();
        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest, new Godot.Collections.Array<StringName> { Box("Model").Name });
        await Frames(3);
        Check("a box a binding uses stays", Box("Model") != null && _rejections.Any(r => r.Contains("stays")));
    }

    async Task AResourcesPropertiesAreWiredLikeANodes() {
        var outfitBox = Box("Model", "Outfit");
        Check("a resource box offers its properties and methods too", outfitBox?.OutputPort(WireMember.Property, "Hair") >= 0
            && outfitBox.InputPort(WireMember.Property, "Hair") >= 0 && outfitBox.InputPort(WireMember.Method, "Tag") >= 0);

        Drag(outfitBox, WireMember.Property, "Hair", Box("Controller"), WireMember.Method, "Note");
        await Frames(4);
        var read = Data.Bindings.Last();
        Check("reading a resource's property is a property path", read is { Kind: MissSourceKind.Property } && read.Property == "Outfit:Hair");
        Check("its first value goes out in the editor", _controller.LastNote.AsString() == ((EditorProbeOutfit) _model.Get("Outfit").AsGodotObject()).Hair);

        var outfit = (EditorProbeOutfit) _model.Get("Outfit").AsGodotObject();
        var history = EditorInterface.Singleton.GetEditorUndoRedo();
        history.CreateAction("test hair", UndoRedo.MergeMode.Disable, outfit);
        history.AddDoProperty(outfit, "Hair", "teal");
        history.AddUndoProperty(outfit, "Hair", outfit.Hair);
        history.CommitAction();
        await Frames(4);
        Check("an edit inside the resource reaches the steps", _controller.LastNote.AsString() == "teal");

        Drag(Box("Model"), WireMember.Property, "Health", Box("Model", "Outfit"), WireMember.Method, "Tag");
        await Frames(4);
        var call = Data.Bindings.Last().Steps.FirstOrDefault();
        Check("a step into a resource targets the property path", call is { Kind: MissStepKind.Call } && call.Target == "../Model:Outfit" && call.Member == "Tag");
        Check("and calls the resource in the editor, as it has [Tool]", outfit.LastTag.AsInt32() == _model.Get("Health").AsInt32());
        Check("its box wire ends at the resource box", _graph.GetConnectionList().Any(c => c["to_node"].AsStringName() == Box("Model", "Outfit").Name));
    }

    async Task ANodeCanHaveMoreThanOneBox() {
        var main = Box("Controller");
        _graph.DuplicateBox(new MisswireGraphEdit.BoxRef("../Controller", "", ""));
        await Frames(3);
        var copy = _graph.NodeBoxes().FirstOrDefault(b => b.Path == "../Controller" && b.InfoId != "");
        var declared = WireMembers.Declared(_controller).Count;
        Check("another box of a node shows its members, as the first one does", copy != null && copy.Rows().Count() == declared);

        _graph.ShowUnused(new MisswireGraphEdit.BoxRef("../Controller", "", copy?.InfoId ?? ""), false);
        await Frames(3);
        copy = _graph.NodeBoxes().First(b => b.Path == "../Controller" && b.InfoId != "");
        Check("hiding unused members leaves a box nothing uses empty", !copy.Rows().Any());
        Check("and leaves the other box of the node alone", Box("Controller").Rows().Count() >= declared);

        _graph.AddRowToBox(new MisswireGraphEdit.BoxRef("../Controller", "", copy?.InfoId ?? ""), WireMember.Method, "Note");
        await Frames(3);
        copy = _graph.NodeBoxes().First(b => b.Path == "../Controller" && b.InfoId != "");
        Check("a member added by hand shows in that box only", copy.Rows().Count() == 1 && copy.InputPort(WireMember.Method, "Note") >= 0);

        Drag(Box("Model"), WireMember.Property, "Health", copy, WireMember.Method, "Note");
        await Frames(4);
        copy = _graph.NodeBoxes().First(b => b.Path == "../Controller" && b.InfoId != "");
        var step = Data.Bindings.Last().Steps[0];
        Check("a step remembers which box its wire ends at", step.TargetBox == copy.InfoId && step.Target == "../Controller");
        Check("and the wire ends there, not at the main box", _graph.GetConnectionList().Count(c => c["to_node"].AsStringName() == copy.Name) == 1);
        Check("the main box is still there", Box("Controller") != null);

        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest, new Godot.Collections.Array<StringName> { copy.Name });
        await Frames(3);
        Check("a box in use stays", _graph.NodeBoxes().Any(b => b.InfoId == step.TargetBox));
    }

    async Task BoxesAreGrouped() {
        var names = new List<string> { Box("Model").Name, Box("Model", "Outfit").Name };
        _graph.Group(names);
        await Frames(4);
        var group = Data.Groups.FirstOrDefault();
        var frame = group == null ? null : _graph.Frame(group.Id);
        Check("selected boxes make a group", group != null && group.Members.Count == 2 && frame != null);
        Check("the frame holds them", frame != null && _graph.GetAttachedNodesOfFrame(frame.Name).Select(n => n.ToString()).ToHashSet().SetEquals(names));
        Check("picking the frame shows the group in the Inspector", await PickFrame(frame) == group);

        // GraphEdit queues moves of a frame it was just given; a frame thrown away before then made a
        // scene reload print "Child is not a child of this node".
        _graph.RebuildGraph();
        _graph.RebuildGraph();
        await Frames(3);
        Check("a rebuild keeps the frame and hangs the boxes in it again", _graph.Frame(group?.Id ?? "") == frame
            && _graph.Frames().Count() == 1 && frame != null && _graph.GetAttachedNodesOfFrame(frame.Name).Select(n => n.ToString()).ToHashSet().SetEquals(names));

        var controller = Box("Controller").Name.ToString();
        _graph.EmitSignal(GraphEdit.SignalName.GraphElementsLinkedToFrameRequest, new Godot.Collections.Array { controller }, frame?.Name);
        await Frames(4);
        Check("a box dropped on the frame joins the group", Data.Groups[0].Members.Contains(controller));

        _graph.LeaveGroup(controller);
        await Frames(3);
        Check("a box leaves the group", !Data.Groups[0].Members.Contains(controller));

        _graph.Ungroup(Data.Groups[0].Id);
        await Frames(3);
        Check("ungrouping takes the frame away and keeps the boxes", Data.Groups.Count == 0 && !_graph.Frames().Any() && Box("Model") != null);
    }

    async Task TheMemberWindowGroupsByClass() {
        _graph.OpenMembers(Box("Controller"));
        await Frames(2);
        var dialog = _graph.Members;
        var classes = dialog.Classes();
        Check("the member window lists the classes from the box's own down to Object",
            classes.FirstOrDefault() == "EditorProbeController" && classes.LastOrDefault() == "Object" && classes.IndexOf("Node") > 0);
        Check("only the box's own class is open at first", !dialog.IsCollapsed("EditorProbeController") && dialog.IsCollapsed("Node"));
        Check("each member sits under the class that declares it", dialog.Members().Contains("method:ApplyOutfit") && dialog.Members().Contains("method:queue_free")
            && dialog.Members().Count(m => m == "method:queue_free") == 1);

        dialog.Filter("queue_f");
        Check("the search keeps only what matches, with every class open", dialog.Members().SequenceEqual(["method:queue_free"]) && !dialog.IsCollapsed("Node"));
        dialog.Choose(["method:queue_free", "method:ApplyOutfit"]);
        await Frames(3);
        var box = Box("Controller");
        Check("picked members show in the box, ones it shows already are left alone",
            box.InputPort(WireMember.Method, "queue_free") >= 0 && box.Rows().Count(r => r.Id == "ApplyOutfit") == 1);
        Check("the window is closed again", !dialog.Visible);
    }

    async Task DraggingAShownNodeInAgainAddsAnotherBox() {
        var before = _graph.NodeBoxes().Count(b => b.Path == "../Controller");
        var data = new Godot.Collections.Dictionary { { "type", "nodes" }, { "nodes", new Godot.Collections.Array { _controller.GetPath() } } };
        Check("the graph takes nodes dragged in from the scene tree", _graph._CanDropData(Vector2.Zero, data));
        _graph._DropData(new Vector2(300, 500), data);
        await Frames(3);
        var copies = _graph.NodeBoxes().Where(b => b.Path == "../Controller").ToList();
        Check("dragging in a node that has a box adds another one", copies.Count == before + 1 && copies.Count(b => b.InfoId != "") == before);
        Check("and it shows its members, like the first", copies.Last(b => b.InfoId != "").Rows().Count() >= WireMembers.Declared(_controller).Count);
    }

    async Task ValuesComeFromTheBindingOrAPort() {
        var controller = Box("Controller");
        Check("a method that needs two values is not offered", controller.InputPort(WireMember.Method, "Pair") < 0
            && controller.InputPort(WireMember.Method, "Welcome") >= 0);
        Check("one whose further values have C# defaults is, with the defaults shown",
            controller.Rows().FirstOrDefault(r => r.Id == "Greet")?.Text == "Greet(name: String, times: int = 1)");

        Drag(Box("Model"), WireMember.Property, "Health", Box("Controller"), WireMember.Method, "Greet");
        await Frames(4);
        var greet = Data.Bindings.Last().Id;
        _graph.SetValueFrom(greet, 0, MissStepValue.Port);
        var name = _graph.AddConstant(new Vector2(40, 1400));
        await Frames(3);
        _graph.SetConstant(name, "Bob");
        await Frames(3);
        var greetBox = _graph.BindingBox(greet);
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, "C" + name, 0, greetBox.Name, greetBox.InputPort(WireBindingBox.Value, "0"));
        await Frames(4);
        Check("a step into it gives the one value and no warning", _graph.BindingBox(greet).Rows().First(r => r.Kind == WireBindingBox.Step).Warning == "");
        SetProperty(_model, "Health", 90);
        await Frames(4);
        Check("and the C# default fills in the rest when it runs", _controller.Greeting == "Bob x1");
        _controller.Greeting = "";

        Drag(Box("Model"), WireMember.Property, "Health", Box("Controller"), WireMember.Method, "Note");
        await Frames(4);
        var id = Data.Bindings.Last().Id;
        WireBindingBox Binding() => _graph.BindingBox(id);
        GraphRow Row() => Binding().Rows().First(r => r.Kind == WireBindingBox.Step);
        Check("a step takes the binding's value when it fits", Row().Text.EndsWith("Controller.Note(Health)") && Row().Warning == "");
        var model = Box("Model");
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, model.Name, model.OutputPort(WireMember.Signal, "Hit"), Binding().Name, Binding().InputPort(WireBindingBox.Trigger, ""));
        await Frames(4);
        Check("a signal wired into the trigger input sets the binding off too", Data.Bindings.Last().Triggers.Contains("../Model:Hit")
            && Binding().Warning == "" && Binding().Rows().First(r => r.Kind == WireBindingBox.Trigger).Text.Contains("Model » Hit"));
        Check("the value is shown bold, as a variable", Row().RichText.EndsWith("Controller.Note([b]Health[/b])"));

        Drag(Box("Model"), WireMember.Property, "Health", Box("Controller"), WireMember.Method, "Welcome");
        await Frames(4);
        id = Data.Bindings.Last().Id;
        Check("a value of the wrong type is pointed out", Row().Warning.Contains("gets int, needs String"));
        Check("and not passed on, in the editor either", _controller.Greeting == "");

        _graph.SetValueFrom(id, 0, MissStepValue.Port);
        await Frames(3);
        Check("via port the step gets an input of its own", Binding().InputPort(WireBindingBox.Value, "0") >= 0 && Row().Warning.Contains("nothing is wired"));

        var outfit = Box("Model", "Outfit");
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, outfit.Name, outfit.OutputPort(WireMember.Property, "Hair"),
            Binding().Name, Binding().InputPort(WireBindingBox.Value, "0"));
        await Frames(4);
        var step = Data.Bindings.Last().Steps[0];
        Check("a property wired into the port is where the value comes from", step.ValueSource == "../Model:Outfit:Hair"
            && Row().Text.EndsWith("Welcome(Model.Outfit.Hair)") && Row().RichText.Contains("[b]Model.Outfit.Hair[/b]") && Row().Warning == "");
        Check("the value has its own wire", _graph.GetConnectionList().Any(c => c["to_node"].AsStringName() == Binding().Name
            && c["to_port"].AsInt32() == Binding().InputPort(WireBindingBox.Value, "0")));
        var hair = ((EditorProbeOutfit) _model.Get("Outfit").AsGodotObject()).Hair;
        Check("wiring a value sets nothing off by itself", _controller.Greeting == "");
        SetProperty(_model, "Health", 77);
        await Frames(4);
        Check("when the binding fires, the step reads the wired value", _controller.Greeting == hair);

        var constantId = _graph.AddConstant(new Vector2(40, 900));
        await Frames(3);
        WireConstantBox Constant() => _graph.GetNodeOrNull<WireConstantBox>("C" + constantId);
        Check("a constant box starts as text", Constant()?.ValueEditor is LineEdit);
        _graph.SetConstant(constantId, "Hallo");
        await Frames(3);
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, Constant().Name, 0, Binding().Name, Binding().InputPort(WireBindingBox.Value, "0"));
        await Frames(4);
        step = Data.Bindings.Last().Steps[0];
        Check("a constant wired into the port is the value", step.ValueConstant == constantId && step.ValueSource.IsEmpty
            && Row().Text.EndsWith("Welcome(\"Hallo\")") && Row().Warning == "");
        SetProperty(_model, "Health", 78);
        await Frames(4);
        Check("and is what the step passes on", _controller.Greeting == "Hallo");

        var type = Constant().GetNode<OptionButton>("Line/Type");
        type.Select(1);
        type.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        await Frames(3);
        Check("the constant's type is chosen in the box", Data.Constants.First(c => c.Id == constantId).Value.VariantType == Variant.Type.Int
            && Constant().ValueEditor is SpinBox);
        Check("and checked against the step", Row().Warning.Contains("gets Int, needs String") || Row().Warning.Contains("gets int, needs String"));

        _graph.EmitSignal(GraphEdit.SignalName.DisconnectionRequest, Constant().Name, 0, Binding().Name, Binding().InputPort(WireBindingBox.Value, "0"));
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionToEmpty, Constant().Name, 0, new Vector2(10, 10));
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionDragEnded);
        await Frames(4);
        step = Data.Bindings.Last().Steps[0];
        Check("a value wire let go of leaves the port empty", step.ValueConstant == "" && step.ValueFrom == MissStepValue.Port);

        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest, new Godot.Collections.Array<StringName> { Constant().Name });
        await Frames(3);
        Check("a constant box is deleted", Data.Constants.All(c => c.Id != constantId) && Constant() == null);
        _graph.SetValueFrom(id, 0, MissStepValue.Binding);
        await Frames(3);
        Check("back to the binding's value the port is gone", Binding().InputPort(WireBindingBox.Value, "0") < 0);
    }

    async Task AConstantCanBeANodeOfTheScene() {
        Drag(Box("Model"), WireMember.Property, "Health", Box("Controller"), WireMember.Method, "Note");
        await Frames(4);
        var id = Data.Bindings.Last().Id;
        _graph.SetValueFrom(id, 0, MissStepValue.Port);
        var constantId = _graph.AddConstant(new Vector2(40, 1200));
        await Frames(3);
        WireConstantBox Constant() => _graph.GetNodeOrNull<WireConstantBox>("C" + constantId);
        var type = Constant().GetNode<OptionButton>("Line/Type");
        var node = Enumerable.Range(0, type.ItemCount).First(i => type.GetItemText(i) == "Node");
        type.Select(node);
        type.EmitSignal(OptionButton.SignalName.ItemSelected, node);
        await Frames(3);
        Check("a constant of type Node is picked with a button", Constant().ValueEditor is Button { Text: "Pick a node" });

        _graph.PickConstantNode(constantId, "Controller");
        await Frames(3);
        Check("the picked node is kept by its path from the mediator", Data.Constants.First(c => c.Id == constantId).Value.AsNodePath() == "../Controller"
            && Constant().ValueEditor is Button { Text: "Controller" });

        var box = _graph.BindingBox(id);
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, Constant().Name, 0, box.Name, box.InputPort(WireBindingBox.Value, "0"));
        await Frames(4);
        var row = _graph.BindingBox(id).Rows().First(r => r.Kind == WireBindingBox.Step);
        Check("the step names the node", row.Text.EndsWith("Note(Controller)") && row.Warning == "");
        SetProperty(_model, "Health", 81);
        await Frames(4);
        Check("and gets the node itself when it runs", _controller.LastNote.AsGodotObject() == _controller);
    }

    async Task AConstantCanBeAResource() {
        Drag(Box("Model"), WireMember.Property, "Health", Box("Controller"), WireMember.Property, "Outfit");
        await Frames(4);
        var id = Data.Bindings.Last().Id;
        _graph.SetValueFrom(id, 0, MissStepValue.Port);
        var constantId = _graph.AddConstant(new Vector2(40, 1500));
        await Frames(3);
        WireConstantBox Constant() => _graph.GetNodeOrNull<WireConstantBox>("C" + constantId);
        GraphRow Row() => _graph.BindingBox(id).Rows().First(r => r.Kind == WireBindingBox.Step);
        var type = Constant().GetNode<OptionButton>("Line/Type");
        var resource = Enumerable.Range(0, type.ItemCount).First(i => type.GetItemText(i) == "Resource");
        type.Select(resource);
        type.EmitSignal(OptionButton.SignalName.ItemSelected, resource);
        await Frames(3);
        Check("a constant of type Resource is picked with Godot's resource field", Constant().ValueEditor is EditorResourcePicker
            && Data.Constants.First(c => c.Id == constantId).HoldsResource);

        var box = _graph.BindingBox(id);
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, Constant().Name, 0, box.Name, box.InputPort(WireBindingBox.Value, "0"));
        await Frames(4);
        Check("without a resource picked the step says so", Row().Warning.Contains("no resource yet"));

        var cape = new EditorProbeOutfit { Hair = "cape" };
        _graph.SetConstantResource(constantId, cape);
        await Frames(3);
        Check("a picked resource shows in the field and in the step", Constant().ValueEditor is EditorResourcePicker { EditedResource: EditorProbeOutfit }
            && Row().Text.EndsWith("Outfit = EditorProbeOutfit") && Row().Warning == "");
        SetProperty(_model, "Health", 95);
        await Frames(4);
        Check("and the step gets the resource itself", _controller.Outfit?.Hair == "cape");
    }

    async Task AResourceBoxSaysWhatReplacesItsResource() {
        var outfit = Box("Model", "Outfit");
        var input = outfit.InputPort(WireNodeBox.Replace, "");
        Check("a resource box takes signals that say it was replaced", input >= 0);
        Check("the change signal found by its name runs into it dashed", _graph.DashOverlay.Wires.Any(w => w.To == outfit.Name && w.ToPort == input
            && w.From == Box("Model").Name && w.FromPort == Box("Model").OutputPort(WireMember.Signal, "OutfitChanged")));

        _graph.DuplicateBox(new MisswireGraphEdit.BoxRef("../Model", "Outfit", ""));
        _graph.DuplicateBox(new MisswireGraphEdit.BoxRef("../Model", "", ""));
        await Frames(3);
        var farOutfit = _graph.NodeBoxes().Last(b => b.Path == "../Model" && b.Property == "Outfit" && b.InfoId != "");
        var farModel = _graph.NodeBoxes().Last(b => b.Path == "../Model" && b.Property == "" && b.InfoId != "");
        farOutfit.PositionOffset = new Vector2(3000, 3000);
        farModel.PositionOffset = new Vector2(2600, 3000);
        _graph.EmitSignal(GraphEdit.SignalName.EndNodeMove);
        await Frames(4);
        farOutfit = _graph.NodeBoxes().Last(b => b.Path == "../Model" && b.Property == "Outfit" && b.InfoId != "");
        farModel = _graph.NodeBoxes().Last(b => b.Path == "../Model" && b.Property == "" && b.InfoId != "");
        Check("a dashed wire leaves the nearest box of its node", _graph.DashOverlay.Wires.Any(w => w.To == farOutfit.Name && w.From == farModel.Name)
            && _graph.DashOverlay.Wires.Any(w => w.To == Box("Model", "Outfit").Name && w.From == Box("Model").Name));

        _graph.ShowResource(new MisswireGraphEdit.BoxRef("../Model", "", ""), "Extra");
        await Frames(3);
        Drag(Box("Model", "Extra"), WireMember.Signal, "changed", Box("Controller"), WireMember.Method, "ApplyOutfit");
        await Frames(4);
        Check("a resource nothing says is replaced warns", Box("Model", "Extra").Warning.Contains("nothing says when"));

        var model = Box("Model");
        var extra = Box("Model", "Extra");
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, model.Name, model.OutputPort(WireMember.Signal, "Hit"),
            extra.Name, extra.InputPort(WireNodeBox.Replace, ""));
        await Frames(4);
        extra = Box("Model", "Extra");
        Check("a signal wired in by hand says it", Data.ReplaceWatches.Any(w => w.Property == "Extra" && w.Signals.Contains("../Model:Hit"))
            && extra.Warning == ""
            && _graph.GetConnectionList().Any(c => c["to_node"].AsStringName() == extra.Name && c["to_port"].AsInt32() == extra.InputPort(WireNodeBox.Replace, "")));

        _graph.DeleteWire(Box("Model").Name, Box("Model").OutputPort(WireMember.Signal, "Hit"), extra.Name, extra.InputPort(WireNodeBox.Replace, ""));
        await Frames(3);
        Check("and is taken away like any wire", Data.ReplaceWatches.All(w => !w.Signals.Contains("../Model:Hit")) && Box("Model", "Extra").Warning != "");
    }

    async Task ShortcutsDuplicateAndAddMembers() {
        foreach (var element in _graph.GetChildren().OfType<GraphElement>()) element.Selected = false;
        var controller = Box("Controller");
        controller.Selected = true;
        _graph._GuiInput(new InputEventKey { Keycode = Key.A, CtrlPressed = true, Pressed = true });
        await Frames(2);
        Check("Ctrl+A opens the member window for the selected box", _graph.Members.Visible);
        _graph.Members.Hide();

        var binding = Data.Bindings.Last();
        var boxes = _graph.NodeBoxes().Count(b => b.Path == "../Controller");
        var rows = controller.Rows().Select(r => r.Id).ToList();
        _graph.BindingBox(binding.Id).Selected = true;
        _graph.EmitSignal(GraphEdit.SignalName.DuplicateNodesRequest);
        await Frames(4);
        var copy = _graph.NodeBoxes().LastOrDefault(b => b.Path == "../Controller" && b.InfoId != "");
        Check("Ctrl+D copies a node box with the rows it shows", _graph.NodeBoxes().Count(b => b.Path == "../Controller") == boxes + 1
            && copy != null && copy.Rows().Select(r => r.Id).ToHashSet().SetEquals(rows));
        var twin = Data.Bindings.Last();
        Check("and a binding with its steps, as a binding of its own", twin.Id != binding.Id && twin.Steps.Count == binding.Steps.Count
            && twin.Steps[0].Member == binding.Steps[0].Member && _graph.BindingBox(twin.Id) != null);
    }

    static void SetProperty(GodotObject target, string property, Variant value) {
        var history = EditorInterface.Singleton.GetEditorUndoRedo();
        history.CreateAction($"test {property}", UndoRedo.MergeMode.Disable, target);
        history.AddDoProperty(target, property, value);
        history.AddUndoProperty(target, property, target.Get(property));
        history.CommitAction();
    }

    async Task DeleteTakesThePickedStep() {
        var binding = Data.Bindings.First(b => b.Steps.Count >= 1);
        var steps = binding.Steps.Count;
        var count = Data.Bindings.Count;
        var box = _graph.BindingBox(binding.Id);
        box.EmitSignal(WireBox.SignalName.RowPicked, box.Name, WireBindingBox.Step, "0");
        await Frames(2);
        Check("clicking a step selects its box alone", _graph.BindingBox(binding.Id).Selected
            && _graph.GetChildren().OfType<GraphElement>().Count(e => e.Selected) == 1);
        _graph._GuiInput(new InputEventKey { Keycode = Key.Delete, Pressed = true });
        await Frames(3);
        Check("Delete takes the picked step, not the binding", Data.Bindings.Count == count
            && Data.Bindings.First(b => b.Id == binding.Id).Steps.Count == steps - 1);

        box = _graph.BindingBox(binding.Id);
        foreach (var element in _graph.GetChildren().OfType<GraphElement>()) element.Selected = element == box;
        _graph.EmitSignal(GraphEdit.SignalName.NodeSelected, box);
        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest, new Godot.Collections.Array<StringName> { box.Name });
        await Frames(3);
        Check("with the box itself picked, Delete takes the binding", Data.Bindings.Count == count - 1);
    }

    async Task<GodotObject> PickFrame(GraphFrame frame) {
        if (frame == null) return null;
        _graph.EmitSignal(GraphEdit.SignalName.NodeSelected, frame);
        await Frames(2);
        return EditorInterface.Singleton.GetInspector().GetEditedObject();
    }

    void TheGraphIsSavedWithTheScene(Node root) {
        var packed = new PackedScene();
        packed.Pack(root);
        var copy = packed.Instantiate(PackedScene.GenEditState.Instance);
        var graph = copy.GetNode("Mediator").Get("Graph").As<MissBindingGraph>();
        Check("the graph is saved inside the scene", graph != null && graph.Bindings.Count == Data.Bindings.Count && graph.Boxes.Count == Data.Boxes.Count);
        copy.Free();
    }

    /// <summary>With <c>--misswire-screenshot=&lt;file.png&gt;</c> and a window, saves what the dock shows.</summary>
    async Task Screenshot() {
        var arg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--misswire-screenshot="));
        if (arg == null || DisplayServer.GetName() == "headless") return;
        (_panel.GetParent() as EditorDock)?.MakeVisible();
        foreach (var box in _graph.GetChildren().OfType<GraphNode>()) box.Selected = false;
        _graph.Group([Box("Model").Name, Box("Model", "Outfit").Name]);
        var welcome = Data.Bindings.Last().Id;
        _graph.SetValueFrom(welcome, 0, MissStepValue.Port);
        var constant = _graph.AddConstant(_graph.BindingBox(welcome).PositionOffset - new Vector2(260, 0));
        await Frames(3);
        _graph.SetConstant(constant, "Hallo");
        await Frames(3);
        var welcomeBox = _graph.BindingBox(welcome);
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, "C" + constant, 0, welcomeBox.Name, welcomeBox.InputPort(WireBindingBox.Value, "0"));
        await Frames(3);
        var resourceBox = _graph.GetChildren().OfType<WireConstantBox>().FirstOrDefault(c => c.ValueEditor is EditorResourcePicker);
        _graph.ScrollOffset = ((resourceBox?.PositionOffset ?? Vector2.Zero) - new Vector2(80, 80)) * 0.7f;
        await Frames(5);
        _panel.CustomMinimumSize = new Vector2(0, 640);
        _graph.Zoom = 0.7f;
        await Frames(10);
        var rect = new Rect2I((Vector2I) _panel.GetGlobalRect().Position, (Vector2I) _panel.GetGlobalRect().Size);
        var image = GetViewport().GetTexture().GetImage().GetRegion(rect);
        var file = arg["--misswire-screenshot=".Length..];
        image.SavePng(file);

        _graph.OpenMembers(Box("Model", "Outfit"));
        await Frames(10);
        _graph.Members.GetTexture().GetImage().SavePng(file.Replace(".png", "-members.png"));
        _graph.Members.Hide();
    }

    // ---- helpers -------------------------------------------------------------------------------

    void Drag(WireNodeBox from, string fromKind, string fromName, WireNodeBox to, string toKind, string toName) =>
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, from.Name, from.OutputPort(fromKind, fromName), to.Name, to.InputPort(toKind, toName));

    static void PressZ(bool shift) {
        foreach (var pressed in new[] { true, false }) {
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Z, PhysicalKeycode = Key.Z, CtrlPressed = true, ShiftPressed = shift, Pressed = pressed });
        }
    }

    async Task Frames(int count = 1) {
        for (var i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    void Check(string name, bool ok) {
        _checks++;
        if (!ok) _failures.Add(name);
    }
}
#endif
