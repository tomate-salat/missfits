#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;
using Misscore.Editor;

namespace Missbehave.Editor;

/// <summary>
/// The authoring surface. Every structural change goes through <see cref="Commit"/>, which records
/// a before/after snapshot with the editor's undo manager, so Ctrl+Z covers adding, connecting,
/// reordering, moving and deleting alike.
/// <para>
/// Reload safety: Godot rebuilds the C# side of every editor object whenever the assembly is
/// reloaded — which pressing play does — and keeps only Variant-compatible fields. Delegate-based
/// signal connections (anything wired with <c>+=</c>) and plain C# collections do not survive.
/// So signals are connected with native method callables via <see cref="Wire"/>, outgoing
/// notifications are real Godot signals, and <see cref="_pool"/> is rebuilt from the tree on demand.
/// </para>
/// </summary>
[Tool]
public partial class BehaviorTreeGraphEdit : GraphEdit {
    /// <summary>The tree resource was changed and needs saving.</summary>
    [Signal]
    public delegate void TreeDirtiedEventHandler();

    /// <summary>The set of selected boxes changed. Listeners read the actual selection themselves.</summary>
    [Signal]
    public delegate void SelectionMovedEventHandler();

    /// <summary>Every box was recreated, so anything painted onto the old ones is gone.</summary>
    [Signal]
    public delegate void RebuiltEventHandler();

    /// <summary>An edit was refused, with a short message saying why.</summary>
    [Signal]
    public delegate void EditRejectedEventHandler(string reason);

    /// <summary>
    /// Undo or redo reached an edit of a tree other than the open one. Expected to open that tree
    /// right away, before the signal returns.
    /// </summary>
    [Signal]
    public delegate void TreeRequestedEventHandler(Resource tree);

    // Untyped so an assembly reload can restore them — see ReloadSafe.
    GodotObject _tree;
    GodotObject _createDialog;
    GodotObject _flowOverlay;

    public BehaviorTree Tree => ReloadSafe.Get<BehaviorTree>(ref _tree);
    CreateNodeDialog CreateDialog => ReloadSafe.Get<CreateNodeDialog>(ref _createDialog);
    public ConnectionFlowOverlay FlowOverlay => ReloadSafe.Get<ConnectionFlowOverlay>(ref _flowOverlay);

    PopupMenu _nodeMenu;

    /// <summary>GraphEdit's internal child that draws the wires.</summary>
    internal const string ConnectionLayerName = "_connection_layer";

    internal const int MenuReplace = 0;
    internal const int MenuDelete = 1;
    internal const int MenuOpenScript = 2;
    internal const int MenuEntryUp = 10;
    internal const int MenuEntryDown = 11;
    internal const int MenuEntryTakeOut = 12;
    internal const int MenuEntryOpenScript = 13;
    internal const int MenuEntryDelete = 14;

    /// <summary>
    /// Every node this session has seen, so undo can bring deleted ones back. Not preserved across
    /// an assembly reload, hence <see cref="EnsurePool"/>.
    /// </summary>
    readonly Dictionary<string, MissNode> _pool = [];

    readonly List<MissNode> _clipboard = [];

    bool _rebuilding;
    bool _rebuildQueued;
    StringName _pendingParent;
    StringName _replaceTarget;
    StringName _menuTarget;
    string _menuEntry;
    string _selectAfterRebuild;

    public override void _Ready() {
        RightDisconnects = true;
        ShowArrangeButton = false;
        MinimapEnabled = false;

        var createDialog = new CreateNodeDialog { Name = "CreateNodeDialog" };
        _createDialog = createDialog;
        // Internal, so it never shows up in the child scans that drive the graph.
        AddChild(createDialog, false, InternalMode.Back);
        createDialog.Connect(CreateNodeDialog.SignalName.TypeChosen, new Callable(this, MethodName.OnTypeChosen));

        // An ordinary child, not an internal one — see PlaceFlowOverlay.
        var flowOverlay = new ConnectionFlowOverlay();
        _flowOverlay = flowOverlay;
        AddChild(flowOverlay);
        PlaceFlowOverlay();

        UseOwnMaterialForDraggedWire();

        // Items are filled in per right-click, see BuildNodeMenu.
        _nodeMenu = new PopupMenu { Name = "NodeMenu" };
        AddChild(_nodeMenu, false, InternalMode.Back);
        _nodeMenu.Connect(PopupMenu.SignalName.IdPressed, new Callable(this, MethodName.OnNodeMenuIdPressed));

        Wire(GraphEdit.SignalName.ConnectionRequest, MethodName.OnConnectionRequest);
        Wire(GraphEdit.SignalName.DisconnectionRequest, MethodName.OnDisconnectionRequest);
        Wire(GraphEdit.SignalName.ConnectionToEmpty, MethodName.OnConnectionToEmpty);
        Wire(GraphEdit.SignalName.ConnectionDragEnded, MethodName.OnConnectionDragEnded);
        Wire(GraphEdit.SignalName.PopupRequest, MethodName.OnPopupRequest);
        Wire(GraphEdit.SignalName.DeleteNodesRequest, MethodName.OnDeleteNodesRequest);
        Wire(GraphEdit.SignalName.CopyNodesRequest, MethodName.OnCopyNodesRequest);
        Wire(GraphEdit.SignalName.CutNodesRequest, MethodName.OnCutNodesRequest);
        Wire(GraphEdit.SignalName.PasteNodesRequest, MethodName.OnPasteNodesRequest);
        Wire(GraphEdit.SignalName.DuplicateNodesRequest, MethodName.OnDuplicateNodesRequest);
        Wire(GraphEdit.SignalName.EndNodeMove, MethodName.OnEndNodeMove);
        Wire(GraphEdit.SignalName.NodeSelected, MethodName.OnNodeSelected);
        Wire(GraphEdit.SignalName.NodeDeselected, MethodName.OnNodeDeselected);
    }

    /// <summary>
    /// Connects one of this node's own signals to one of its methods by name. A method callable is
    /// just an object id plus a method name on the engine side, so it keeps working after the
    /// assembly reloads — unlike <c>Signal += Handler</c>, which wraps a delegate that dies.
    /// </summary>
    void Wire(StringName signal, StringName method) => Connect(signal, new Callable(this, method));

    // ---- loading -----------------------------------------------------------------------------

    public void LoadTree(BehaviorTree tree) {
        _tree = tree;
        _pool.Clear();
        if (tree == null) {
            ClearGraph();
            return;
        }

        foreach (var node in tree.AllNodes()) node?.EnsureId();
        EnsurePool();

        var untouched = tree.AllNodes().All(n => n.GraphPosition == Vector2.Zero);
        RebuildGraph();

        // A tree built in code has no positions yet; lay it out once so it is not a single pile.
        // Deferred, because the layout needs the boxes' real sizes and those only exist after the
        // first layout pass.
        if (untouched && tree.Root != null) CallDeferred(MethodName.ArrangeInitial);
    }

    public void ArrangeInitial() => ArrangeNodes(record: false);

    /// <summary>Makes sure every node of the open tree is in the pool, e.g. after a reload emptied it.</summary>
    void EnsurePool() {
        if (Tree == null) return;
        foreach (var node in Tree.AllNodes()) {
            if (node != null && !string.IsNullOrEmpty(node.Id)) _pool.TryAdd(node.Id, node);
        }
    }

    /// <summary>A node this session has seen, even if it is no longer part of the tree; null if none.</summary>
    internal MissNode KnownNode(string id) => id != null && _pool.TryGetValue(id, out var node) ? node : null;

    void ClearGraph() {
        ClearConnections();

        // Materialise before removing: mutating the child list while enumerating it lazily is a
        // crash. And RemoveChild has to happen before QueueFree, otherwise the doomed boxes are
        // still children until the end of the frame and Godot renames the replacements to
        // "<id>@2" — which silently breaks every ConnectNode call that follows.
        foreach (var child in GetChildren().OfType<BehaviorTreeGraphNode>().ToList()) {
            RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>
    /// Rebuilds every box and wire from the current model.
    /// <para>
    /// Never call this straight out of a GraphEdit signal handler — freeing the nodes GraphEdit is
    /// currently working with (a drag that just ended, a connection being made) crashes the editor.
    /// Use <see cref="QueueRebuild"/> from anything signal-driven.
    /// </para>
    /// </summary>
    public void RebuildGraph() {
        _rebuildQueued = false;
        _rebuilding = true;

        ClearGraph();

        if (Tree != null) {
            var rootBox = NewBox(null, isRoot: true);
            rootBox.PositionOffset = Tree.RootGraphPosition;

            foreach (var node in Tree.AllNodes()) {
                // An entry of a list is drawn as a row inside the list's box, not as a box of its own.
                if (node == null || IsListEntry(node)) continue;
                NewBox(node, isRoot: false).PositionOffset = node.GraphPosition;
            }

            RebuildConnections();
        }

        _rebuilding = false;
        EmitSignal(SignalName.Rebuilt);

        // A freshly replaced node should stay the one being inspected, not the detached original.
        if (!string.IsNullOrEmpty(_selectAfterRebuild)) {
            var box = BoxFor(_selectAfterRebuild);
            _selectAfterRebuild = null;
            if (box != null) {
                box.Selected = true;
                EmitSignal(SignalName.SelectionMoved);
            }
        }
    }

    /// <summary>Rebuilds after the current signal has finished unwinding.</summary>
    void QueueRebuild() {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        CallDeferred(MethodName.RebuildGraph);
    }

    /// <summary>Refreshes labels and order badges only — no boxes are created or freed.</summary>
    void QueueRefresh() {
        if (_rebuildQueued) return;
        CallDeferred(MethodName.RefreshBoxes);
    }

    BehaviorTreeGraphNode NewBox(MissNode node, bool isRoot) {
        var box = new BehaviorTreeGraphNode {
            // Named before entering the tree so it can never collide with a leftover sibling.
            Name = isRoot ? BehaviorTreeGraphNode.RootName : node.Id,
        };
        AddChild(box);
        box.Bind(node, isRoot);
        box.Connect(BehaviorTreeGraphNode.SignalName.ContextMenuRequested, new Callable(this, MethodName.OnBoxContextMenu));
        box.Connect(BehaviorTreeGraphNode.SignalName.EntryContextMenuRequested, new Callable(this, MethodName.OnEntryContextMenu));
        box.Connect(BehaviorTreeGraphNode.SignalName.EntryPicked, new Callable(this, MethodName.OnEntryPicked));
        return box;
    }

    void RebuildConnections() {
        ClearConnections();
        if (Tree == null) return;

        if (Tree.Root != null) ConnectNode(BehaviorTreeGraphNode.RootName, 0, Tree.Root.Id, 0);

        foreach (var node in Tree.AllNodes()) {
            if (node == null || node is AListNode) continue;
            for (var i = 0; i < node.Children.Count; i++) {
                var child = node.Children[i];
                if (child != null) ConnectNode(node.Id, 0, child.Id, 0);
            }
        }

        RefreshBoxes();
    }

    /// <summary>Re-reads titles, summaries, warnings and order badges without rebuilding.</summary>
    public void RefreshBoxes() {
        foreach (var box in Boxes()) {
            if (box.IsRoot) {
                box.Refresh();
                continue;
            }
            var parent = ParentOf(box.Node);
            var order = parent?.Children.IndexOf(box.Node) ?? 0;
            var count = parent?.Children.Count ?? 1;
            // Only the tree knows its entries, so broken links are checked here rather than by the node.
            box.Refresh(order, count, Tree?.LinkProblems(box.Node).ToArray() ?? [],
                entry => Tree?.LinkProblems(entry).ToArray() ?? []);
        }
    }

    IEnumerable<BehaviorTreeGraphNode> Boxes() => GetChildren().OfType<BehaviorTreeGraphNode>();

    // ---- top-to-bottom wiring ----------------------------------------------------------------

    /// <summary>Half the height of a port's grab zone, in graph units.</summary>
    const float PortGrabReach = 10f;

    /// <summary>
    /// GraphEdit places and grabs ports on the left and right of a box; this tree grows downwards, so
    /// the grab zones move to the ports drawn on top and at the bottom (see <see cref="BehaviorTreeGraphNode"/>).
    /// The zone spans the middle of the edge rather than just the dot, so a port is easy to hit.
    /// </summary>
    public override bool _IsInInputHotzone(GodotObject inNode, int inPort, Vector2 mousePosition)
        => inNode is BehaviorTreeGraphNode { HasInput: true } box && IsOnPort(box, box.InputAnchor, mousePosition);

    public override bool _IsInOutputHotzone(GodotObject inNode, int inPort, Vector2 mousePosition)
        => inNode is BehaviorTreeGraphNode { HasOutput: true } box && IsOnPort(box, box.OutputAnchor, mousePosition);

    /// <param name="mousePosition">
    /// As GraphEdit passes it: the mouse in the graph's view, already divided by the zoom.
    /// </param>
    bool IsOnPort(BehaviorTreeGraphNode box, Vector2 anchor, Vector2 mousePosition) {
        var mouse = mousePosition * Zoom;
        var center = box.Position + anchor * Zoom;
        var halfWidth = Mathf.Max(box.Size.X * 0.25f, PortGrabReach) * Zoom;
        var halfHeight = PortGrabReach * Zoom;
        return Mathf.Abs(mouse.X - center.X) <= halfWidth && Mathf.Abs(mouse.Y - center.Y) <= halfHeight;
    }

    /// <summary>
    /// Shapes every wire GraphEdit draws — the saved ones, the one being dragged, and the animated
    /// ones — as a right-angled line from a bottom port down to a top port.
    /// <para>
    /// GraphEdit only hands over the positions of its own left/right ports, so each end is matched
    /// back to its box and moved to the drawn port. GraphEdit asks both in the graph's scrolled view
    /// and in unscrolled canvas space, so both are tried. An end that is no port — the mouse, while
    /// dragging — stays where it is.
    /// </para>
    /// </summary>
    public override Vector2[] _GetConnectionLine(Vector2 fromPosition, Vector2 toPosition)
        => ElbowLine(ToDrawnPort(fromPosition), ToDrawnPort(toPosition));

    Vector2 ToDrawnPort(Vector2 position) {
        const float tolerance = 1f;
        foreach (var box in Boxes()) {
            if (box.GetOutputPortCount() > 0 && box.HasOutput
                && TryMatch(box, box.GetOutputPortPosition(0), box.OutputAnchor, position, tolerance, out var output)) {
                return output;
            }
            if (box.GetInputPortCount() > 0 && box.HasInput
                && TryMatch(box, box.GetInputPortPosition(0), box.InputAnchor, position, tolerance, out var input)) {
                return input;
            }
        }
        return position;
    }

    bool TryMatch(BehaviorTreeGraphNode box, Vector2 nativePort, Vector2 anchor, Vector2 position, float tolerance, out Vector2 moved) {
        // Unscrolled canvas space, as used for the saved wires.
        if (((box.PositionOffset + nativePort) * Zoom).DistanceTo(position) <= tolerance) {
            moved = (box.PositionOffset + anchor) * Zoom;
            return true;
        }
        // The graph's own view, as used for the wire under the mouse.
        if ((box.Position + nativePort * Zoom).DistanceTo(position) <= tolerance) {
            moved = box.Position + anchor * Zoom;
            return true;
        }
        moved = position;
        return false;
    }

    /// <summary>How far below a parent its wires turn sideways, in graph units — at most, see <see cref="ElbowLine"/>.</summary>
    const float WireDrop = 40f;

    /// <summary>
    /// A right-angled wire: straight down out of the parent, sideways, straight down into the child.
    /// The sideways run sits at a fixed distance below the parent, not halfway to each child, so all
    /// wires of one parent share it and read as a single branching bar.
    /// </summary>
    Vector2[] ElbowLine(Vector2 from, Vector2 to) {
        if (Mathf.Abs(to.X - from.X) < 0.5f) return [from, to];

        var drop = to.Y > from.Y ? Mathf.Min((to.Y - from.Y) / 2f, WireDrop * Zoom) : WireDrop * 0.5f * Zoom;
        var turn = from.Y + drop;
        return [from, new Vector2(from.X, turn), new Vector2(to.X, turn), to];
    }

    // ---- live debugging ----------------------------------------------------------------------

    /// <summary>
    /// Animates the wires the last tick went through. A wire counts when both ends were ticked, so a
    /// node that was edited into a different place since the game started does not light up a wire
    /// the running tree never had.
    /// </summary>
    /// <param name="ticked">Status per node id, for the nodes that were ticked.</param>
    public void ShowLiveFlows(IReadOnlyDictionary<string, MissStatus> ticked) {
        if (FlowOverlay == null) return;
        if (Tree == null) {
            FlowOverlay.ClearFlows();
            return;
        }

        PlaceFlowOverlay();

        var flows = new List<LiveFlow>();
        if (Tree.Root != null && ticked.TryGetValue(Tree.Root.Id, out var rootStatus)) {
            flows.Add(new LiveFlow(BehaviorTreeGraphNode.RootName, Tree.Root.Id, rootStatus));
        }
        foreach (var node in Tree.AllNodes()) {
            // A list's entries have no wires; their rows show the status instead.
            if (node == null || node is AListNode || !ticked.ContainsKey(node.Id)) continue;
            foreach (var child in node.Children) {
                if (child != null && ticked.TryGetValue(child.Id, out var status)) flows.Add(new LiveFlow(node.Id, child.Id, status));
            }
        }
        FlowOverlay.ShowFlows(flows);
    }

    public void ClearLiveFlows() => FlowOverlay?.ClearFlows();

    /// <summary>
    /// Keeps the animated wires on top of the plain ones but beneath the boxes. GraphEdit draws its
    /// wires in <c>_connection_layer</c>, an ordinary first child rather than an internal one, so an
    /// internal child of ours would be drawn before it and end up hidden under the wires. Inside the
    /// layer does not work either: the layer has no size, and the renderer skips anything drawn by its
    /// children whenever the layer's own position is out of view — at many zoom and scroll positions.
    /// So the overlay becomes an ordinary child right after the layer. Boxes are added or raised
    /// behind it, never in front.
    /// </summary>
    void PlaceFlowOverlay() {
        var overlay = FlowOverlay;
        var layer = GetChildren().FirstOrDefault(c => c.Name == ConnectionLayerName);
        if (overlay == null || layer == null || overlay.GetParent() != this) return;
        if (overlay.GetIndex() != layer.GetIndex() + 1) MoveChild(overlay, layer.GetIndex() + 1);
    }

    /// <summary>
    /// GraphEdit draws the wire being dragged with a Line2D set to use its parent's material, so the
    /// wire shader it is given never runs. That shader is what narrows a wire to its visible thickness
    /// and smooths its edges; without it the dragged wire comes out at the full, much wider line width.
    /// The line lives in an internal child of GraphEdit and exists from the start, so this runs once.
    /// </summary>
    void UseOwnMaterialForDraggedWire() {
        foreach (var child in GetChildren(includeInternal: true)) {
            if (child.Name == ConnectionLayerName) continue;
            foreach (var line in child.GetChildren(includeInternal: true).OfType<Line2D>()) line.UseParentMaterial = false;
        }
    }

    /// <summary>The Line2D GraphEdit draws the wire being dragged with, for the test.</summary>
    internal Line2D DraggedWire => GetChildren(includeInternal: true)
        .Where(c => c.Name != ConnectionLayerName)
        .SelectMany(c => c.GetChildren(includeInternal: true).OfType<Line2D>())
        .FirstOrDefault();

    public BehaviorTreeGraphNode BoxFor(string id)
        => Boxes().FirstOrDefault(b => !b.IsRoot && b.Node?.Id == id);

    /// <summary>The box a node is drawn in: its own, or for a list entry the box of its list.</summary>
    public BehaviorTreeGraphNode BoxShowing(string id)
        => BoxFor(id) ?? Boxes().FirstOrDefault(b => b.List?.Children.Any(e => e?.Id == id) == true);

    // ---- model helpers -----------------------------------------------------------------------

    MissNode Resolve(StringName name) {
        var key = name.ToString();
        if (key == BehaviorTreeGraphNode.RootName) return null;

        if (_pool.TryGetValue(key, out var node)) return node;
        EnsurePool();
        return _pool.GetValueOrDefault(key);
    }

    bool IsRootName(StringName name) => name.ToString() == BehaviorTreeGraphNode.RootName;

    MissNode ParentOf(MissNode node) {
        if (node == null || Tree == null) return null;
        return Tree.AllNodes().FirstOrDefault(candidate => candidate.Children.Contains(node));
    }

    bool IsAncestorOf(MissNode maybeAncestor, MissNode node) {
        var current = ParentOf(node);
        while (current != null) {
            if (ReferenceEquals(current, maybeAncestor)) return true;
            current = ParentOf(current);
        }
        return false;
    }

    bool IsListEntry(MissNode node) => ParentOf(node) is AListNode;

    /// <summary>
    /// Gives nodes leaving a list a place of their own beside it. Their stored positions date from
    /// before they joined, if they ever had one, and would pile them up somewhere unrelated.
    /// </summary>
    void PlaceBeside(AListNode list, IReadOnlyList<MissNode> nodes) {
        var box = BoxFor(list.Id);
        var width = box?.Size.X ?? 200f;
        for (var i = 0; i < nodes.Count; i++) {
            nodes[i].GraphPosition = list.GraphPosition + new Vector2(width + 60f, i * 60f);
        }
    }

    void Detach(MissNode node) {
        if (node == null) return;
        if (ReferenceEquals(Tree.Root, node)) Tree.Root = null;
        foreach (var candidate in Tree.AllNodes().ToList()) candidate.Children.Remove(node);
        Tree.Orphans.Remove(node);
    }

    void MakeOrphan(MissNode node) {
        if (node == null || Tree.Orphans.Contains(node)) return;
        Tree.Orphans.Add(node);
    }

    /// <summary>Sorts a parent's children by their horizontal position, which is what defines order: left runs first.</summary>
    void SortChildren(MissNode parent) {
        // A list's entries are ordered by their rows, not by where boxes they no longer have once stood.
        if (parent == null || parent is AListNode || parent.Children.Count < 2) return;

        var ordered = parent.Children.OrderBy(c => c?.GraphPosition.X ?? 0f).ToList();
        parent.Children.Clear();
        foreach (var child in ordered) parent.Children.Add(child);
    }

    void SortAllChildren() {
        foreach (var node in Tree.AllNodes().ToList()) SortChildren(node);
    }

    // ---- connections -------------------------------------------------------------------------

    void OnConnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort) {
        if (Tree == null) return;

        var child = Resolve(toNode);
        if (child == null) {
            Reject("The root entry cannot be a child.");
            return;
        }

        if (IsRootName(fromNode)) {
            Commit("Missbehave: set root", () => {
                Detach(child);
                if (Tree.Root != null) MakeOrphan(Tree.Root);
                Tree.Root = child;
                Tree.Orphans.Remove(child);
            });
            return;
        }

        var parent = Resolve(fromNode);
        if (parent == null) return;

        if (ReferenceEquals(parent, child)) {
            Reject("A node cannot be its own child.");
            return;
        }
        if (IsAncestorOf(child, parent)) {
            Reject("That would create a cycle — a behavior tree has to stay a tree.");
            return;
        }
        if (parent.Children.Count >= parent.MaxChildren && !parent.Children.Contains(child)) {
            Reject(parent.MaxChildren == 0
                ? $"{parent.GetLabel()} is a leaf and takes no children."
                : $"{parent.GetLabel()} takes at most {parent.MaxChildren} child(ren).");
            return;
        }
        if (parent.Children.Contains(child)) return;
        if (parent is AListNode list && !list.Accepts(child)) {
            Reject($"{list.GetLabel()} only holds {list.EntryType.Name}s.");
            return;
        }

        // Dragging a wire onto a node that already has a parent reparents it, rather than being
        // refused — that is what enforces the single-parent rule without an extra step.
        Commit("Missbehave: connect", () => {
            Detach(child);
            parent.Children.Add(child);
            SortChildren(parent);
        });
    }

    /// <summary>
    /// How far a wire picked up at its top port has to be dragged before letting go cuts it, in graph
    /// units. The grab zone is generous, so a press near a port easily picks a wire up by accident.
    /// </summary>
    const float DisconnectDistance = 30f;

    /// <summary>The wire picked up at its top port and not yet cut; empty when there is none.</summary>
    string _grabbedFrom = "";
    string _grabbedTo = "";

    /// <summary>Where the mouse was when the wire was picked up, in this control's coordinates.</summary>
    Vector2 _grabbedAt;

    void OnDisconnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort) {
        if (Tree == null) return;
        var child = Resolve(toNode);
        if (child == null) return;

        // GraphEdit asks as soon as a wire is picked up at its top port. While the mouse is still down
        // this is only a grab: whether it becomes a cut is decided on release, see OnConnectionDragEnded.
        if (Input.IsMouseButtonPressed(MouseButton.Left)) {
            _grabbedFrom = fromNode;
            _grabbedTo = toNode;
            _grabbedAt = GetLocalMousePosition();
            // Only the drawing: the dragged wire replaces it until the release decides. Deferred, as
            // GraphEdit is still walking its connections while it asks.
            CallDeferred(GraphEdit.MethodName.DisconnectNode, fromNode, fromPort, toNode, toPort);
            return;
        }

        Disconnect(child);
    }

    void Disconnect(MissNode child) {
        Commit("Missbehave: disconnect", () => {
            Detach(child);
            MakeOrphan(child);
        });
    }

    public void OnConnectionDragEnded() {
        if (string.IsNullOrEmpty(_grabbedTo)) return;

        var from = _grabbedFrom;
        var to = _grabbedTo;
        _grabbedFrom = "";
        _grabbedTo = "";

        ReleaseGrabbedWire(from, to, _grabbedAt, GetLocalMousePosition());
    }

    /// <summary>
    /// Decides what a wire picked up at <paramref name="grabbedAt"/> becomes once let go at
    /// <paramref name="releasedAt"/> (both in this control's coordinates): cut when dragged far enough,
    /// otherwise put back. Measured from the press rather than the port, as the grab zone spans half
    /// the box — a plain click there is still no drag. Public so the headless test can pick the spots.
    /// </summary>
    public void ReleaseGrabbedWire(string from, string to, Vector2 grabbedAt, Vector2 releasedAt) {
        var child = Resolve(to);
        var box = BoxFor(to);
        if (Tree == null || child == null || box == null) return;

        // Dropped onto another parent: GraphEdit's connection request has already moved it there.
        var stillAttached = IsRootName(from) ? ReferenceEquals(Tree.Root, child) : ParentOf(child)?.Id == from;
        if (!stillAttached) return;

        if (releasedAt.DistanceTo(grabbedAt) >= DisconnectDistance * Zoom) {
            Disconnect(child);
        }
        else {
            // Not far enough: nothing changed in the tree, so just draw the wire again.
            RebuildConnections();
        }
    }

    void OnConnectionToEmpty(StringName fromNode, long fromPort, Vector2 releasePosition) {
        _pendingParent = fromNode;
        _replaceTarget = null;
        // From a list, only what the list can hold is worth offering.
        CreateDialog.Open(GraphPositionOf(releasePosition), (Resolve(fromNode) as AListNode)?.EntryType);
    }

    void OnPopupRequest(Vector2 atPosition) {
        _pendingParent = null;
        _replaceTarget = null;
        CreateDialog.Open(GraphPositionOf(atPosition));
    }

    Vector2 GraphPositionOf(Vector2 localPosition) => (localPosition + ScrollOffset) / Zoom;

    void OnTypeChosen(string typeName) {
        var type = NodeTypeRegistry.FindByName(typeName);

        if (_replaceTarget != null) {
            var target = _replaceTarget;
            _replaceTarget = null;
            ReplaceNode(target, type);
            return;
        }

        var parentName = _pendingParent;
        _pendingParent = null;
        CreateNode(type, CreateDialog.PendingPosition, parentName);
    }

    void OnBoxContextMenu(StringName boxName, Vector2 screenPosition) {
        var box = Boxes().FirstOrDefault(b => b.Name == boxName);
        if (box == null || box.IsRoot) return;

        SelectForContextMenu(box);
        _menuTarget = boxName;
        BuildNodeMenu(box.Node);

        _nodeMenu.Position = (Vector2I) screenPosition;
        _nodeMenu.ResetSize();
        _nodeMenu.Popup();
    }

    void OnEntryContextMenu(StringName boxName, string entryId, Vector2 screenPosition) {
        var box = Boxes().FirstOrDefault(b => b.Name == boxName);
        if (box?.List == null) return;

        SelectForContextMenu(box);
        if (box.SelectedEntry != entryId) box.PickEntry(entryId);
        _menuTarget = boxName;
        _menuEntry = entryId;

        var entries = box.List.Children;
        var index = entries.IndexOf(entries.FirstOrDefault(e => e?.Id == entryId));
        _nodeMenu.Clear();
        _nodeMenu.AddItem("Move up", MenuEntryUp);
        _nodeMenu.SetItemDisabled(_nodeMenu.GetItemIndex(MenuEntryUp), index <= 0);
        _nodeMenu.AddItem("Move down", MenuEntryDown);
        _nodeMenu.SetItemDisabled(_nodeMenu.GetItemIndex(MenuEntryDown), index < 0 || index >= entries.Count - 1);
        _nodeMenu.AddItem("Take out of list", MenuEntryTakeOut);
        _nodeMenu.AddItem("Open script", MenuEntryOpenScript);
        _nodeMenu.SetItemDisabled(_nodeMenu.GetItemIndex(MenuEntryOpenScript), ScriptOf(index >= 0 ? entries[index] : null) == null);
        _nodeMenu.AddSeparator();
        _nodeMenu.AddItem("Delete", MenuEntryDelete);

        _nodeMenu.Position = (Vector2I) screenPosition;
        _nodeMenu.ResetSize();
        _nodeMenu.Popup();
    }

    void OnEntryPicked(StringName boxName, string entryId) => EmitSignal(SignalName.SelectionMoved);

    void OnNodeMenuIdPressed(long id) {
        var target = _menuTarget;
        var entry = _menuEntry;
        _menuTarget = null;
        _menuEntry = null;
        if (target == null) return;

        if (entry != null) {
            OnEntryMenuIdPressed(id, Resolve(target) as AListNode, entry);
            return;
        }

        switch (id) {
            case MenuReplace:
                var node = Resolve(target);
                if (node == null) return;
                _pendingParent = null;
                _replaceTarget = target;
                CreateDialog.OpenForReplace(node);
                break;
            case MenuOpenScript:
                var script = ScriptOf(Resolve(target));
                // EditScript honours the configured external editor, so C# lands in the IDE.
                if (script != null && Engine.IsEditorHint()) EditorInterface.Singleton.EditScript(script);
                break;
            case MenuDelete:
                OnDeleteNodesRequest([target]);
                break;
        }
    }

    void OnEntryMenuIdPressed(long id, AListNode list, string entryId) {
        var entry = list?.Children.FirstOrDefault(e => e?.Id == entryId);
        if (entry == null) return;

        switch (id) {
            case MenuEntryUp:
                MoveEntry(list, entry, -1);
                break;
            case MenuEntryDown:
                MoveEntry(list, entry, 1);
                break;
            case MenuEntryTakeOut:
                TakeOutOfList(list, entry);
                break;
            case MenuEntryOpenScript:
                if (ScriptOf(entry) is { } script && Engine.IsEditorHint()) EditorInterface.Singleton.EditScript(script);
                break;
            case MenuEntryDelete:
                Commit("Missbehave: delete list entry", () => Detach(entry));
                break;
        }
    }

    /// <summary>Moves an entry up (-1) or down (+1) its list, which is the order the entries run in.</summary>
    public void MoveEntry(AListNode list, MissNode entry, int direction) {
        var index = list.Children.IndexOf(entry);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= list.Children.Count) return;

        Commit("Missbehave: reorder list entry", () => {
            list.Children.RemoveAt(index);
            list.Children.Insert(target, entry);
        });
    }

    /// <summary>Turns an entry back into a box of its own, unconnected, beside the list.</summary>
    public void TakeOutOfList(AListNode list, MissNode entry) {
        if (!list.Children.Contains(entry)) return;

        Commit("Missbehave: take out of list", () => {
            Detach(entry);
            PlaceBeside(list, [entry]);
            MakeOrphan(entry);
        });
        _selectAfterRebuild = entry.Id;
    }

    /// <summary>
    /// Right-clicking a box selects it, so the Inspector shows what the menu is about to act on.
    /// A box that is already part of the selection leaves the selection alone.
    /// </summary>
    void SelectForContextMenu(BehaviorTreeGraphNode box) {
        if (box.Selected) return;
        foreach (var other in Boxes().ToList()) other.Selected = ReferenceEquals(other, box);
        EmitSignal(SignalName.SelectionMoved);
    }

    /// <summary>
    /// Rebuilt on every right-click rather than once in <see cref="_Ready"/>: an assembly reload does
    /// not run _Ready again, so a menu filled there would never pick up new entries, and some
    /// entries depend on the node anyway.
    /// </summary>
    void BuildNodeMenu(MissNode node) {
        _nodeMenu.Clear();
        _nodeMenu.AddItem("Replace with…", MenuReplace);
        _nodeMenu.AddItem("Open script", MenuOpenScript);
        _nodeMenu.SetItemDisabled(_nodeMenu.GetItemIndex(MenuOpenScript), ScriptOf(node) == null);
        _nodeMenu.AddSeparator();
        _nodeMenu.AddItem("Delete", MenuDelete);
    }

    /// <summary>The C# script behind a node, or null when it has no file to open.</summary>
    internal static Script ScriptOf(MissNode node) {
        var script = node?.GetScript().As<Script>();
        return script != null && !string.IsNullOrEmpty(script.ResourcePath) ? script : null;
    }

    /// <summary>
    /// Adds a node of the given type, optionally hanging it off <paramref name="parentName"/>.
    /// Public so the headless editor test can exercise the same path as the create dialog.
    /// </summary>
    public MissNode CreateNode(NodeTypeInfo type, Vector2 position, StringName parentName = null) {
        if (Tree == null || type == null) return null;

        var node = type.Create();
        node.GraphPosition = position;
        _pool[node.Id] = node;

        Commit($"Missbehave: add {type.Name}", () => {
            if (parentName == null) {
                if (Tree.Root == null) Tree.Root = node;
                else MakeOrphan(node);
                return;
            }

            if (IsRootName(parentName)) {
                if (Tree.Root != null) MakeOrphan(Tree.Root);
                Tree.Root = node;
                return;
            }

            var parent = Resolve(parentName);
            if (parent is AListNode list && !list.Accepts(node)) {
                MakeOrphan(node);
                return;
            }
            if (parent != null && parent.Children.Count < parent.MaxChildren) {
                parent.Children.Add(node);
                SortChildren(parent);
            }
            else {
                MakeOrphan(node);
            }
        });

        return node;
    }

    /// <summary>
    /// Swaps the node behind <paramref name="name"/> for a new one of <paramref name="type"/>, in the
    /// same slot, with the same children and every stored property both types share. Children
    /// beyond what the new type accepts become orphans.
    /// <para>
    /// The replacement gets a fresh id rather than inheriting the old one: undo works on snapshots
    /// keyed by id, and it has to be able to tell the two objects apart to bring the original back.
    /// </para>
    /// </summary>
    public MissNode ReplaceNode(StringName name, NodeTypeInfo type) {
        if (Tree == null || type == null) return null;

        var old = Resolve(name);
        if (old == null) {
            Reject("The root entry cannot be replaced.");
            return null;
        }
        if (old.GetType() == type.Type) return old;

        var replacement = type.Create();
        CopySharedProperties(old, replacement);
        _pool[replacement.Id] = replacement;

        var children = old.Children.Where(c => c != null).ToList();
        var newList = replacement as AListNode;
        // A list keeps only what it can hold; everything else stays on the canvas as before.
        var fitting = newList == null ? children : children.Where(newList.Accepts).ToList();
        var kept = fitting.Take(replacement.MaxChildren).ToList();
        var dropped = children.Except(kept).ToList();

        // Folding a selector into a list keeps what it did.
        if (newList != null && old is SelectorNode or SelectorReactiveNode or SelectorRandomNode) newList.Mode = ListMode.Selector;
        if (newList != null && old is AListNode oldList) newList.Mode = oldList.Mode;

        Commit($"Missbehave: replace {old.GetType().Name} with {type.Name}", () => {
            var parent = ParentOf(old);
            if (ReferenceEquals(Tree.Root, old)) {
                Tree.Root = replacement;
            }
            else if (parent != null) {
                parent.Children[parent.Children.IndexOf(old)] = replacement;
            }
            else {
                var index = Tree.Orphans.IndexOf(old);
                if (index >= 0) Tree.Orphans[index] = replacement;
                else Tree.Orphans.Add(replacement);
            }

            old.Children.Clear();
            foreach (var child in kept) replacement.Children.Add(child);
            foreach (var child in dropped) MakeOrphan(child);

            // Entries unfolded out of a list need boxes somewhere sensible: in a row below.
            if (old is AListNode && newList == null) {
                for (var i = 0; i < children.Count; i++) {
                    children[i].GraphPosition = replacement.GraphPosition + new Vector2(i * 220f, 120f);
                }
            }
            else if (old is AListNode unfolded) {
                PlaceBeside(unfolded, dropped);
            }
        });

        _selectAfterRebuild = replacement.Id;

        // After Commit, whose dirty notification would otherwise overwrite the message at once.
        if (dropped.Count > 0) {
            Reject($"{type.Name} takes {replacement.MaxChildren} child(ren) — {dropped.Count} left unconnected.");
        }

        return replacement;
    }

    /// <summary>
    /// Copies every stored property that exists on both nodes with the same name and type —
    /// DisplayName and GraphPosition always, parameters like WaitTime when both types have one.
    /// </summary>
    static void CopySharedProperties(MissNode from, MissNode to) {
        var targets = new Dictionary<string, Godot.Collections.Dictionary>();
        foreach (var property in to.GetPropertyList()) targets.TryAdd(property["name"].AsString(), property);

        foreach (var property in from.GetPropertyList()) {
            var name = property["name"].AsString();
            if (name is nameof(MissNode.Id) or nameof(MissNode.Children) or "script"
                || name.StartsWith("resource_") || name.StartsWith("metadata/")) {
                continue;
            }
            if (!IsStored(property) || !targets.TryGetValue(name, out var target) || !IsStored(target)) continue;
            if (!SameType(property, target)) continue;

            to.Set(name, from.Get(name));
        }
    }

    /// <summary>Blackboard parameters count as stored even while at their default, when Godot is told otherwise.</summary>
    static bool IsStored(Godot.Collections.Dictionary property)
        => (((PropertyUsageFlags) property["usage"].AsInt64()) & PropertyUsageFlags.Storage) != 0
           || property["hint_string"].AsString() == BbParams.HintString;

    static bool SameType(Godot.Collections.Dictionary a, Godot.Collections.Dictionary b) {
        if (a["type"].AsInt32() != b["type"].AsInt32()) return false;

        // For resources and enums the hint string names the actual type or the value set; for plain
        // ranges it only differs in bounds, which is no reason to drop the value.
        var hint = (PropertyHint) a["hint"].AsInt32();
        var isTyped = a["type"].AsInt32() == (int) Variant.Type.Object
                      || hint is PropertyHint.Enum or PropertyHint.ResourceType or PropertyHint.TypeString;
        return !isTyped || a["hint_string"].AsString() == b["hint_string"].AsString();
    }

    // ---- editing -----------------------------------------------------------------------------

    void OnDeleteNodesRequest(Godot.Collections.Array<StringName> names) {
        if (Tree == null || names.Count == 0) return;

        // With one entry of a list picked, Delete means that entry, not the whole list.
        if (names.Count == 1 && Boxes().FirstOrDefault(b => b.Name == names[0]) is { List: { } list, SelectedEntry: { Length: > 0 } picked }
            && list.Children.FirstOrDefault(e => e?.Id == picked) is { } entry) {
            Commit("Missbehave: delete list entry", () => Detach(entry));
            return;
        }

        var targets = names.Select(Resolve).Where(n => n != null).ToList();
        if (targets.Count == 0) {
            Reject("The root entry cannot be deleted.");
            return;
        }

        Commit("Missbehave: delete", () => {
            foreach (var node in targets) {
                // Children are kept as orphans rather than cascade-deleted: losing a whole subtree
                // to one misclick is worse than leaving a few loose nodes on the canvas.
                var children = node.Children.Where(c => c != null).ToList();
                if (node is AListNode list) PlaceBeside(list, children);
                foreach (var child in children) {
                    node.Children.Remove(child);
                    MakeOrphan(child);
                }
                Detach(node);
            }
        });
    }

    void OnCopyNodesRequest() {
        _clipboard.Clear();
        foreach (var box in Boxes().Where(b => b.Selected && !b.IsRoot).ToList()) {
            _clipboard.Add(box.Node);
        }
    }

    void OnCutNodesRequest() {
        OnCopyNodesRequest();
        var names = new Godot.Collections.Array<StringName>();
        foreach (var box in Boxes().Where(b => b.Selected && !b.IsRoot).ToList()) names.Add(box.Name);
        OnDeleteNodesRequest(names);
    }

    void OnPasteNodesRequest() {
        if (Tree == null || _clipboard.Count == 0) return;

        var offset = new Vector2(40, 40);
        var copies = _clipboard.Select(node => DeepCopy(node, offset)).ToList();

        Commit("Missbehave: paste", () => {
            foreach (var copy in copies) MakeOrphan(copy);
        });
    }

    void OnDuplicateNodesRequest() {
        OnCopyNodesRequest();
        OnPasteNodesRequest();
    }

    /// <summary>Clones a subtree with fresh ids, so pasted nodes are independent of the originals.</summary>
    MissNode DeepCopy(MissNode source, Vector2 offset) {
        var copy = (MissNode) source.Duplicate(false);
        copy.ResourcePath = "";
        copy.Id = MissNode.NewId();
        copy.GraphPosition = source.GraphPosition + offset;

        var children = new Godot.Collections.Array<MissNode>();
        foreach (var child in source.Children) {
            if (child != null) children.Add(DeepCopy(child, offset));
        }
        copy.Children = children;

        _pool[copy.Id] = copy;
        return copy;
    }

    void OnEndNodeMove() {
        if (Tree == null || _rebuilding) return;

        Commit("Missbehave: move", () => {
            foreach (var box in Boxes().ToList()) {
                if (box.IsRoot) Tree.RootGraphPosition = box.PositionOffset;
                else if (box.Node != null) box.Node.GraphPosition = box.PositionOffset;
            }
            SortAllChildren();
        }, structural: false);
    }

    void OnNodeSelected(Node node) => EmitSignal(SignalName.SelectionMoved);

    void OnNodeDeselected(Node node) => EmitSignal(SignalName.SelectionMoved);

    // ---- layout ------------------------------------------------------------------------------

    public void ArrangeNodes(bool record = true) {
        if (Tree?.Root == null) return;

        var rootBox = Boxes().FirstOrDefault(b => b.IsRoot);
        if (rootBox == null) return;

        var layoutRoot = new BtLayoutNode(rootBox);
        BuildLayout(layoutRoot, Tree.Root);

        var scale = Engine.IsEditorHint() ? EditorInterface.Singleton.GetEditorScale() : 1f;
        BtLayout.UpdatePositions(layoutRoot, scale);

        void Apply() {
            ApplyLayout(layoutRoot);
            Tree.RootGraphPosition = rootBox.PositionOffset;
        }

        if (record) Commit("Missbehave: arrange", Apply, structural: false);
        else Apply();
    }

    void BuildLayout(BtLayoutNode parent, MissNode node) {
        var box = BoxFor(node.Id);
        if (box == null) return;

        var layoutNode = parent.AddChild(box);
        if (node is AListNode) return;
        foreach (var child in node.Children) {
            if (child != null) BuildLayout(layoutNode, child);
        }
    }

    void ApplyLayout(BtLayoutNode layoutNode) {
        if (layoutNode.Item is BehaviorTreeGraphNode box) {
            var position = new Vector2(layoutNode.X, layoutNode.Y);
            box.PositionOffset = position;
            if (!box.IsRoot && box.Node != null) box.Node.GraphPosition = position;
        }
        foreach (var child in layoutNode.Children) ApplyLayout(child);
    }

    // ---- undo/redo ---------------------------------------------------------------------------

    /// <summary>
    /// Applies <paramref name="mutate"/> and records it as one undoable action. Undo/redo restore a
    /// structural snapshot rather than replaying individual operations, which keeps every edit —
    /// including reparenting and multi-node deletes — correct with one pair of methods.
    /// </summary>
    /// <param name="structural">
    /// False for edits that only move boxes around: the graph already shows the right thing, so
    /// only the order badges need refreshing and no node has to be destroyed.
    /// </param>
    void Commit(string actionName, Action mutate, bool structural = true) {
        var before = TakeSnapshot();
        mutate();
        var after = TakeSnapshot();

        // Looked up each time rather than held: a reference kept in a field would be one more thing
        // to lose on reload, and the editor singleton is always there when we are.
        var undoRedo = Engine.IsEditorHint() ? EditorInterface.Singleton?.GetEditorUndoRedo() : null;
        if (undoRedo != null) {
            // Not marked unsaved in Godot's history: saving the tree cannot mark that history saved
            // again, so the editor's "(*)" would outlive every save. The panel tracks unsaved trees.
            undoRedo.CreateAction(actionName, UndoRedo.MergeMode.Disable, Tree, false, false);
            undoRedo.AddDoMethod(this, MethodName.RestoreSnapshot, after);
            undoRedo.AddUndoMethod(this, MethodName.RestoreSnapshot, before);

            // The mutation is already applied, so do not let CommitAction replay it.
            undoRedo.CommitAction(false);
        }

        if (structural) QueueRebuild();
        else QueueRefresh();

        EmitSignal(SignalName.TreeDirtied);
    }

    internal Godot.Collections.Dictionary TakeSnapshot() {
        EnsurePool();

        var nodes = new Godot.Collections.Dictionary();
        foreach (var (id, node) in _pool) {
            var children = new Godot.Collections.Array<string>();
            foreach (var child in node.Children) {
                if (child != null) children.Add(child.Id);
            }
            nodes[id] = new Godot.Collections.Dictionary {
                // The node itself travels along, so undo can bring it back even once the pool has
                // forgotten it — after another tree was opened, or after a reload.
                { "node", node },
                { "children", children },
                { "pos", node.GraphPosition },
            };
        }

        var orphans = new Godot.Collections.Array<string>();
        foreach (var orphan in Tree.Orphans) {
            if (orphan != null) orphans.Add(orphan.Id);
        }

        return new Godot.Collections.Dictionary {
            { "tree", Tree },
            { "root", Tree.Root?.Id ?? "" },
            { "rootPos", Tree.RootGraphPosition },
            { "nodes", nodes },
            { "orphans", orphans },
        };
    }

    public void RestoreSnapshot(Godot.Collections.Dictionary snapshot) {
        // Undo is shared by every tree edited this session: an edit of another tree is shown by
        // opening that tree, never applied to whichever one happens to be open.
        if (snapshot.TryGetValue("tree", out var owner) && owner.AsGodotObject() is BehaviorTree ownerTree
            && !ReferenceEquals(ownerTree, Tree)) {
            EmitSignal(SignalName.TreeRequested, ownerTree);
            if (!ReferenceEquals(ownerTree, Tree)) return;
        }
        if (Tree == null) return;
        EnsurePool();

        var nodes = snapshot["nodes"].AsGodotDictionary();
        foreach (var key in nodes.Keys) {
            var entry = nodes[key].AsGodotDictionary();
            if (entry.TryGetValue("node", out var kept) && kept.AsGodotObject() is MissNode keptNode) {
                _pool.TryAdd(keptNode.Id, keptNode);
            }
        }

        foreach (var key in nodes.Keys) {
            var id = key.AsString();
            if (!_pool.TryGetValue(id, out var node)) continue;

            var entry = nodes[key].AsGodotDictionary();
            node.GraphPosition = entry["pos"].AsVector2();

            var children = new Godot.Collections.Array<MissNode>();
            foreach (var childId in entry["children"].AsGodotArray<string>()) {
                if (_pool.TryGetValue(childId, out var child)) children.Add(child);
            }
            node.Children = children;
        }

        var rootId = snapshot["root"].AsString();
        Tree.Root = string.IsNullOrEmpty(rootId) ? null : _pool.GetValueOrDefault(rootId);
        Tree.RootGraphPosition = snapshot["rootPos"].AsVector2();

        Tree.Orphans.Clear();
        foreach (var orphanId in snapshot["orphans"].AsGodotArray<string>()) {
            if (_pool.TryGetValue(orphanId, out var orphan)) Tree.Orphans.Add(orphan);
        }

        QueueRebuild();
        EmitSignal(SignalName.TreeDirtied);
    }

    void Reject(string reason) => EmitSignal(SignalName.EditRejected, reason);
}
#endif
