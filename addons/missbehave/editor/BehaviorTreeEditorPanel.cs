#if TOOLS
using System;
using System.Linq;
using Godot;
using Misscore.Editor;
using Misscore;

namespace Missbehave.Editor;

/// <summary>
/// Bottom-panel editor: toolbar, graph surface and a status line.
/// <para>
/// All wiring uses native method callables and Godot signals, never <c>+=</c> or C# delegate
/// fields: pressing play reloads the assembly, and anything delegate-based comes back dead.
/// See <see cref="BehaviorTreeGraphEdit"/> for the details.
/// </para>
/// </summary>
[Tool]
public partial class BehaviorTreeEditorPanel : VBoxContainer {
    /// <summary>The dirty flag changed, so the panel button can show an asterisk.</summary>
    [Signal]
    public delegate void DirtyStateChangedEventHandler(bool dirty);

    /// <summary>A tree was opened, so the debugger can follow it.</summary>
    [Signal]
    public delegate void TreeOpenedEventHandler(string resourcePath);

    /// <summary>The user picked a different running instance to watch.</summary>
    [Signal]
    public delegate void InstanceRequestedEventHandler(long runnerId);

    // Untyped so an assembly reload can restore them — see ReloadSafe.
    GodotObject _tree;
    GodotObject _graph;
    GodotObject _blackboard;

    public BehaviorTree Tree => ReloadSafe.Get<BehaviorTree>(ref _tree);
    public BehaviorTreeGraphEdit Graph => ReloadSafe.Get<BehaviorTreeGraphEdit>(ref _graph);
    public BlackboardPanel Blackboard => ReloadSafe.Get<BlackboardPanel>(ref _blackboard);

    /// <summary>
    /// Test seam: when set, receives what would otherwise go to the editor's Inspector, so the
    /// headless test can stand in for the editor. Deliberately not relied upon in the editor — it
    /// is a plain delegate and would be null again after a reload.
    /// </summary>
    public Action<Resource> EditInInspector;

    Label _title;
    Label _status;
    Button _saveButton;
    Button _revertButton;
    ConfirmationDialog _revertDialog;
    Button _blackboardToggle;
    OptionButton _instances;

    /// <summary>
    /// Trees edited since they were last saved. Holding them here also keeps an unsaved tree in
    /// memory, edits and all, while another one is open. Untyped so an assembly reload restores it.
    /// </summary>
    Godot.Collections.Array _unsavedTrees = [];

    bool _showingLiveStatus;
    string[] _lastFrameIds;
    byte[] _lastFrame;
    bool _syncingSelection;
    string _pendingAutoOpen;

    public override void _Ready() {
        Name = "Missbehave";
        CustomMinimumSize = new Vector2(0, 320);
        SizeFlagsVertical = SizeFlags.ExpandFill;

        AddChild(BuildToolbar());

        // The blackboard sits beside the graph, so what a tree has to work with is always in view.
        var split = new HSplitContainer { Name = "Split", SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);

        _graph = new BehaviorTreeGraphEdit { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(Graph);
        Graph.Connect(BehaviorTreeGraphEdit.SignalName.TreeDirtied, new Callable(this, MethodName.MarkDirty));
        Graph.Connect(BehaviorTreeGraphEdit.SignalName.SelectionMoved, new Callable(this, MethodName.OnSelectionMoved));
        Graph.Connect(BehaviorTreeGraphEdit.SignalName.EditRejected, new Callable(this, MethodName.OnGraphRejected));
        Graph.Connect(BehaviorTreeGraphEdit.SignalName.Rebuilt, new Callable(this, MethodName.OnGraphRebuilt));
        Graph.Connect(BehaviorTreeGraphEdit.SignalName.TreeRequested, new Callable(this, MethodName.OnTreeRequested));

        _blackboard = new BlackboardPanel {
            UndoPrefix = "Missbehave",
            NotOpenHint = "Open this tree in Missbehave to link entries",
        };
        split.AddChild(Blackboard);
        Blackboard.Connect(BlackboardPanel.SignalName.BlackboardEdited, new Callable(this, MethodName.OnBlackboardEdited));
        Blackboard.Connect(BlackboardPanel.SignalName.EditRejected, new Callable(this, MethodName.OnGraphRejected));
        Blackboard.Connect(BlackboardPanel.SignalName.SourceRequested, new Callable(this, MethodName.OnTreeRequested));

        _status = new Label { Text = "No tree open." };
        _status.AddThemeFontSizeOverride("font_size", 11);
        AddChild(_status);

        // A custom Resource does not emit "changed" when the Inspector writes a property — only the
        // built-in ones do, from their own setters. So listen to the Inspector itself, which also
        // covers every exported property of every user-written leaf for free. Godot removes the
        // connection when this panel is freed.
        if (Engine.IsEditorHint()) {
            var inspector = EditorInterface.Singleton?.GetInspector();
            inspector?.Connect(EditorInspector.SignalName.PropertyEdited,
                new Callable(this, MethodName.OnInspectorPropertyEdited));
        }

        ShowEmptyState();
    }

    /// <summary>Re-reads titles, summaries and warnings after the Inspector edited one of our nodes.</summary>
    public void OnInspectorPropertyEdited(string property) {
        if (Tree == null) return;

        if (Engine.IsEditorHint()) {
            var edited = EditorInterface.Singleton.GetInspector()?.GetEditedObject();
            if (edited is not MissNode && edited is not BehaviorTree) return;
        }

        Graph.RefreshBoxes();
        MarkDirty();
    }

    Control BuildToolbar() {
        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 4);

        _title = new Label { Text = "—", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        bar.AddChild(_title);

        _instances = new OptionButton {
            TooltipText = "Which running instance of this tree to colour live",
            Visible = false,
            Flat = true,
        };
        _instances.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnInstanceSelected));
        bar.AddChild(_instances);

        _saveButton = Tool("Save", "Save the tree resource (Ctrl+S)", MethodName.Save);
        bar.AddChild(_saveButton);

        _revertButton = Tool("Revert", "Discard the unsaved changes and go back to the saved tree", MethodName.RevertPressed);
        _revertButton.Disabled = true;
        bar.AddChild(_revertButton);
        _revertDialog = new ConfirmationDialog { Title = "Revert behavior tree", OkButtonText = "Discard changes" };
        _revertDialog.Connect(AcceptDialog.SignalName.Confirmed, new Callable(this, MethodName.RevertTree));
        bar.AddChild(_revertDialog);

        _blackboardToggle = new Button {
            Text = "Blackboard",
            TooltipText = "Show or hide the tree's blackboard",
            Flat = true,
            ToggleMode = true,
            ButtonPressed = true,
        };
        _blackboardToggle.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.OnBlackboardToggled));
        bar.AddChild(_blackboardToggle);

        bar.AddChild(Tool("Arrange", "Lay the tree out automatically", MethodName.ArrangePressed));
        bar.AddChild(Tool("Validate", "List structural problems", MethodName.Validate));
        bar.AddChild(Tool("Reload types", "Re-scan the project for behavior node classes", MethodName.ReloadTypesPressed));

        return bar;
    }

    Button Tool(string text, string tooltip, StringName method) {
        var button = new Button { Text = text, TooltipText = tooltip, Flat = true };
        button.Connect(BaseButton.SignalName.Pressed, new Callable(this, method));
        return button;
    }

    void ArrangePressed() => Graph.ArrangeNodes();

    void ReloadTypesPressed() {
        NodeTypeRegistry.Refresh();
        Graph.RefreshBoxes();
        SetStatus($"{NodeTypeRegistry.Types.Count} node types found.");
    }

    void OnInstanceSelected(long index) {
        var id = _instances.GetItemMetadata((int) index).AsInt64();
        EmitSignal(SignalName.InstanceRequested, id);
    }

    void OnGraphRejected(string message) => SetStatus(message, warning: true);

    void OnBlackboardToggled(bool visible) => Blackboard.Visible = visible;

    void OnBlackboardEdited() {
        Graph.RefreshBoxes();
        MarkDirty();
    }

    // ---- opening -----------------------------------------------------------------------------

    public void OpenTree(BehaviorTree tree) {
        if (tree == null || ReferenceEquals(tree, Tree)) return;

        // No saving on the way out: the tree left behind keeps its unsaved edits in memory, and
        // they are saved with it later, or when the editor saves or runs the project.
        // Statuses belong to the previous tree; the debugger resends for this one.
        ClearStatuses();
        _tree = tree;
        Graph.LoadTree(tree);
        Blackboard.ShowSource(tree);

        _title.Text = string.IsNullOrEmpty(tree.ResourcePath) ? "(unsaved tree)" : tree.ResourcePath;
        ShowDirtyState();
        SetStatus(Describe());
        EmitSignal(SignalName.TreeOpened, tree.ResourcePath);
    }

    /// <summary>Undo or redo went back to an edit of a tree that is not open.</summary>
    void OnTreeRequested(Resource tree) => OpenTree(tree as BehaviorTree);

    // ---- live debugging ----------------------------------------------------------------------

    /// <summary>Colours the graph from one streamed frame. 0xFF means the node was not ticked.</summary>
    public void ShowFrame(string[] idTable, byte[] statuses) {
        if (idTable == null || statuses == null) return;
        _showingLiveStatus = true;
        _lastFrameIds = idTable;
        _lastFrame = statuses;

        var seen = new System.Collections.Generic.HashSet<string>();
        var ticked = new System.Collections.Generic.Dictionary<string, MissStatus>();

        for (var i = 0; i < statuses.Length && i < idTable.Length; i++) {
            if (statuses[i] != MissStatusExtensions.NotTicked) ticked[idTable[i]] = (MissStatus) statuses[i];

            var box = Graph.BoxFor(idTable[i]);
            if (box == null) continue;

            seen.Add(idTable[i]);
            if (statuses[i] == MissStatusExtensions.NotTicked) box.ShowLiveStatus(null);
            else box.ShowLiveStatus((MissStatus) statuses[i]);
        }
        Graph.ShowLiveFlows(ticked);

        // Nodes the running tree does not even contain (edited since launch) never run either, so
        // they fade like any other node that was not reached.
        foreach (var box in GetGraphBoxes()) {
            if (!box.IsRoot && box.Node != null && !seen.Contains(box.Node.Id)) box.ShowLiveStatus(null);
            if (box.List != null) box.ShowLiveEntryStatuses(ticked);
        }
    }

    public void ClearStatuses() {
        _lastFrameIds = null;
        _lastFrame = null;
        Graph?.ClearLiveFlows();
        if (!_showingLiveStatus) return;
        _showingLiveStatus = false;
        foreach (var box in GetGraphBoxes()) box.ClearLiveStatus();
    }

    /// <summary>
    /// Repaints the last frame onto the new boxes. A paused game sends no further frames, so without
    /// this an edit that rebuilds the graph would leave it blank until the game resumes.
    /// </summary>
    void OnGraphRebuilt() {
        if (_showingLiveStatus) ShowFrame(_lastFrameIds, _lastFrame);
    }

    /// <summary>Refreshes the instance picker from the runners the debugger knows about.</summary>
    public void OnRunnersChanged(
        System.Collections.Generic.IReadOnlyList<RunnerInfo> runners, long selected) {
        if (_instances == null) return;

        _instances.Clear();
        var matching = 0;

        foreach (var runner in runners) {
            if (Tree != null && runner.TreePath != Tree.ResourcePath) continue;

            _instances.AddItem(runner.ActorName);
            _instances.SetItemMetadata(_instances.ItemCount - 1, runner.Id);
            if (runner.Id == selected) _instances.Selected = _instances.ItemCount - 1;
            matching++;
        }

        _instances.Visible = matching > 0;

        // Nothing open but something is running: pick the running tree up instead of showing an
        // instance list over an empty canvas.
        if (Tree == null && runners.Count > 0) {
            _pendingAutoOpen = runners[0].TreePath;
            CallDeferred(MethodName.OpenPendingTree);
            return;
        }

        if (matching == 0 && runners.Count > 0) {
            SetStatus("The running game is not using this tree.", warning: true);
        }
        else if (matching > 1) {
            SetStatus($"{matching} running instances — colouring the selected one.");
        }
    }

    public void OpenPendingTree() {
        var path = _pendingAutoOpen;
        _pendingAutoOpen = null;

        if (string.IsNullOrEmpty(path) || Tree != null || !ResourceLoader.Exists(path)) return;

        var tree = ResourceLoader.Load<BehaviorTree>(path);
        if (tree != null) OpenTree(tree);
    }

    /// <summary>
    /// Selects the box for a node, typically because the editor started inspecting that resource.
    /// <para>
    /// The early-out when the box is already selected is what breaks the selection cycle: picking a
    /// box hands the node to the Inspector, the editor answers by editing it, the plugin routes
    /// that back here, and touching <c>Selected</c> again would emit another selection signal.
    /// </para>
    /// </summary>
    public void HighlightNode(MissNode node) {
        if (node == null || Tree == null || _syncingSelection) return;

        var box = Graph.BoxShowing(node.Id);
        if (box == null) return;

        // An entry of a list is shown by its row, the list itself by the box with no row picked.
        var entryId = ReferenceEquals(box.Node, node) ? "" : node.Id;
        if (box.Selected && box.SelectedEntry == entryId) return;

        _syncingSelection = true;
        foreach (var other in GetGraphBoxes()) other.Selected = ReferenceEquals(other, box);
        box.ShowEntryPicked(entryId);
        _syncingSelection = false;
    }

    System.Collections.Generic.IEnumerable<BehaviorTreeGraphNode> GetGraphBoxes() {
        foreach (var child in Graph.GetChildren()) {
            if (child is BehaviorTreeGraphNode box) yield return box;
        }
    }

    void ShowEmptyState() {
        _title.Text = "—";
        SetStatus("Open a BehaviorTree resource from the FileSystem dock to edit it.");
    }

    // ---- saving ------------------------------------------------------------------------------

    void MarkDirty() {
        if (Tree != null && !_unsavedTrees.Contains(Tree)) _unsavedTrees.Add(Tree);
        ShowDirtyState();
        SetStatus(Describe());
    }

    void ShowDirtyState() {
        var dirty = HasUnsavedChanges(Tree);
        _saveButton.Disabled = !dirty;
        // A tree without a file has nothing to go back to.
        _revertButton.Disabled = !dirty || string.IsNullOrEmpty(Tree?.ResourcePath);
        EmitSignal(SignalName.DirtyStateChanged, dirty);
    }

    public bool HasUnsavedChanges(BehaviorTree tree) => tree != null && _unsavedTrees.Contains(tree);

    /// <summary>Paths of every tree with unsaved edits, open or not.</summary>
    public string[] UnsavedTreePaths() => [.. UnsavedTrees().Select(t => string.IsNullOrEmpty(t.ResourcePath) ? "(unsaved tree)" : t.ResourcePath)];

    System.Collections.Generic.List<BehaviorTree> UnsavedTrees()
        => [.. _unsavedTrees.Select(t => t.AsGodotObject()).OfType<BehaviorTree>()];

    /// <summary>Saves every tree with unsaved edits — before the project runs, or when the editor saves.</summary>
    public void SaveUnsavedTrees() {
        foreach (var tree in UnsavedTrees()) SaveTree(tree);
    }

    void Save() => SaveTree(Tree);

    void RevertPressed() {
        if (!HasUnsavedChanges(Tree)) return;
        _revertDialog.DialogText = $"Discard all unsaved changes to {Tree.ResourcePath}?";
        _revertDialog.PopupCentered();
    }

    /// <summary>
    /// Puts the open tree back to its file, keeping the tree and node objects (see
    /// <see cref="TreeRevert"/>). Not an undo step of its own; undo still reaches the discarded edits.
    /// </summary>
    public void RevertTree() {
        var tree = Tree;
        if (tree == null) return;

        var error = TreeRevert.Revert(tree, Graph.KnownNode);
        if (error != Error.Ok) {
            SetStatus($"Reverting failed: {error}", warning: true);
            return;
        }

        _unsavedTrees.Remove(tree);
        Graph.LoadTree(tree);
        Blackboard.ShowSource(tree);
        // The Inspector may be showing one of the nodes; its values just changed underneath it.
        foreach (var node in tree.AllNodes()) node.NotifyPropertyListChanged();
        tree.NotifyPropertyListChanged();

        ShowDirtyState();
        SetStatus($"Discarded the unsaved changes to {tree.ResourcePath}");
    }

    void SaveTree(BehaviorTree tree) {
        if (tree == null) return;

        if (string.IsNullOrEmpty(tree.ResourcePath)) {
            SetStatus("This tree has no file yet — save it from the FileSystem dock first.", warning: true);
            return;
        }

        var error = ResourceSaver.Save(tree, tree.ResourcePath);
        if (error != Error.Ok) {
            SetStatus($"Saving {tree.ResourcePath} failed: {error}", warning: true);
            return;
        }

        _unsavedTrees.Remove(tree);
        ShowDirtyState();
        SetStatus($"Saved {tree.ResourcePath}");
    }

    void Validate() {
        if (Tree == null) return;

        var problems = Tree.Validate();
        if (problems.Length == 0) {
            SetStatus("No problems found.");
            return;
        }

        foreach (var problem in problems) GD.PushWarning($"missbehave: {problem}");
        SetStatus($"{problems.Length} problem(s) — see the Output panel.", warning: true);
    }

    // ---- inspector ---------------------------------------------------------------------------

    /// <summary>
    /// Selection signals arrive one per box, and a click emits a deselect for the old box and a
    /// select for the new one in no guaranteed order. So rather than reacting per signal, read the
    /// actual selection once the dust has settled.
    /// </summary>
    void OnSelectionMoved() {
        if (_syncingSelection) return;
        CallDeferred(MethodName.SyncInspector);
    }

    public void SyncInspector() {
        if (Tree == null || _syncingSelection) return;

        MissNode selected = null;
        foreach (var box in GetGraphBoxes()) {
            if (box.Selected && !box.IsRoot && box.Node != null) {
                selected = box.List?.Children.FirstOrDefault(e => e != null && e.Id == box.SelectedEntry) ?? box.Node;
                break;
            }
        }

        Resource target = selected;
        target ??= Tree;

        _syncingSelection = true;
        if (EditInInspector != null) EditInInspector(target);
        else if (Engine.IsEditorHint()) EditorInterface.Singleton.EditResource(target);
        _syncingSelection = false;
    }

    // ---- status ------------------------------------------------------------------------------

    string Describe() {
        if (Tree == null) return "No tree open.";
        var count = 0;
        foreach (var _ in Tree.AllNodes()) count++;
        var orphans = Tree.Orphans.Count;
        return orphans > 0 ? $"{count} nodes, {orphans} not connected" : $"{count} nodes";
    }

    void SetStatus(string message, bool warning = false) {
        if (_status == null) return;
        _status.Text = message;
        _status.AddThemeColorOverride("font_color",
            warning ? new Color("#e0b400") : new Color(1, 1, 1, 0.55f));
    }

    public override void _ShortcutInput(InputEvent @event) {
        if (Tree == null || !IsVisibleInTree()) return;
        if (@event is not InputEventKey { Pressed: true, Keycode: Key.S, CtrlPressed: true }) return;

        Save();
        AcceptEvent();
    }
}
#endif
