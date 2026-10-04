#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;
using Missbehave.Editor;
using Missbehave.Tests;
using Misscore;
using Misscore.Editor;

namespace Missbehave;

/// <summary>
/// Drives the graph editor through the very signals GraphEdit emits, headless.
/// <para>
/// This exists because the interesting failures there are not logic errors but lifetime errors:
/// rebuilding the graph straight out of a signal handler frees the boxes GraphEdit is still using
/// and takes the editor down with it, and freeing a box without removing it first makes Godot
/// rename its replacement so every wire silently disappears. Both reproduce here.
/// </para>
/// <code>
/// godot --headless --path &lt;project&gt; res://addons/missbehave/tests/editor_self_test.tscn
/// </code>
/// </summary>
public partial class BtEditorSelfTest : Node {
    readonly List<string> _failures = [];
    int _checks;

    BehaviorTreeEditorPanel _panel;
    BehaviorTreeGraphEdit _graph;
    BehaviorTree _tree;

    int _inspectorCalls;
    Resource _lastEdited;
    MissNode _root;
    MissNode _branch;
    MissNode _sub;
    MissNode _leaf;

    public override async void _Ready() {
        // The probes are hidden from the picker in the editor; the tests create them through it.
        NodeTypeRegistry.IncludeTestTypes = true;
        NodeTypeRegistry.Refresh();
        BuildTree();

        _panel = new BehaviorTreeEditorPanel();
        AddChild(_panel);

        // Stand in for the editor: whatever the panel hands to the Inspector comes straight back
        // as an _Edit call, which is exactly the round-trip that used to recurse forever.
        _panel.EditInInspector = resource => {
            _inspectorCalls++;
            _lastEdited = resource;
            if (resource is MissNode node) _panel.HighlightNode(node);
        };

        _panel.OpenTree(_tree);
        _graph = _panel.Graph;
        await Settle();

        // Runs first, before any test code connects its own (delegate-based) observers.
        NoEditorSignalIsBackedByADelegate();
        EditorSourcesDoNotWireDelegates();
        NoEditorObjectStoresAReloadUnsafeReference();

        BoxesAndWiresExist();
        await CategoriesAreVisibleAtAGlance();
        await SelectingANodeDoesNotLoop();
        await CreatingNodesKeepsTheGraphIntact();
        await MovingNodesKeepsTheWires();
        await ReparentingRewiresTheGraph();
        await ReplacingANodeKeepsItsPlace();
        await RightClickingANodeSelectsIt();
        await DeletingKeepsChildrenAsOrphans();
        DebuggerCaptureUnderstandsWhatGodotSends();
        await TheWiresATickWentThroughAreAnimated();
        await TheTreeGrowsTopToBottom();
        await TheBlackboardSitsBesideTheGraph();
        await BlackboardEntriesAreEditedInThePanel();
        await ParametersLinkToEntriesByIdNotByName();
        await AnEmptyPanelPicksUpTheRunningTree();
        await EditingAPropertyUpdatesTheGraph();
        await TheCreateDialogWorksFromTheKeyboard();
        await AListDrawsItsEntriesInsideItsBox();
        await SwitchingTreesNeitherSavesNorLosesEdits();
        await RevertingDiscardsTheUnsavedEdits();

        foreach (var failure in _failures) GD.PrintErr($"FAIL  {failure}");
        GD.Print($"missbehave editor self test: {_checks - _failures.Count}/{_checks} checks passed");
        // Collections the test left for the garbage collector must be finalized now. Left until the
        // process exits, their finalizers race Godot's own teardown and crash it after every check passed.
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    /// <summary>
    /// Root sequence with a composite branch and a leaf beside it, and one node under the branch —
    /// enough shape to reparent, to attempt a cycle, and to orphan children by deleting a parent.
    /// </summary>
    void BuildTree() {
        _sub = Composite<SelectorNode>("Sub", new Vector2(0, 360));
        _branch = Composite<SelectorNode>("Branch", new Vector2(0, 240));
        _branch.Children.Add(_sub);

        _leaf = Probe("Leaf", new Vector2(300, 240));

        _root = Composite<SequenceNode>("Root", new Vector2(150, 120));
        _root.Children.Add(_branch);
        _root.Children.Add(_leaf);

        _tree = new BehaviorTree { Root = _root, RootGraphPosition = Vector2.Zero };
    }

    /// <summary>
    /// Opening another tree must not save the one left behind, nor drop its edits; undo reaching one
    /// of those edits opens that tree again instead of applying it to the open one.
    /// </summary>
    async System.Threading.Tasks.Task SwitchingTreesNeitherSavesNorLosesEdits() {
        BehaviorTree Saved(string path) {
            var tree = new BehaviorTree { Root = Composite<SequenceNode>("Root", new Vector2(0, 100)) };
            ResourceSaver.Save(tree, path);
            tree.TakeOverPath(path);
            return tree;
        }
        const string pathA = "user://missbehave_switch_a.tres";
        const string pathB = "user://missbehave_switch_b.tres";
        var a = Saved(pathA);
        var b = Saved(pathB);

        var panel = new BehaviorTreeEditorPanel();
        AddChild(panel);
        panel.EditInInspector = _ => { };
        panel.OpenTree(a);
        await Settle();
        var graph = panel.Graph;

        var untouched = graph.TakeSnapshot();
        var added = graph.CreateNode(NodeTypeRegistry.Find(typeof(CooldownNode)), new Vector2(300, 300));
        await Settle();
        var onDisk = FileAccess.GetFileAsString(pathA);
        Check("an edit leaves the tree unsaved", panel.HasUnsavedChanges(a));

        panel.OpenTree(b);
        await Settle();
        Check("opening another tree does not save the one left behind", FileAccess.GetFileAsString(pathA) == onDisk);
        Check("the tree left behind keeps its edits and stays unsaved",
            a.Orphans.Contains(added) && panel.HasUnsavedChanges(a) && !panel.HasUnsavedChanges(b)
            && panel.UnsavedTreePaths().SequenceEqual([pathA]));

        graph.RestoreSnapshot(untouched);
        await Settle();
        Check("undoing an edit of another tree opens that tree and undoes it there",
            panel.Tree == a && !a.Orphans.Contains(added) && b.Root != null && b.Orphans.Count == 0);

        // Undo brought the content back to what is on disk; a difference shows the save really happened.
        a.RootGraphPosition = new Vector2(7, 7);
        panel.SaveUnsavedTrees();
        Check("saving the unsaved trees writes them and clears the mark",
            FileAccess.GetFileAsString(pathA).Contains("Vector2(7, 7)") && panel.UnsavedTreePaths().Length == 0);

        panel.QueueFree();
        DirAccess.RemoveAbsolute(pathA);
        DirAccess.RemoveAbsolute(pathB);
    }

    /// <summary>
    /// Revert puts the tree back to its file without swapping objects: everything else — scenes, the
    /// Inspector, undo — keeps pointing at the same tree, nodes and entries.
    /// </summary>
    async System.Threading.Tasks.Task RevertingDiscardsTheUnsavedEdits() {
        const string path = "user://missbehave_revert.tres";
        var kept = Probe("Kept", new Vector2(0, 200));
        var cooldown = (CooldownNode) Composite<CooldownNode>("Cooldown", new Vector2(200, 200));
        cooldown.WaitTime = 2;
        var root = Composite<SequenceNode>("Root", new Vector2(0, 100));
        root.Children.Add(kept);
        root.Children.Add(cooldown);
        var entry = new BlackboardEntry { Name = "Speed", VariantType = Variant.Type.Float, Default = 4f };
        var tree = new BehaviorTree { Root = root, Blackboard = [entry] };
        ResourceSaver.Save(tree, path);
        tree.TakeOverPath(path);

        var panel = new BehaviorTreeEditorPanel();
        AddChild(panel);
        panel.EditInInspector = _ => { };
        panel.OpenTree(tree);
        await Settle();
        var graph = panel.Graph;

        kept.DisplayName = "Renamed";
        cooldown.WaitTime = 9;
        entry.Name = "Pace";
        var added = graph.CreateNode(NodeTypeRegistry.Find(typeof(InverterNode)), new Vector2(400, 400));
        // Gone from the tree since saving; the file still has it.
        root.Children.Remove(cooldown);
        await Settle();
        Check("edits before reverting leave the tree unsaved", panel.HasUnsavedChanges(tree));

        panel.RevertTree();
        await Settle();
        Check("reverting keeps the tree, its nodes and entries as the same objects",
            panel.Tree == tree && tree.Root == root && root.Children.SequenceEqual<MissNode>([kept, cooldown])
            && tree.Blackboard.SequenceEqual([entry]));
        Check("reverting restores the saved values",
            kept.DisplayName == "Kept" && cooldown.WaitTime == 2 && entry.Name == "Speed");
        Check("reverting drops nodes added since saving", !tree.Orphans.Contains(added) && graph.BoxFor(added.Id) == null);
        Check("reverting shows the saved tree and clears the unsaved mark",
            graph.BoxFor(cooldown.Id) != null && !panel.HasUnsavedChanges(tree));

        panel.QueueFree();
        DirAccess.RemoveAbsolute(path);
    }

    /// <summary>
    /// A condition list is one box: its entries are rows inside it, in the order they run, with no
    /// boxes or wires of their own — and the rows light up with the running tree like boxes do.
    /// </summary>
    async System.Threading.Tasks.Task AListDrawsItsEntriesInsideItsBox() {
        static MissNode Condition(string name) {
            var condition = new BtProbeCondition { DisplayName = name };
            condition.EnsureId();
            return condition;
        }
        var aggressive = Condition("IsAggressive");
        var inRange = Condition("PlayerInRangeCheck");
        var damaged = Condition("DamageReceived");
        var action = Probe("Attack", new Vector2(400, 100));
        var selector = Composite<SelectorNode>("Aggression Check", new Vector2(0, 100));
        selector.Children.Add(aggressive);
        selector.Children.Add(inRange);
        selector.Children.Add(damaged);
        selector.Children.Add(action);

        var tree = new BehaviorTree { Root = selector };
        var panel = new BehaviorTreeEditorPanel();
        AddChild(panel);
        Resource inspected = null;
        panel.EditInInspector = resource => {
            inspected = resource;
            if (resource is MissNode node) panel.HighlightNode(node);
        };
        panel.OpenTree(tree);
        await Settle();
        var graph = panel.Graph;

        var list = graph.ReplaceNode(selector.Id, NodeTypeRegistry.Find(typeof(ConditionListNode))) as ConditionListNode;
        await Settle();
        Check("replacing a selector with a condition list folds its conditions in, in order, as a selector",
            list != null && list.Mode == ListMode.Selector && list.GetLabel() == "Aggression Check"
            && list.Children.Select(c => c.Id).SequenceEqual([aggressive.Id, inRange.Id, damaged.Id]));
        Check("what a condition list cannot hold is left on the canvas", tree.Orphans.Contains(action) && graph.BoxFor(action.Id) != null);

        var box = graph.BoxFor(list?.Id);
        Check("the entries of a list get no boxes and no wires of their own",
            box != null && graph.BoxFor(inRange.Id) == null
            && graph.GetConnectionList().All(c => c["to_node"].AsString() != inRange.Id));
        Check("a list draws one row per entry, top to bottom in running order",
            box?.EntryRowIds.SequenceEqual([aggressive.Id, inRange.Id, damaged.Id]) == true);
        var conditions = NodeTypeRegistry.InGroup(NodeTypeRegistry.GroupCondition).ToList();
        var actions = NodeTypeRegistry.InGroup(NodeTypeRegistry.GroupAction).ToList();
        Check("lists are filed with what they hold, at the top of that group",
            conditions.FirstOrDefault()?.Type == typeof(ConditionListNode) && actions.FirstOrDefault()?.Type == typeof(ActionListNode));
        NodeTypeRegistry.IncludeTestTypes = false;
        NodeTypeRegistry.Refresh();
        Check("the self tests' probe nodes are not offered in the editor", NodeTypeRegistry.Find(typeof(BtProbeCondition)) == null);
        NodeTypeRegistry.IncludeTestTypes = true;
        NodeTypeRegistry.Refresh();
        Check("lists have an icon of their own in the node picker",
            conditions[0].IconPath.EndsWith("condition_list.svg") && actions[0].IconPath.EndsWith("action_list.svg"));
        Check("on the box a condition list shows its own icon",
            box?.FindChild("Icon", true, false) is TextureRect { Texture: { } kindIcon } && kindIcon.ResourcePath.EndsWith("condition_list.svg"));
        Check("a condition list is still shaped like a condition",
            box?.GetThemeStylebox("panel") is StyleBoxFlat shape
            && shape.CornerRadiusTopLeft == GraphNodeStyles.ShapeOf(NodeTypeRegistry.GroupCondition).Radius);
        Check("a list keeps some room below its last entry",
            box?.FindChild("EntriesPad", true, false) is Control { Visible: true } pad && pad.CustomMinimumSize.Y > 0
            && pad.GetIndex() > box.FindChild("Entries", true, false).GetIndex());
        Check("a list shows the icon of its kind and of its mode",
            box?.FindChild("ModeIcon", true, false) is TextureRect { Visible: true, Texture: not null } mode
            && mode.Texture.ResourcePath.EndsWith("selector.svg"));

        // The summary is kept off the rows; there is only the name.
        inRange.Set("Negate", true);
        graph.RefreshBoxes();
        Check("an entry row shows no summary",
            box?.FindChild("Entries", true, false)?.FindChildren("*", "Label", true, false)
                .OfType<Label>().All(l => l.Text != "negated") == true);

        box!.Selected = true;
        box.PickEntry(inRange.Id);
        await Settle();
        Check("clicking a row inspects that entry", ReferenceEquals(inspected, inRange));
        box.PickEntry("");
        await Settle();
        Check("clicking the rest of the box inspects the list again", ReferenceEquals(inspected, list));

        graph.MoveEntry(list, damaged, -1);
        await Settle();
        box = graph.BoxFor(list.Id);
        Check("moving an entry up reorders the list and its rows",
            list.Children.Select(c => c.Id).SequenceEqual([aggressive.Id, damaged.Id, inRange.Id])
            && box.EntryRowIds.SequenceEqual([aggressive.Id, damaged.Id, inRange.Id]));

        graph.GetChildren().OfType<BehaviorTreeGraphNode>().ToList().ForEach(b => b.Selected = false);
        box.Selected = true;
        panel.ShowFrame([list.Id, aggressive.Id, damaged.Id, inRange.Id], [
            (byte) MissStatus.Success, (byte) MissStatus.Failure, (byte) MissStatus.Success,
            MissStatusExtensions.NotTicked,
        ]);
        Check("the debugger colours each entry row with its own status",
            box.EntryStatus(aggressive.Id) == MissStatus.Failure && box.EntryStatus(damaged.Id) == MissStatus.Success
            && box.EntryStatus(inRange.Id) == null && !box.IsDimmed);
        Check("an entry the tick did not reach fades",
            box.FindChild("Entries", true, false)?.GetNode<Control>(inRange.Id).Modulate.A < 1f);
        Check("a list's entries draw no animated wires", graph.FlowOverlay.Flows.All(f => f.To != aggressive.Id));
        panel.ClearStatuses();

        graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, list.Id, 0, action.Id, 0);
        await Settle();
        Check("a condition list refuses an action", !list.Children.Contains(action));

        graph.TakeOutOfList(list, damaged);
        await Settle();
        Check("taking an entry out of a list gives it its own box again",
            !list.Children.Contains(damaged) && tree.Orphans.Contains(damaged) && graph.BoxFor(damaged.Id) != null);

        graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, list.Id, 0, damaged.Id, 0);
        await Settle();
        Check("connecting a condition to a list appends it as the last entry",
            list.Children.LastOrDefault() == damaged && graph.BoxFor(damaged.Id) == null);

        var sequence = graph.ReplaceNode(list.Id, NodeTypeRegistry.Find(typeof(SequenceNode)));
        await Settle();
        Check("replacing a list with a composite unfolds the entries into wired boxes",
            sequence.Children.Count == 3 && sequence.Children.All(c => graph.BoxFor(c.Id) != null)
            && graph.GetConnectionList().Count(c => c["from_node"].AsString() == sequence.Id) == 3);

        panel.QueueFree();
        await Settle();
    }

    static MissNode Composite<T>(string name, Vector2 position) where T : MissNode, new() {
        var node = new T { DisplayName = name, GraphPosition = position };
        node.EnsureId();
        return node;
    }

    static MissNode Probe(string name, Vector2 position) {
        var probe = new BtProbeAction { DisplayName = name, GraphPosition = position };
        probe.EnsureId();
        return probe;
    }

    // ---- cases -------------------------------------------------------------------------------

    /// <summary>
    /// In a tree of any size, telling a condition from an action from a composite has to work without
    /// reading every title — and without colour, which is kept for the live status alone. Each
    /// category gets its own box shape, and a box is a single row: icon, then name, beside the ports.
    /// </summary>
    async System.Threading.Tasks.Task CategoriesAreVisibleAtAGlance() {
        var decoratorNode = _graph.CreateNode(NodeTypeRegistry.Find(typeof(InverterNode)), new Vector2(600, 300));
        var conditionNode = _graph.CreateNode(NodeTypeRegistry.Find(typeof(BlackboardHasNode)), new Vector2(600, 400));
        await Settle();

        var composite = Style(_root, "panel");
        var decorator = Style(decoratorNode, "panel");
        var action = Style(_leaf, "panel");
        var condition = Style(conditionNode, "panel");

        Check("every category has a styled box",
            composite != null && decorator != null && action != null && condition != null);
        if (composite == null || decorator == null || action == null || condition == null) return;

        Check("categories are not told apart by colour",
            new[] { composite.BgColor, decorator.BgColor, action.BgColor, condition.BgColor }.Distinct().Count() == 1);
        Check("conditions are round, composites slightly rounded, actions square",
            condition.CornerRadiusTopLeft > composite.CornerRadiusTopLeft
            && composite.CornerRadiusTopLeft > 0 && action.CornerRadiusTopLeft == 0);
        Check("decorators have chamfered corners", decorator.CornerDetail == 1 && decorator.CornerRadiusTopLeft > 0);
        Check("the shape covers the whole box", condition.CornerRadiusBottomLeft == condition.CornerRadiusTopLeft);

        var box = _graph.BoxFor(_root.Id);
        var row = box.GetNodeOrNull<HBoxContainer>("Row");
        var icon = row?.GetNodeOrNull<TextureRect>("Icon");
        var name = row?.GetNodeOrNull<Label>("NodeName");
        Check("icon and name share the row that carries the ports",
            row?.GetIndex() == 0 && icon?.Texture != null && name?.Text == box.Title && icon.GetIndex() < name.GetIndex());
        Check("the title bar draws nothing",
            box.GetThemeStylebox("titlebar") is StyleBoxEmpty
            && box.GetTitlebarHBox().GetChildren(includeInternal: true).OfType<Label>().All(l => !l.Visible));
        // The condition here uses the blackboard icon, which actions share, so its colour has to come
        // from the category rather than from the icon file.
        var tints = new[] {
            IconTint(_root), IconTint(decoratorNode), IconTint(_leaf), IconTint(conditionNode),
        };
        Check("icons are tinted by category, not by their file",
            tints[0] == GraphNodeStyles.IconColor(NodeTypeRegistry.GroupComposite)
            && tints[1] == GraphNodeStyles.IconColor(NodeTypeRegistry.GroupDecorator)
            && tints[2] == GraphNodeStyles.IconColor(NodeTypeRegistry.GroupAction)
            && tints[3] == GraphNodeStyles.IconColor(NodeTypeRegistry.GroupCondition));
        Check("each category has its own icon colour", tints.Distinct().Count() == 4);
        Check("while editing nothing is faded", !box.IsDimmed && box.Modulate.A == 1f);

        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest,
            new Godot.Collections.Array<StringName> { decoratorNode.Id, conditionNode.Id });
        await Settle();
        Check("category cleanup leaves the original graph", BoxCount() == 5 && ConnectionCount() == 4);
    }

    StyleBoxFlat Style(MissNode node, string name) => _graph.BoxFor(node.Id)?.GetThemeStylebox(name) as StyleBoxFlat;

    Color? IconTint(MissNode node)
        => (_graph.BoxFor(node.Id)?.GetNodeOrNull<TextureRect>("Row/Icon")?.Material as ShaderMaterial)
            ?.GetShaderParameter("tint").AsColor();

    static Color? FrameColor(GraphNode box) => (box?.GetThemeStylebox("panel") as StyleBoxFlat)?.BorderColor;

    void BoxesAndWiresExist() {
        Check("a box exists per node plus the root entry", BoxCount() == 5);
        Check("boxes keep their id as their node name", _graph.BoxFor(_leaf.Id) != null);
        Check("every parent-child pair is wired", ConnectionCount() == 4);

        // Zooming scales a box as drawn, so its text needs a font that survives scaling.
        var font = _graph.BoxFor(_leaf.Id).GetNode<Label>("Row/NodeName").GetThemeFont("font");
        Check("the text of a box is drawn from a distance field, so it stays sharp when zoomed",
            font is FontFile { MultichannelSignedDistanceField: true } or FontVariation { BaseFont: FontFile { MultichannelSignedDistanceField: true } });
    }

    /// <summary>
    /// Selecting a box shows its parameters in the Inspector, and the editor answers by editing
    /// that resource — which lands back on the panel as a selection. Without a break in that loop
    /// it recurses until the stack gives out, taking Godot with it.
    /// </summary>
    async System.Threading.Tasks.Task SelectingANodeDoesNotLoop() {
        var box = _graph.BoxFor(_leaf.Id);
        _inspectorCalls = 0;
        _lastEdited = null;

        box.Selected = true;
        _graph.EmitSignal(GraphEdit.SignalName.NodeSelected, box);
        await Settle();

        Check("selecting a node reaches the inspector", ReferenceEquals(_lastEdited, _leaf));
        Check("selecting a node settles instead of looping", _inspectorCalls is > 0 and <= 2);

        _inspectorCalls = 0;
        box.Selected = false;
        _graph.EmitSignal(GraphEdit.SignalName.NodeDeselected, box);
        await Settle();

        Check("deselecting falls back to the tree", ReferenceEquals(_lastEdited, _tree));
        Check("deselecting settles too", _inspectorCalls <= 2);
    }

    /// <summary>The create-dialog path, which is where rebuilding mid-signal used to take the editor down.</summary>
    async System.Threading.Tasks.Task CreatingNodesKeepsTheGraphIntact() {
        var inverter = NodeTypeRegistry.Find(typeof(InverterNode));
        Check("the registry finds a built-in type", inverter != null);
        if (inverter == null) return;

        var created = _graph.CreateNode(inverter, new Vector2(450, 240), _sub.Id);
        await Settle();

        Check("the new node is attached to the chosen parent", _sub.Children.Contains(created));
        Check("the new node got a box", _graph.BoxFor(created.Id) != null);
        Check("creating a node keeps the existing wires", ConnectionCount() == 5);
        Check("creating a node adds exactly one box", BoxCount() == 6);

        // Creating on empty canvas parks the node as an orphan rather than losing it.
        var loose = _graph.CreateNode(inverter, new Vector2(600, 400));
        await Settle();

        Check("a node created on empty canvas becomes an orphan", _tree.Orphans.Contains(loose));
        Check("the orphan still gets a box", _graph.BoxFor(loose.Id) != null);

        // Put things back so the later cases start from the known shape.
        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest,
            new Godot.Collections.Array<StringName> { created.Id, loose.Id });
        await Settle();

        Check("cleanup leaves the original graph", BoxCount() == 5 && ConnectionCount() == 4);
    }

    async System.Threading.Tasks.Task MovingNodesKeepsTheWires() {
        // Swapping the two children sideways is what reorders them.
        var branchBox = _graph.BoxFor(_branch.Id);
        var leafBox = _graph.BoxFor(_leaf.Id);
        (branchBox.PositionOffset, leafBox.PositionOffset) = (leafBox.PositionOffset, branchBox.PositionOffset);

        _graph.EmitSignal(GraphEdit.SignalName.EndNodeMove);
        await Settle();

        Check("moving nodes keeps every wire", ConnectionCount() == 4);
        Check("moving nodes does not rename the boxes", _graph.BoxFor(_branch.Id) != null);
        Check("left to right decides child order", ReferenceEquals(_root.Children[0], _leaf));
    }

    async System.Threading.Tasks.Task ReparentingRewiresTheGraph() {
        // Drag the leaf's input onto the branch: it should be reparented, not refused.
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, _branch.Id, 0, _leaf.Id, 0);
        await Settle();

        Check("reparenting moves the child", _branch.Children.Contains(_leaf));
        Check("reparenting detaches it from the old parent", !_root.Children.Contains(_leaf));
        Check("reparenting leaves no stale wire", ConnectionCount() == 4);
        Check("reparenting keeps all boxes", BoxCount() == 5);

        // Hanging an ancestor under its own descendant has to be refused.
        var rejected = false;
        _graph.EditRejected += _ => rejected = true;
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, _sub.Id, 0, _branch.Id, 0);
        await Settle();

        Check("a cycle is refused", rejected && !_sub.Children.Contains(_branch));

        // A leaf takes no children at all.
        rejected = false;
        _graph.EmitSignal(GraphEdit.SignalName.ConnectionRequest, _leaf.Id, 0, _sub.Id, 0);
        await Settle();

        Check("a leaf refuses children", rejected && !_leaf.Children.Contains(_sub));
    }

    /// <summary>
    /// Swapping a node's type in place — a selector for its reactive variant, say — must not cost
    /// the wiring: the replacement takes the old node's slot, children and shared parameters, and
    /// undo has to bring the original object back exactly where it was.
    /// </summary>
    async System.Threading.Tasks.Task ReplacingANodeKeepsItsPlace() {
        var reactive = NodeTypeRegistry.Find(typeof(SelectorReactiveNode));
        var inverter = NodeTypeRegistry.Find(typeof(InverterNode));
        var before = _graph.TakeSnapshot();
        var childrenBefore = _branch.Children.ToList();
        var indexBefore = _root.Children.IndexOf(_branch);

        var replacement = _graph.ReplaceNode(_branch.Id, reactive);
        await Settle();

        Check("replacing creates the requested type", replacement is SelectorReactiveNode);
        Check("the replacement takes the old node's slot",
            indexBefore >= 0 && _root.Children.IndexOf(replacement) == indexBefore && !_root.Children.Contains(_branch));
        Check("the replacement takes over all children", replacement.Children.SequenceEqual(childrenBefore));
        Check("the replacement keeps name and position",
            replacement.DisplayName == "Branch" && replacement.GraphPosition == _branch.GraphPosition);
        Check("replacing swaps the box", _graph.BoxFor(replacement.Id) != null && _graph.BoxFor(_branch.Id) == null);
        Check("replacing keeps every wire and box", ConnectionCount() == 4 && BoxCount() == 5);

        // A decorator takes one child; the rest must survive as orphans rather than vanish.
        var decorator = _graph.ReplaceNode(replacement.Id, inverter);
        await Settle();

        Check("children that no longer fit become orphans",
            decorator.Children.Count == 1 && childrenBefore.Skip(1).All(_tree.Orphans.Contains));

        _graph.RestoreSnapshot(before);
        await Settle();

        Check("undo puts the original node back", _root.Children.IndexOf(_branch) == indexBefore);
        Check("undo gives the original its children back", _branch.Children.SequenceEqual(childrenBefore));
        Check("undo leaves no orphans behind", !childrenBefore.Any(_tree.Orphans.Contains));
        Check("undo restores the original graph", BoxCount() == 5 && ConnectionCount() == 4);

        // Parameters with the same name and type carry over between otherwise unrelated types.
        var cooldown = (CooldownNode) _graph.CreateNode(NodeTypeRegistry.Find(typeof(CooldownNode)), new Vector2(700, 400));
        cooldown.WaitTime = 3.5;
        var delayer = _graph.ReplaceNode(cooldown.Id, NodeTypeRegistry.Find(typeof(DelayerNode))) as DelayerNode;
        await Settle();

        Check("shared parameters carry over", delayer != null && Mathf.IsEqualApprox(delayer.WaitTime, 3.5));
        Check("a replaced orphan stays an orphan", _tree.Orphans.Contains(delayer) && !_tree.Orphans.Contains(cooldown));

        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest, new Godot.Collections.Array<StringName> { delayer.Id });
        await Settle();
    }

    /// <summary>
    /// The context menu acts on the box that was right-clicked, so that box has to become the
    /// selection — otherwise the Inspector shows one node while the menu edits another.
    /// </summary>
    async System.Threading.Tasks.Task RightClickingANodeSelectsIt() {
        foreach (var other in _graph.GetChildren().OfType<BehaviorTreeGraphNode>()) other.Selected = false;
        await Settle();
        _lastEdited = null;

        var box = _graph.BoxFor(_sub.Id);
        box.EmitSignal(BehaviorTreeGraphNode.SignalName.ContextMenuRequested, box.Name, Vector2.Zero);
        await Settle();

        Check("right-clicking a node selects it", box.Selected);
        Check("right-clicking selects only that node",
            _graph.GetChildren().OfType<BehaviorTreeGraphNode>().Count(b => b.Selected) == 1);
        Check("right-clicking shows the node in the inspector", ReferenceEquals(_lastEdited, _sub));

        var menu = _graph.GetChildren(includeInternal: true).OfType<PopupMenu>().FirstOrDefault();
        var openIndex = menu?.GetItemIndex(BehaviorTreeGraphEdit.MenuOpenScript) ?? -1;
        Check("the context menu offers to open the script", openIndex >= 0 && !menu.IsItemDisabled(openIndex));
        menu?.Hide();

        Check("a built-in node resolves to its source file",
            BehaviorTreeGraphEdit.ScriptOf(_sub)?.ResourcePath == "res://addons/missbehave/runtime/composites/SelectorNode.cs");
        Check("a user-written leaf resolves to its source file",
            BehaviorTreeGraphEdit.ScriptOf(_leaf)?.ResourcePath.EndsWith("/BtProbeAction.cs") == true);
    }

    async System.Threading.Tasks.Task DeletingKeepsChildrenAsOrphans() {
        var names = new Godot.Collections.Array<StringName> { _branch.Id };
        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest, names);
        await Settle();

        Check("the deleted node is gone from the tree", !_root.Children.Contains(_branch));
        Check("its children survive as orphans",
            _tree.Orphans.Contains(_leaf) && _tree.Orphans.Contains(_sub));
        Check("the orphan still has a box", _graph.BoxFor(_leaf.Id) != null);
        Check("the deleted node has no box", _graph.BoxFor(_branch.Id) == null);
    }

    /// <summary>
    /// The two directions of the debugger channel disagree about the prefix: a game-side capture
    /// callback gets the bare "watch_path", but the editor side is handed the full
    /// "missbehave:register". Matching only the bare form makes every message fall through and the
    /// editor logs nothing worse than "Unknown message", so this is worth pinning down.
    /// </summary>
    void DebuggerCaptureUnderstandsWhatGodotSends() {
        var router = new MissbehaveDebugRouter();

        var register = new Godot.Collections.Array {
            42L,
            "res://missbehave demo/demo_tree.tres",
            "EnemyNear",
            new[] { _root.Id, _leaf.Id },
            new[] { -1, 0 },
            new[] { "SequenceNode", "BtProbeAction" },
        };

        Check("capture accepts the prefixed form the editor actually sends",
            router.Handle("missbehave:register", register, _panel));
        Check("capture also accepts the bare form",
            router.Handle("register", register, _panel));
        Check("capture rejects messages it does not own",
            !router.Handle("missbehave:something_else", [], _panel));
        Check("the first registered runner becomes the watched one", router.Selected == 42L);

        // A registration is proof the channel is up, so it is what triggers the reply telling the
        // game which tree to stream. Without that reply the game stays silent and the graph never
        // colours, which is indistinguishable from the debugger being broken.
        Check("a registration is recognised in both forms",
            MissbehaveDebugRouter.IsRegistration("missbehave:register")
            && MissbehaveDebugRouter.IsRegistration("register"));
        Check("other messages do not trigger the handshake",
            !MissbehaveDebugRouter.IsRegistration("missbehave:frame"));

        var frame = new Godot.Collections.Array {
            42L,
            new byte[] { (byte) MissStatus.Running, (byte) MissStatus.Success },
            1,
        };
        // The editor reloads its assembly by itself when a newer build appears, mid-game too, and the
        // router comes back empty. Frames keep arriving but name runners it never heard of; ignoring
        // them silently left the graph frozen, so they have to prompt a fresh announcement instead.
        var reloaded = new MissbehaveDebugRouter();
        Check("a frame from an unknown runner is taken", reloaded.Handle("missbehave:frame", frame, null));
        Check("a frame from an unknown runner reveals that runners were lost", reloaded.MissesRunners);
        reloaded.Handle("missbehave:register", register, null);
        Check("once the runner announces itself again, nothing is missing", !reloaded.MissesRunners);

        Check("capture accepts a status frame", router.Handle("missbehave:frame", frame, _panel));
        Check("a frame from a known runner reveals nothing missing", !router.MissesRunners);

        // A frame that arrives but paints nothing looks exactly like a frame that never arrived.
        var rootBox = _graph.BoxFor(_root.Id);
        var leafBox = _graph.BoxFor(_leaf.Id);
        Check("a streamed frame colours the running node", FrameColor(rootBox) == GraphNodeStyles.Running);
        Check("a streamed frame colours the finished node", FrameColor(leafBox) == GraphNodeStyles.Success);
        Check("a status tints the box itself, not just its edge",
            Style(_root, "panel")?.BgColor != Style(_sub, "panel")?.BgColor);

        // _sub is on the canvas but not in the running tree, so it was not reached this tick.
        var subBox = _graph.BoxFor(_sub.Id);
        Check("a node the tree did not reach fades out", subBox?.IsDimmed == true && subBox.Modulate.A < 1f);
        Check("reached nodes stay fully visible", rootBox?.Modulate.A == 1f && leafBox?.Modulate.A == 1f);

        // A paused game sends no further frames, so an edit that recreates the boxes must not wipe
        // the colours it last sent.
        _graph.RebuildGraph();
        subBox = _graph.BoxFor(_sub.Id);
        Check("rebuilding the graph keeps the last frame's colours",
            FrameColor(_graph.BoxFor(_root.Id)) == GraphNodeStyles.Running && subBox?.IsDimmed == true);

        // Two enemies on one tree, and the watched one dies: the other has to take over, otherwise
        // the game keeps filtering on a runner that is gone and nothing is coloured ever again.
        register[0] = 43L;
        register[2] = "EnemyFar";
        router.Handle("missbehave:register", register, _panel);
        Check("a second runner does not steal the selection", router.Selected == 42L);
        Check("runners coming and going make the editor re-send its selection",
            MissbehaveDebugRouter.ChangesRunners("missbehave:register")
            && MissbehaveDebugRouter.ChangesRunners("missbehave:unregister")
            && !MissbehaveDebugRouter.ChangesRunners("missbehave:frame"));

        Check("capture accepts an unregister", router.Handle("missbehave:unregister",
            new Godot.Collections.Array { 42L }, _panel));
        Check("when the watched runner goes, the next one of the tree is watched", router.Selected == 43L);
        Check("the gone runner's colours are cleared", subBox?.IsDimmed == false);

        router.Handle("missbehave:unregister", new Godot.Collections.Array { 43L }, _panel);
        Check("unregistering the last runner drops the selection", router.Selected == -1);
        Check("once nothing streams, nothing stays faded", subBox?.IsDimmed == false && subBox.Modulate.A == 1f);
    }

    /// <summary>
    /// The tree grows downwards although GraphNode only has ports on its left and right: the ports
    /// are drawn on top and at the bottom, grabbing a wire works there and not at the old spots, every
    /// wire runs vertically between them, and Arrange lays children out in a row below their parent.
    /// </summary>
    async System.Threading.Tasks.Task TheTreeGrowsTopToBottom() {
        var first = Probe("First", Vector2.Zero);
        var second = Probe("Second", Vector2.Zero);
        var sequence = Composite<SequenceNode>("Sequence", Vector2.Zero);
        sequence.Children.Add(first);
        sequence.Children.Add(second);

        var panel = new BehaviorTreeEditorPanel();
        AddChild(panel);
        panel.EditInInspector = _ => { };
        panel.OpenTree(new BehaviorTree { Root = sequence });
        await Settle();
        await Settle();

        var graph = panel.Graph;
        var rootBox = graph.GetChildren().OfType<BehaviorTreeGraphNode>().First(b => b.IsRoot);
        var parentBox = graph.BoxFor(sequence.Id);
        var leafBox = graph.BoxFor(first.Id);

        Check("the root entry has only a bottom port, a leaf only a top one, a composite both",
            !rootBox.HasInput && rootBox.HasOutput && leafBox.HasInput && !leafBox.HasOutput
            && parentBox.HasInput && parentBox.HasOutput);
        Check("ports sit centred on the top and bottom edge",
            parentBox.InputAnchor == new Vector2(parentBox.Size.X / 2, 0)
            && parentBox.OutputAnchor == new Vector2(parentBox.Size.X / 2, parentBox.Size.Y));

        // Not at zoom 1, where mixing up zoomed and unzoomed coordinates goes unnoticed — the editor
        // rarely sits at exactly 1.
        graph.Zoom = 1.5f;
        await Settle();

        Vector2 InView(BehaviorTreeGraphNode box, Vector2 local) => box.Position + local * graph.Zoom;
        // GraphEdit asks with the mouse already divided by the zoom.
        Vector2 Mouse(BehaviorTreeGraphNode box, Vector2 local) => InView(box, local) / graph.Zoom;
        Check("a wire is grabbed at the bottom port", graph._IsInOutputHotzone(parentBox, 0, Mouse(parentBox, parentBox.OutputAnchor)));
        Check("a wire is dropped at the top port", graph._IsInInputHotzone(leafBox, 0, Mouse(leafBox, leafBox.InputAnchor)));
        Check("the old side ports no longer grab anything",
            !graph._IsInOutputHotzone(parentBox, 0, Mouse(parentBox, parentBox.GetOutputPortPosition(0)))
            && !graph._IsInInputHotzone(leafBox, 0, Mouse(leafBox, leafBox.GetInputPortPosition(0))));
        Check("a leaf offers no bottom port to grab", !graph._IsInOutputHotzone(leafBox, 0, Mouse(leafBox, leafBox.OutputAnchor)));

        // GraphEdit hands over its own side-port positions; the wire has to run between the drawn ports.
        var line = graph.GetConnectionLine(
            InView(parentBox, parentBox.GetOutputPortPosition(0)), InView(leafBox, leafBox.GetInputPortPosition(0)));
        Check("a wire starts at the parent's bottom port and ends at the child's top port",
            line.Length >= 2 && line[0].DistanceTo(InView(parentBox, parentBox.OutputAnchor)) < 0.5f
                             && line[^1].DistanceTo(InView(leafBox, leafBox.InputAnchor)) < 0.5f);
        Check("a wire is drawn with right angles only",
            line.Length >= 2 && Enumerable.Range(1, line.Length - 1)
                .All(i => Mathf.IsEqualApprox(line[i].X, line[i - 1].X) || Mathf.IsEqualApprox(line[i].Y, line[i - 1].Y)));
        Check("a wire leaves and enters its ports vertically",
            line.Length >= 2 && Mathf.IsEqualApprox(line[1].X, line[0].X) && Mathf.IsEqualApprox(line[^1].X, line[^2].X));

        // Wires of one parent turn sideways at the same height, so they read as one branching bar.
        var secondLine = graph.GetConnectionLine(
            InView(parentBox, parentBox.GetOutputPortPosition(0)),
            InView(graph.BoxFor(second.Id), graph.BoxFor(second.Id).GetInputPortPosition(0)));
        Check("the wires of one parent share their sideways run",
            line.Length == 4 && secondLine.Length == 4 && Mathf.IsEqualApprox(line[1].Y, secondLine[1].Y));
        // Otherwise the wire shader is skipped and the wire being dragged is drawn far wider than the rest.
        Check("the wire being dragged is drawn with the wire shader like every other wire",
            graph.DraggedWire is { UseParentMaterial: false });
        var free = new Vector2(12345, 6789);
        Check("the loose end of a wire being dragged stays under the mouse",
            graph.GetConnectionLine(InView(parentBox, parentBox.GetOutputPortPosition(0)), free)[^1] == free);

        graph.Zoom = 1f;
        await Settle();
        graph.ArrangeNodes(record: false);
        var secondBox = graph.BoxFor(second.Id);
        Check("arranging puts children below their parent",
            leafBox.PositionOffset.Y > parentBox.PositionOffset.Y + parentBox.Size.Y
            && parentBox.PositionOffset.Y > rootBox.PositionOffset.Y + rootBox.Size.Y);
        Check("arranging puts siblings side by side, in child order, without overlap",
            leafBox.PositionOffset.Y == secondBox.PositionOffset.Y
            && secondBox.PositionOffset.X > leafBox.PositionOffset.X + leafBox.Size.X);
        var childrenCentre = (leafBox.PositionOffset.X + secondBox.PositionOffset.X + secondBox.Size.X) / 2;
        Check("arranging centres a parent over its children",
            Mathf.Abs(parentBox.PositionOffset.X + parentBox.Size.X / 2 - childrenCentre) < 1f);

        // A wire picked up at its top port — easily done by accident, the grab zone is wide — is only
        // cut when pulled well away before letting go.
        var topPort = leafBox.Position + leafBox.InputAnchor * graph.Zoom;
        // Pressed well to the side of the port, still inside its grab zone, and let go on the spot.
        var sideways = topPort + new Vector2(60, 0);
        graph.ReleaseGrabbedWire(sequence.Id, first.Id, sideways, sideways + new Vector2(5, 3));
        await Settle();
        Check("a click beside a top port leaves its wire connected", sequence.Children.Contains(first) && !panel.Tree.Orphans.Contains(first));
        graph.ReleaseGrabbedWire(sequence.Id, first.Id, sideways, sideways + new Vector2(0, 200));
        await Settle();
        Check("a wire pulled well away from its top port is cut", !sequence.Children.Contains(first) && panel.Tree.Orphans.Contains(first));

        // Boxes of different widths, as real names give them: the middle one must still be straight below.
        var boxes = new List<Control>();
        Control Box(float width) {
            var box = new Control { Size = new Vector2(width, 30) };
            boxes.Add(box);
            return box;
        }
        var layoutParent = new BtLayoutNode(Box(140));
        layoutParent.AddChild(Box(120));
        var middleChild = layoutParent.AddChild(Box(200));
        layoutParent.AddChild(Box(160));
        BtLayout.UpdatePositions(layoutParent);
        Check("with an odd number of children the middle one sits straight below its parent",
            Mathf.Abs(layoutParent.X + 70 - (middleChild.X + 100)) < 0.5f);
        foreach (var box in boxes) box.Free();

        panel.QueueFree();
    }

    /// <summary>
    /// Coloured boxes say where the tree is, coloured wires say how it got there: every wire the last
    /// tick went through takes the colour of the node it leads to, and dots move only into running nodes.
    /// </summary>
    async System.Threading.Tasks.Task TheWiresATickWentThroughAreAnimated() {
        var done = Probe("Done", new Vector2(300, 0));
        var busy = Probe("Busy", new Vector2(300, 100));
        var skipped = Probe("Skipped", new Vector2(300, 200));
        var sequence = Composite<SequenceNode>("Sequence", new Vector2(150, 100));
        sequence.Children.Add(done);
        sequence.Children.Add(busy);
        sequence.Children.Add(skipped);

        var panel = new BehaviorTreeEditorPanel();
        AddChild(panel);
        panel.EditInInspector = _ => { };
        panel.OpenTree(new BehaviorTree { Root = sequence });
        await Settle();

        var overlay = panel.Graph.FlowOverlay;
        Check("the graph has a layer for animated wires", overlay != null && overlay.MouseFilter == Control.MouseFilterEnum.Ignore);
        Check("while editing no wire is animated", overlay?.Flows.Count == 0);

        panel.ShowFrame([sequence.Id, done.Id, busy.Id, skipped.Id], [
            (byte) MissStatus.Running, (byte) MissStatus.Success, (byte) MissStatus.Running,
            MissStatusExtensions.NotTicked,
        ]);

        var flows = overlay?.Flows ?? [];
        Check("every wire the tick went through is animated, from the root down",
            flows.Count == 3
            && flows.Contains(new LiveFlow(BehaviorTreeGraphNode.RootName, sequence.Id, MissStatus.Running))
            && flows.Contains(new LiveFlow(sequence.Id, done.Id, MissStatus.Success))
            && flows.Contains(new LiveFlow(sequence.Id, busy.Id, MissStatus.Running)));
        Check("a wire to a node the tick did not reach stays still", flows.All(f => f.To != skipped.Id));

        // GraphEdit's wire layer is an ordinary first child, so anything drawn before it vanishes under
        // the plain wires. Inside the layer is no good either: it has no size, and whatever its children
        // draw is skipped whenever the layer's position is out of view, i.e. when zoomed in or scrolled.
        var layer = panel.Graph.GetChildren().FirstOrDefault(c => c.Name == BehaviorTreeGraphEdit.ConnectionLayerName);
        Check("the animated wires are drawn right after the plain ones, beneath the boxes",
            layer != null && overlay?.GetParent() == panel.Graph
            && overlay.GetIndex() == layer.GetIndex() + 1
            && overlay.GetIndex() < panel.Graph.BoxFor(sequence.Id).GetIndex());
        Check("the wire layer spans the whole graph, so the renderer never skips it",
            overlay?.Position == Vector2.Zero && overlay.Size == panel.Graph.Size);

        var phase = overlay?.Phase ?? 0f;
        await Settle();
        await Settle();
        Check("the dots move along the wires into running nodes", overlay?.Animating == true && overlay.Phase > phase);

        // Everything finished: the wires keep their colours, but nothing moves any more.
        panel.ShowFrame([sequence.Id, done.Id, busy.Id, skipped.Id], [
            (byte) MissStatus.Failure, (byte) MissStatus.Success, (byte) MissStatus.Failure,
            MissStatusExtensions.NotTicked,
        ]);
        phase = overlay?.Phase ?? 0f;
        await Settle();
        Check("wires into finished nodes are coloured but still",
            overlay?.Flows.Count == 3 && !overlay.Animating && overlay.Phase == phase);
        // Still wires move with the view all the same, so they have to keep being redrawn.
        Check("still wires keep following zoom and scrolling", overlay?.IsProcessing() == true);

        // An assembly reload keeps only what Godot can serialize. Flows in a plain C# list came back
        // empty while their last picture stayed on the canvas, frozen at the old zoom and scroll.
        Check("the flows are kept where an assembly reload preserves them",
            overlay?.Get("_from").AsStringArray().Length == 3 && overlay.Get("_status").AsInt32Array().Length == 3);

        panel.ClearStatuses();
        await Settle();
        Check("once nothing streams, the wires stop", overlay?.Flows.Count == 0 && !overlay.IsProcessing());

        // Flows vanishing behind the overlay's back, as after a reload that did lose them: it must wipe
        // its canvas before it stops redrawing, not leave the old picture standing.
        overlay?.ShowFlows(flows);
        overlay?.Set("_from", System.Array.Empty<string>());
        await Settle();
        Check("losing its flows, the overlay stops redrawing only after wiping", overlay?.IsProcessing() == false);

        panel.QueueFree();
    }

    /// <summary>
    /// Pressing play rebuilds the assembly, which reloads the plugin and replaces the panel with an
    /// empty one — right when the user wants to watch the tree run. The panel has to pick the
    /// running tree back up rather than showing an instance list over a blank canvas.
    /// </summary>
    async System.Threading.Tasks.Task AnEmptyPanelPicksUpTheRunningTree() {
        var panel = new BehaviorTreeEditorPanel();
        AddChild(panel);
        panel.EditInInspector = _ => { };
        await Settle();

        Check("a fresh panel starts with nothing open", panel.Tree == null);

        var runner = new RunnerInfo {
            Id = 7,
            TreePath = "res://missbehave demo/demo_tree.tres",
            ActorName = "EnemyNear",
            IdTable = [],
        };
        panel.OnRunnersChanged([runner], 7);
        await Settle();

        Check("an empty panel opens the tree the game is running", panel.Tree != null);
        Check("and actually draws it",
            panel.Graph.GetChildren().OfType<BehaviorTreeGraphNode>().Any());

        panel.QueueFree();
    }

    /// <summary>
    /// Pressing play reloads the C# assembly. Godot keeps the editor objects and rebuilds only their
    /// managed side, and any signal connection backed by a C# delegate — which is what every
    /// <c>Signal += Handler</c> produces, named method or lambda alike — comes back with a dead handle:
    /// "delegate_handle.value is null". Native method callables are just an object id and a method
    /// name, so they survive.
    /// <para>
    /// This walks every node of the live panel, internal children included, and fails on any
    /// connection that carries a delegate. It checks the real connections rather than the source,
    /// so it catches a delegate however it was created.
    /// </para>
    /// </summary>
    void NoEditorSignalIsBackedByADelegate() {
        // First prove the audit can see a delegate at all; otherwise "none found" means nothing.
        var probe = new Node();
        probe.Renamed += () => { };
        Check("the connection audit recognises a delegate-backed connection",
            DelegateConnections(probe, out _).Count == 1);
        probe.Free();

        var offenders = DelegateConnections(_panel, out var inspected);
        foreach (var offender in offenders) GD.PrintErr($"      delegate-backed connection: {offender}");

        // Without this the check would pass simply by finding no connections to look at.
        Check("the connection audit actually saw the editor's connections", inspected >= 15);
        Check("no editor signal connection is backed by a C# delegate", offenders.Count == 0);
    }

    /// <summary>
    /// The other half of reload safety. Godot restores the fields of scripted objects one object at a
    /// time, so a field typed as one of this assembly's own classes can be restored while its target
    /// is still a bare engine wrapper, and the generated restore code throws InvalidCastException.
    /// Editor objects therefore store such references as plain GodotObject (see ReloadSafe); this
    /// holds every editor class to that, including the plugins that cannot be constructed here.
    /// </summary>
    void NoEditorObjectStoresAReloadUnsafeReference() {
        Check("the reference audit recognises reload-unsafe members",
            ReloadUnsafeMembers(typeof(ReferenceAuditProbe)).Count == 3);

        var editorTypes = typeof(MissNode).Assembly.GetTypes()
            .Where(t => t.Namespace == "Missbehave.Editor" && typeof(GodotObject).IsAssignableFrom(t))
            .ToList();
        var offenders = editorTypes.SelectMany(ReloadUnsafeMembers).ToList();
        foreach (var offender in offenders) GD.PrintErr($"      reload-unsafe reference: {offender}");

        Check("the reference audit covered the editor classes", editorTypes.Count >= 7);
        Check("no editor object stores a reference typed as one of the addon's own classes", offenders.Count == 0);
    }

    sealed class ReferenceAuditProbe {
        public MissNode Field = null;
        public BehaviorTree Property { get; set; }
        public Godot.Collections.Array<MissNode> Serialized = [];
        public List<MissNode> NotSerialized = [];
    }

    static List<string> ReloadUnsafeMembers(System.Type type) {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                                                     | System.Reflection.BindingFlags.Public
                                                     | System.Reflection.BindingFlags.NonPublic
                                                     | System.Reflection.BindingFlags.DeclaredOnly;
        var members = new List<string>();

        // Auto-property backing fields are skipped here and reported through their property instead.
        foreach (var field in type.GetFields(flags)) {
            if (!field.Name.StartsWith('<') && IsOwnGodotType(field.FieldType)) members.Add($"{type.Name}.{field.Name}");
        }
        foreach (var property in type.GetProperties(flags)) {
            if (property.SetMethod != null && IsOwnGodotType(property.PropertyType)) members.Add($"{type.Name}.{property.Name}");
        }

        return members;
    }

    static bool IsOwnGodotType(System.Type type) {
        // Only Godot collections are serialized; a plain List or Dictionary is simply dropped on reload.
        if (type.IsGenericType) {
            return type.Namespace == "Godot.Collections" && type.GetGenericArguments().Any(IsOwnGodotType);
        }
        return typeof(GodotObject).IsAssignableFrom(type) && type.Assembly == typeof(MissNode).Assembly;
    }

    static List<string> DelegateConnections(Node root, out int inspected) {
        var offenders = new List<string>();
        inspected = 0;

        foreach (var node in SelfAndDescendants(root)) {
            foreach (var signal in node.GetSignalList()) {
                var name = signal["name"].AsStringName();
                foreach (var connection in node.GetSignalConnectionList(name)) {
                    inspected++;
                    var callable = connection["callable"].AsCallable();
                    if (callable.Delegate != null) offenders.Add($"{node.Name}.{name} -> {callable.Delegate.Method.Name}");
                }
            }
        }

        return offenders;
    }

    static IEnumerable<Node> SelfAndDescendants(Node root) {
        yield return root;
        foreach (var child in root.GetChildren(includeInternal: true)) {
            foreach (var node in SelfAndDescendants(child)) yield return node;
        }
    }

    /// <summary>
    /// The connection audit above cannot reach the plugin classes or the Inspector's parameter editor — none of them can be
    /// constructed outside the editor — so for exactly those files the rule is held at the source
    /// level instead: no <c>+=</c> at all, since nothing in them does arithmetic. Everywhere under
    /// editor/, <c>Callable.From</c> is off limits too, as it is the other way to build a delegate
    /// callable.
    /// </summary>
    void EditorSourcesDoNotWireDelegates() {
        const string editor = "res://addons/missbehave/editor";
        // The blackboard's editor lives in the core, and is held to the same rule.
        const string coreEditor = "res://addons/misscore/editor";
        string[] unauditable = [
            $"{editor}/MissbehaveEditorPlugin.cs",
            $"{editor}/BehaviorTreeInspectorPlugin.cs",
            $"{editor}/debug/MissbehaveDebuggerPlugin.cs",
            $"{coreEditor}/blackboard/BbParamEditorProperty.cs",
        ];

        var offenders = new List<string>();
        var plusEquals = new System.Text.RegularExpressions.Regex(@"\+=");
        var callableFrom = new System.Text.RegularExpressions.Regex(@"Callable\.From\s*\(");
        var scanned = 0;
        var pluginsScanned = 0;

        foreach (var path in CsFilesUnder(editor).Concat(CsFilesUnder(coreEditor))) {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null) continue;
            scanned++;

            var isPlugin = System.Array.IndexOf(unauditable, path) >= 0;
            if (isPlugin) pluginsScanned++;

            var lines = file.GetAsText().Split('\n');
            for (var i = 0; i < lines.Length; i++) {
                var line = lines[i].Trim();
                if (line.StartsWith("//")) continue;

                if (callableFrom.IsMatch(line) || (isPlugin && plusEquals.IsMatch(line))) {
                    offenders.Add($"{path}:{i + 1}: {line}");
                }
            }
        }

        foreach (var offender in offenders) GD.PrintErr($"      delegate wiring at {offender}");

        Check("the source check actually read the editor sources", scanned >= 5);
        Check("the source check found every unauditable file", pluginsScanned == unauditable.Length);
        Check("no editor source wires a signal through a delegate", offenders.Count == 0);
    }

    static IEnumerable<string> CsFilesUnder(string directory) {
        foreach (var file in DirAccess.GetFilesAt(directory)) {
            if (file.EndsWith(".cs")) yield return $"{directory}/{file}";
        }
        foreach (var sub in DirAccess.GetDirectoriesAt(directory)) {
            foreach (var file in CsFilesUnder($"{directory}/{sub}")) yield return file;
        }
    }

    // ---- blackboard --------------------------------------------------------------------------

    async System.Threading.Tasks.Task TheBlackboardSitsBesideTheGraph() {
        var blackboard = _panel.Blackboard;
        Check("the panel has a blackboard", blackboard != null && blackboard.Source == _tree);
        Check("the blackboard shares a split with the graph",
            blackboard?.GetParent() is HSplitContainer split && _graph.GetParent() == split);
        Check("the blackboard is shown by default", blackboard?.IsVisibleInTree() == true);

        var toggle = FindButton(_panel, "Blackboard");
        toggle?.EmitSignal(BaseButton.SignalName.Toggled, false);
        await Settle();
        Check("the toolbar hides the blackboard", toggle != null && !blackboard.Visible);
        toggle?.EmitSignal(BaseButton.SignalName.Toggled, true);
        Check("the toolbar shows it again", blackboard.Visible);
    }

    async System.Threading.Tasks.Task BlackboardEntriesAreEditedInThePanel() {
        var blackboard = _panel.Blackboard;
        var edits = 0;
        var rejections = new List<string>();
        blackboard.BlackboardEdited += () => edits++;
        blackboard.EditRejected += reason => rejections.Add(reason);

        var speed = blackboard.AddEntry(Variant.Type.Float);
        var second = blackboard.AddEntry(Variant.Type.Float);
        await Settle();

        Check("added entries land on the tree", _tree.Blackboard.Count == 2 && _tree.Blackboard[0] == speed);
        Check("new entries get distinct names", speed.Name == "Float" && second.Name == "Float2");
        Check("a new entry starts at its type's zero", speed.Default.VariantType == Variant.Type.Float && speed.Default.AsDouble() == 0);
        Check("every entry has a row", RowCount() == 2);
        Check("an edit is reported, so the tree gets saved", edits == 2);

        Check("an entry can be renamed", blackboard.RenameEntry(speed.Id, "Speed") && speed.Name == "Speed");
        Check("a taken name is refused", !blackboard.RenameEntry(second.Id, "Speed") && second.Name == "Float2");
        Check("a name with a slash is refused", !blackboard.RenameEntry(second.Id, "a/b") && rejections.Count == 2);

        blackboard.SetEntryDefault(speed.Id, 3.5);
        Check("the default is set", speed.Default.AsDouble() == 3.5);

        var before = blackboard.TakeSnapshot();
        blackboard.SetEntryType(second.Id, Variant.Type.String);
        Check("changing the type resets a default that no longer fits",
            second.VariantType == Variant.Type.String && second.Default.VariantType == Variant.Type.String);
        blackboard.RemoveEntry(second.Id);
        await Settle();
        Check("an entry can be removed", _tree.Blackboard.Count == 1 && RowCount() == 1);

        blackboard.RestoreSnapshot(before);
        await Settle();
        Check("undo brings back the removed entry as it was",
            _tree.Blackboard.Count == 2 && _tree.Blackboard[1] == second && second.VariantType == Variant.Type.Float
            && RowCount() == 2);
    }

    /// <summary>
    /// The point of ids: a parameter keeps its entry through a rename, shows the new name straight
    /// away, and a removed or mistyped entry shows up as a warning on the box instead of failing
    /// silently at runtime.
    /// </summary>
    async System.Threading.Tasks.Task ParametersLinkToEntriesByIdNotByName() {
        var blackboard = _panel.Blackboard;
        var setter = (BlackboardSetNode) _graph.CreateNode(NodeTypeRegistry.Find(typeof(BlackboardSetNode)), new Vector2(600, 500));
        var probe = (BtProbeParamAction) _graph.CreateNode(NodeTypeRegistry.Find(typeof(BtProbeParamAction)), new Vector2(600, 600));
        await Settle();

        var tempo = blackboard.CreateEntryForParam(probe, nameof(BtProbeParamAction.Speed), "Tempo");
        Check("a new entry for a parameter takes its type and value",
            tempo?.VariantType == Variant.Type.Float && tempo.Default.AsDouble() == 4.0);
        Check("the parameter is linked to the new entry", probe.Speed.EntryId == tempo?.Id && probe.Speed.EntryName == "Tempo");

        blackboard.LinkParam(setter, nameof(BlackboardSetNode.Target), tempo.Id);
        setter.Value.Literal = 1;
        _graph.RefreshBoxes();
        await Settle();
        Check("a box names the linked entry", Summary(setter) == "Tempo = 1");

        var beforeRename = blackboard.TakeSnapshot();
        blackboard.RenameEntry(tempo.Id, "Pace");
        await Settle();
        Check("renaming an entry keeps the link", probe.Speed.EntryId == tempo.Id && setter.Target.EntryId == tempo.Id);
        Check("renaming an entry updates every linked name", probe.Speed.EntryName == "Pace" && Summary(setter) == "Pace = 1");

        blackboard.RestoreSnapshot(beforeRename);
        await Settle();
        Check("undoing a rename restores the linked names too", tempo.Name == "Tempo" && Summary(setter) == "Tempo = 1");

        // The probe's node parameter is never linked here, so its box always carries that one warning.
        Check("a linked parameter does not warn", !WarningOf(probe).Contains("Speed"));
        blackboard.SetEntryType(tempo.Id, Variant.Type.String);
        await Settle();
        Check("an entry of the wrong type warns on the box", WarningOf(probe).Contains("expects float"));

        blackboard.RemoveEntry(tempo.Id);
        await Settle();
        Check("a removed entry warns on the box", WarningOf(probe).Contains("no longer exists"));

        blackboard.UnlinkParam(probe, nameof(BtProbeParamAction.Speed));
        await Settle();
        Check("unlinking falls back to the fixed value", !probe.Speed.IsLinked && !WarningOf(probe).Contains("Speed"));

        blackboard.RestoreSnapshot(beforeRename);
        var has = (BlackboardHasNode) _graph.ReplaceNode(setter.Id, NodeTypeRegistry.Find(typeof(BlackboardHasNode)));
        await Settle();
        blackboard.LinkParam(has, nameof(BlackboardHasNode.Entry), tempo.Id);
        var erase = (BlackboardEraseNode) _graph.ReplaceNode(has.Id, NodeTypeRegistry.Find(typeof(BlackboardEraseNode)));
        await Settle();
        Check("replacing a node carries a parameter of the same name", erase.Entry.EntryId == tempo.Id);

        _graph.EmitSignal(GraphEdit.SignalName.DeleteNodesRequest, new Godot.Collections.Array<StringName> { probe.Id, erase.Id });
        await Settle();
    }

    int RowCount() => _panel.Blackboard.FindChild("Rows", owned: false).GetChildren().OfType<BlackboardEntryRow>().Count(r => !r.IsQueuedForDeletion());

    string Summary(MissNode node) => _graph.BoxFor(node.Id)?.GetNodeOrNull<Label>("Summary")?.Text;

    string WarningOf(MissNode node) => _graph.BoxFor(node.Id)?.GetNodeOrNull<Label>("Row/Warning")?.TooltipText ?? "?";

    static Button FindButton(Node root, string text)
        => SelfAndDescendants(root).OfType<Button>().FirstOrDefault(b => b.Text == text);

    /// <summary>
    /// Renaming a node has to show up on its box straight away. A custom Resource stays silent when
    /// the Inspector writes to it, so the panel listens to the Inspector instead; this covers the
    /// refresh path behind that, which is what actually redraws the box.
    /// </summary>
    async System.Threading.Tasks.Task EditingAPropertyUpdatesTheGraph() {
        var box = _graph.BoxFor(_root.Id);
        Check("the box starts with the authored name", box.Title == "Root");

        _root.DisplayName = "Renamed root";
        _panel.OnInspectorPropertyEdited("DisplayName");
        await Settle();

        Check("editing a name updates the box title", box.Title == "Renamed root");

        // A summary is derived from a leaf's own exports, so it has to refresh the same way.
        var leafBox = _graph.BoxFor(_sub.Id);
        ((SelectorNode) _sub).DisplayName = "Renamed sub";
        _panel.OnInspectorPropertyEdited("DisplayName");
        await Settle();

        Check("editing a child's name updates its box too", leafBox.Title == "Renamed sub");
    }

    /// <summary>Type, Enter — or type, arrow down, arrows, Enter — without touching the mouse.</summary>
    async System.Threading.Tasks.Task TheCreateDialogWorksFromTheKeyboard() {
        var dialog = _graph.GetChildren(includeInternal: true).OfType<CreateNodeDialog>().First();
        var filter = FindChildOfType<LineEdit>(dialog);
        var list = FindChildOfType<Tree>(dialog);
        var chosen = new List<string>();
        dialog.TypeChosen += name => chosen.Add(name);

        dialog.Open(Vector2.Zero);
        await Settle();
        filter.Text = "sequence";
        filter.EmitSignal(LineEdit.SignalName.TextChanged, filter.Text);
        list.DeselectAll();
        dialog.OnFilterSubmitted(filter.Text);
        await Settle();
        Check("Enter in the filter takes the first match", chosen.Count == 1 && chosen[0] == typeof(SequenceNode).FullName);

        dialog.Open(Vector2.Zero);
        await Settle();
        filter.Text = "sequence";
        filter.EmitSignal(LineEdit.SignalName.TextChanged, filter.Text);
        list.DeselectAll();
        dialog.OnFilterGuiInput(new InputEventKey { Pressed = true, Keycode = Key.Down });
        Check("arrow down moves from the filter onto the first type",
            list.HasFocus() && list.GetSelected()?.GetMetadata(0).AsString() == typeof(SequenceNode).FullName);

        dialog.OnTreeGuiInput(new InputEventKey { Pressed = true, Keycode = Key.Up });
        Check("arrow up on the first type goes back to the filter", filter.HasFocus());
        dialog.Hide();

        // [NodeGroup] files a type in folders below its category, [NodeName] renames it.
        dialog.Open(Vector2.Zero);
        await Settle();
        filter.Text = "";
        filter.EmitSignal(LineEdit.SignalName.TextChanged, filter.Text);
        var item = FindItem(list.GetRoot(), typeof(BtProbeGroupedAction).FullName);
        Check("a node group files the type in nested folders below its category",
            item?.GetText(0) == "Grouped probe" && item.GetParent()?.GetText(0) == "Nested"
            && item.GetParent().GetParent()?.GetText(0) == "Probes"
            && item.GetParent().GetParent().GetParent()?.GetText(0) == NodeTypeRegistry.GroupAction
            && !item.GetParent().IsSelectable(0));
        Check("types without a node group stay directly in their category",
            FindItem(list.GetRoot(), typeof(BtProbeAction).FullName)?.GetParent()?.GetText(0) == NodeTypeRegistry.GroupAction);
        Check("sub-groups start folded, their categories open",
            item?.GetParent() is { Collapsed: true } && item.GetParent().GetParent() is { Collapsed: true } folded
            && folded.GetParent() is { Collapsed: false });
        Check("sub-groups are listed before the nodes of their category",
            item?.GetParent()?.GetParent()?.GetParent() is { } category
            && category.GetChildren().SkipWhile(child => !child.IsSelectable(0)).All(child => child.IsSelectable(0))
            && !category.GetFirstChild().IsSelectable(0));

        filter.Text = "nested";
        filter.EmitSignal(LineEdit.SignalName.TextChanged, filter.Text);
        Check("the filter finds a type by its group", list.GetSelected()?.GetMetadata(0).AsString() == typeof(BtProbeGroupedAction).FullName);
        filter.Text = "BtProbeGrouped";
        filter.EmitSignal(LineEdit.SignalName.TextChanged, filter.Text);
        Check("the filter still finds a renamed type by its class name",
            list.GetSelected()?.GetMetadata(0).AsString() == typeof(BtProbeGroupedAction).FullName);
        dialog.Hide();

        Check("a node name labels the box unless the node has a display name of its own",
            new BtProbeGroupedAction().GetLabel() == "Grouped probe"
            && new BtProbeGroupedAction { DisplayName = "Mine" }.GetLabel() == "Mine");
    }

    static TreeItem FindItem(TreeItem parent, string typeName) {
        for (var item = parent?.GetFirstChild(); item != null; item = item.GetNext()) {
            if (item.GetMetadata(0).AsString() == typeName) return item;
            if (FindItem(item, typeName) is { } nested) return nested;
        }
        return null;
    }

    static T FindChildOfType<T>(Node root) where T : Node {
        foreach (var child in root.GetChildren(includeInternal: true)) {
            if (child is T match) return match;
            if (FindChildOfType<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>Waits for the deferred rebuild the editor schedules after every edit.</summary>
    async System.Threading.Tasks.Task Settle() {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    int BoxCount() => _graph.GetChildren().OfType<BehaviorTreeGraphNode>().Count();

    int ConnectionCount() => _graph.GetConnectionList().Count;

    void Check(string what, bool condition) {
        _checks++;
        if (!condition) _failures.Add(what);
    }
}
#endif
