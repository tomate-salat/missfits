#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using Misscore;
using Misscore.Editor;

namespace Misswire.Editor;

/// <summary>
/// The graph of one <see cref="MissMediator"/>: a box per node (and per resource a node's property
/// holds) with the members it offers, and a box per binding in between. A wire from a property or
/// signal into a binding is its source; a wire from a step of a binding into a method or property
/// is what that step does. Frames group boxes.
/// <para>
/// A node may have more than one box, so wires need not cross the whole graph. Which box a wire
/// belongs to is the editor's business only: bindings and steps name it in fields the game ignores.
/// </para>
/// <para>
/// The graph lives in the scene, inside the mediator, so an edit replaces the mediator's
/// <c>Graph</c> with an edited copy through the editor's undo history: the scene is marked unsaved,
/// Ctrl+Z works, and nothing needs saving apart from the scene. The properties of a binding, a step
/// or a group are edited in the Inspector.
/// </para>
/// </summary>
[Tool]
public partial class MisswireGraphEdit : MissGraphEdit {
    /// <summary>An edit was refused, with a short message saying why.</summary>
    [Signal]
    public delegate void EditRejectedEventHandler(string reason);

    /// <summary>A binding, step or group was picked to be shown in the Inspector.</summary>
    [Signal]
    public delegate void PartPickedEventHandler(Resource part);

    internal const int MenuAddSelected = 0;
    internal const int MenuShowResource = 1;
    internal const int MenuAddMember = 2;
    internal const int MenuRemoveBox = 3;
    internal const int MenuEditorActive = 4;
    internal const int MenuPushInitial = 5;
    internal const int MenuDeleteBinding = 6;
    internal const int MenuStepUp = 7;
    internal const int MenuStepDown = 8;
    internal const int MenuDeleteStep = 9;
    internal const int MenuDeleteWire = 10;
    internal const int MenuDuplicateBox = 11;
    internal const int MenuGroupSelected = 12;
    internal const int MenuUngroup = 13;
    internal const int MenuLeaveGroup = 14;
    internal const int MenuFollowChanges = 15;
    internal const int MenuValuePort = 16;
    internal const int MenuAddConstant = 17;

    /// <summary>How far apart boxes added side by side are: room for a binding in between.</summary>
    const float ColumnWidth = 520;

    Node _mediator;
    GodotObject _builtGraph;
    string _builtShape = "";
    bool _rebuilding;
    bool _rebuildQueued;

    PopupMenu _menu;
    GodotObject _members;

    /// <summary>The window "Add a member..." opens.</summary>
    public WireMemberDialog Members => ReloadSafe.Get<WireMemberDialog>(ref _members);
    Vector2 _menuPosition;
    StringName _menuBox;
    string _menuKind;
    string _menuId;

    StringName _pickedBox;
    string _pickedKind = "";
    string _pickedId = "";
    GodotObject _pickedPart;

    public Node Mediator => IsInstanceValid(_mediator) ? _mediator : null;

    public MissBindingGraph Graph => Mediator is { } mediator ? MissEditorSync.Graph(mediator) : null;

    public override void _Ready() {
        base._Ready();
        RightDisconnects = false;
        ShowArrangeButton = false;
        MinimapEnabled = false;

        _menu = new PopupMenu { Name = "Menu" };
        AddChild(_menu, false, InternalMode.Back);
        _menu.Connect(PopupMenu.SignalName.IdPressed, new Callable(this, MethodName.OnMenuIdPressed));
        var members = new WireMemberDialog { Name = "Members" };
        _members = members;
        AddChild(members, false, InternalMode.Back);
        members.Connect(WireMemberDialog.SignalName.MembersChosen, new Callable(this, MethodName.OnMembersChosen));

        Wire(GraphEdit.SignalName.ConnectionRequest, MethodName.OnConnectionRequest);
        Wire(GraphEdit.SignalName.ConnectionToEmpty, MethodName.OnConnectionToEmpty);
        Wire(GraphEdit.SignalName.DisconnectionRequest, MethodName.OnDisconnectionRequest);
        Wire(GraphEdit.SignalName.ConnectionDragEnded, MethodName.OnConnectionDragEnded);
        Wire(GraphEdit.SignalName.PopupRequest, MethodName.OnPopupRequest);
        Wire(GraphEdit.SignalName.DeleteNodesRequest, MethodName.OnDeleteNodesRequest);
        Wire(GraphEdit.SignalName.EndNodeMove, MethodName.OnEndNodeMove);
        Wire(GraphEdit.SignalName.NodeSelected, MethodName.OnNodeSelected);
        Wire(GraphEdit.SignalName.GraphElementsLinkedToFrameRequest, MethodName.OnLinkedToFrame);
        Wire(GraphEdit.SignalName.DuplicateNodesRequest, MethodName.DuplicateSelected);
    }

    /// <summary>Ctrl+A adds members to the one selected box. (Ctrl+D is GraphEdit's own duplicate request.)</summary>
    public override void _GuiInput(InputEvent @event) {
        base._GuiInput(@event);
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.A } key || !key.IsCommandOrControlPressed()) return;
        var selected = NodeBoxes().Where(b => b.Selected).ToList();
        if (selected.Count == 1) OpenMembers(selected[0]);
        else Reject("select one box to add members to");
        AcceptEvent();
    }

    /// <summary>
    /// Copies the selected boxes next to themselves: a node box as a further box of its node
    /// showing the same rows, a binding with its steps, a constant with its value.
    /// </summary>
    public void DuplicateSelected() {
        var graph = Graph;
        var selected = GetChildren().OfType<WireBox>().Where(b => b.Selected).ToList();
        if (graph == null || selected.Count == 0) return;
        var offset = new Vector2(40, 40);

        Commit(selected.Count == 1 ? "Misswire: duplicate box" : $"Misswire: duplicate {selected.Count} boxes", g => {
            foreach (var box in selected) {
                switch (box) {
                    case WireNodeBox node:
                        var info = InfoOf(g, RefOf(node));
                        var copy = new MissGraphBoxInfo {
                            Id = BlackboardEntry.NewId(), Node = node.Path, Property = node.Property,
                            Position = node.PositionOffset + offset, ShowDeclared = info?.ShowDeclared ?? true,
                        };
                        // Rows the box shows because something uses them are kept as rows added by hand.
                        foreach (var row in node.Rows()) {
                            if (!copy.Rows.Contains($"{row.Kind}:{row.Id}")) copy.Rows.Add($"{row.Kind}:{row.Id}");
                        }
                        g.Boxes.Add(copy);
                        break;
                    case WireBindingBox bindingBox when Find(g, bindingBox.BindingId) is { } binding:
                        var twin = (MissBinding) binding.DuplicateDeep(Resource.DeepDuplicateMode.Internal);
                        twin.Id = BlackboardEntry.NewId();
                        twin.GraphPosition = bindingBox.PositionOffset + offset;
                        g.Bindings.Add(twin);
                        break;
                    case WireConstantBox constantBox when g.Constants.FirstOrDefault(c => c != null && c.Id == constantBox.ConstantId) is { } constant:
                        g.Constants.Add(new MissGraphConstant { Value = constant.Value, GraphPosition = constantBox.PositionOffset + offset });
                        break;
                }
            }
        });
    }

    void Wire(StringName signal, StringName method) => Connect(signal, new Callable(this, method));

    // ---- boxes and where wires belong ----------------------------------------------------------

    /// <summary>
    /// A box of a node: which node, which resource inside it (a property path, or empty for the node
    /// itself), and which of its boxes (an id, or empty for the main one).
    /// </summary>
    public readonly record struct BoxRef(string Node, string Property, string Id) {
        public string Name => Id == "" ? "N" + Convert.ToHexString(Encoding.UTF8.GetBytes($"{Node}|{Property}")) : "D" + Id;
        public BoxRef Main => this with { Id = "" };
    }

    static string BindingBoxName(string bindingId) => "W" + bindingId;
    static string FrameName(string groupId) => "G" + groupId;

    public IEnumerable<WireNodeBox> NodeBoxes() => GetChildren().OfType<WireNodeBox>();
    public IEnumerable<WireBindingBox> BindingBoxes() => GetChildren().OfType<WireBindingBox>();
    public IEnumerable<WireGroupFrame> Frames() => GetChildren().OfType<WireGroupFrame>();

    /// <summary>The main box of a node, or of a resource inside it.</summary>
    public WireNodeBox NodeBox(string node, string property = "") => BoxNamed(new BoxRef(node, property ?? "", "").Name);

    WireNodeBox BoxNamed(string name) => NodeBoxes().FirstOrDefault(b => b.Name == name);

    public WireBindingBox BindingBox(string bindingId) => BindingBoxes().FirstOrDefault(b => b.BindingId == bindingId);

    public WireGroupFrame Frame(string groupId) => Frames().FirstOrDefault(f => f.GroupId == groupId);

    static BoxRef RefOf(WireNodeBox box) => new(box.Path, box.Property, box.InfoId);

    /// <summary>A further box only counts while it exists for that node; otherwise the main box stands in.</summary>
    static string Resolve(MissBindingGraph graph, string node, string property, string id) =>
        id != "" && graph.Boxes.Any(i => i != null && i.Id == id && i.Node.ToString() == node && i.Property.ToString() == property) ? id : "";

    /// <summary>The box and row a binding's source wire leaves.</summary>
    static (BoxRef Box, string Kind, string Member) SourceOf(MissBindingGraph graph, MissBinding binding) {
        var node = binding.Source.ToString();
        string property, kind, member;
        switch (binding.Kind) {
            case MissSourceKind.Property:
                var path = binding.Property.ToString();
                var cut = path.LastIndexOf(':');
                (property, kind, member) = cut < 0 ? ("", WireMember.Property, path) : (path[..cut], WireMember.Property, path[(cut + 1)..]);
                break;
            case MissSourceKind.Signal:
                (property, kind, member) = ("", WireMember.Signal, binding.Signal.ToString());
                break;
            default:
                (property, kind, member) = (binding.Property.ToString(), WireMember.Signal, binding.Signal.ToString());
                break;
        }
        return (new BoxRef(node, property, Resolve(graph, node, property, binding.SourceBox)), kind, member);
    }

    /// <summary>The box and row a step's wire ends at.</summary>
    static (BoxRef Box, string Kind, string Member) TargetOf(MissBindingGraph graph, MissBindingStep step) {
        var node = MissWiring.NodePart(step.Target).ToString();
        var property = step.Target.GetSubNameCount() == 0 ? "" : step.Target.GetConcatenatedSubNames();
        var kind = step.Kind == MissStepKind.Call ? WireMember.Method : WireMember.Property;
        return (new BoxRef(node, property, Resolve(graph, node, property, step.TargetBox)), kind, step.Member.ToString());
    }

    /// <summary>The box and property row a step's value wire leaves, or null if its value comes from elsewhere.</summary>
    static (BoxRef Box, string Row)? ValueOf(MissBindingGraph graph, MissBindingStep step) {
        if (step.ValueFrom != MissStepValue.Port || step.ValueConstant != "" || step.ValueSource.GetSubNameCount() == 0) return null;
        var node = MissWiring.NodePart(step.ValueSource).ToString();
        var path = step.ValueSource.GetConcatenatedSubNames();
        var cut = path.LastIndexOf(':');
        var property = cut < 0 ? "" : path[..cut];
        return (new BoxRef(node, property, Resolve(graph, node, property, step.ValueBox)), cut < 0 ? path : path[(cut + 1)..]);
    }

    static string ConstantBoxName(string constantId) => "C" + constantId;

    static MissGraphBoxInfo InfoOf(MissBindingGraph graph, BoxRef box) => graph.Boxes.FirstOrDefault(i => i != null && (box.Id != ""
        ? i.Id == box.Id
        : i.Id == "" && i.Node.ToString() == box.Node && i.Property.ToString() == box.Property));

    /// <summary>What a box stands for: the node, or the resource its property path leads to.</summary>
    GodotObject ObjectOf(BoxRef box) {
        var node = box.Node == "" ? null : Mediator?.GetNodeOrNull(box.Node);
        return box.Property == "" ? node : node?.GetIndexed(box.Property).AsGodotObject();
    }

    // ---- opening and building --------------------------------------------------------------

    public void Open(Node mediator) {
        if (mediator == _mediator && Graph == _builtGraph) return;
        _mediator = mediator;
        _pickedBox = null;
        _pickedPart = null;
        RebuildGraph();
    }

    /// <summary>
    /// Brings the graph up to date after an edit anywhere — this graph's own, the Inspector's, an
    /// undo. Rebuilds only when something it shows changed.
    /// </summary>
    public void SyncWithGraph(bool force = false) {
        if (Mediator == null && _builtGraph == null) return;
        if (force || Graph != _builtGraph || Shape(Graph) != _builtShape) QueueRebuild();
    }

    public void QueueRebuild() {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        CallDeferred(MethodName.RebuildGraph);
    }

    void ClearGraph() {
        ClearConnections();
        // RemoveChild before QueueFree, or a new box of the same name would be renamed "<name>@2".
        foreach (var element in GetChildren().OfType<GraphElement>().ToList()) {
            RemoveChild(element);
            element.QueueFree();
        }
    }

    /// <summary>
    /// Rebuilds every box and wire. Never call this straight out of a GraphEdit signal handler —
    /// freeing the boxes GraphEdit is working with crashes the editor; use <see cref="QueueRebuild"/>.
    /// </summary>
    public void RebuildGraph() {
        _rebuildQueued = false;
        _rebuilding = true;
        var selected = GetChildren().OfType<GraphElement>().Where(b => b.Selected).Select(b => b.Name.ToString()).ToHashSet();
        ClearGraph();

        var mediator = Mediator;
        var graph = Graph;
        _builtGraph = graph;
        _builtShape = Shape(graph);
        if (mediator != null && graph != null) Build(mediator, graph);

        UpdateFlips();
        foreach (var element in GetChildren().OfType<GraphElement>()) element.Selected = selected.Contains(element.Name);
        _rebuilding = false;
        RestorePicked();
    }

    /// <summary>What the graph shows, in one string; positions included, so moves done by undo show too.</summary>
    static string Shape(MissBindingGraph graph) {
        if (graph == null) return "";
        var text = new StringBuilder();
        foreach (var b in graph.Bindings.Where(b => b != null)) {
            text.Append($"{b.Id}|{b.Kind}|{b.Source}|{b.Property}|{b.Signal}|{b.ChangeSignal}|{b.PushInitial}|{b.FollowChanges}|{b.EditorActive}|{b.EditorTrigger}|{b.GraphPosition}|{b.SourceBox}|{string.Join(",", b.Triggers)}");
            foreach (var s in b.Steps.Where(s => s != null)) {
                text.Append($"[{s.Kind}|{s.Target}|{s.Member}|{s.TargetBox}|{s.ValueFrom}|{s.ValueSource}|{s.ValueConstant}|{s.ValueBox}|{string.Join(",", s.Arguments.Select(a => a == null ? "-" : $"{a.Source}{a.Index}{a.Constant}"))}]");
            }
            text.Append('\n');
        }
        foreach (var c in graph.Constants.Where(c => c != null)) text.Append($"{c.Id}|{c.Value.VariantType}|{GD.VarToStr(c.Value)}|{c.GraphPosition}\n");
        foreach (var i in graph.Boxes.Where(i => i != null)) text.Append($"{i.Id}|{i.Node}|{i.Property}|{i.Position}|{i.ShowDeclared}|{string.Join(",", i.Rows)}\n");
        foreach (var g in graph.Groups.Where(g => g != null)) text.Append($"{g.Id}|{g.Title}|{g.Color}|{string.Join(",", g.Members)}\n");
        return text.ToString();
    }

    void Build(Node mediator, MissBindingGraph graph) {
        // The boxes in the order they appear, and the rows each shows because something uses them.
        var boxes = new List<BoxRef>();
        var used = new Dictionary<string, List<(string Kind, string Name)>>();
        void Box(BoxRef box) {
            if (box.Node != "" && !boxes.Contains(box)) boxes.Add(box);
        }
        void Use(BoxRef box, string kind, string name) {
            Box(box);
            if (!used.TryGetValue(box.Name, out var list)) used[box.Name] = list = [];
            if (!string.IsNullOrEmpty(name) && !list.Contains((kind, name))) list.Add((kind, name));
        }

        foreach (var info in graph.Boxes.Where(i => i != null)) {
            var box = new BoxRef(info.Node.ToString(), info.Property.ToString(), info.Id);
            Box(box);
            foreach (var row in info.Rows.Select(r => r.ToString())) {
                var kind = row.Split(':')[0];
                Use(box, kind, row[(kind.Length + 1)..]);
            }
        }
        foreach (var binding in graph.Bindings.Where(b => b != null && !b.Source.IsEmpty)) {
            foreach (var trigger in binding.Kind == MissSourceKind.Property ? binding.Triggers : []) {
                if (trigger.GetSubNameCount() > 0) Use(new BoxRef(MissWiring.NodePart(trigger).ToString(), "", ""), WireMember.Signal, trigger.GetConcatenatedSubNames());
            }
            var (box, kind, member) = SourceOf(graph, binding);
            Use(box, kind, member);
            // A resource's box comes with its node's, showing where the resource is held.
            if (box.Property != "") Use(box.Main with { Property = "" }, WireMember.Property, box.Property.Split(':')[0]);
        }
        foreach (var step in graph.Bindings.Where(b => b != null).SelectMany(b => b.Steps).Where(s => s != null && !s.Target.IsEmpty)) {
            var (box, kind, member) = TargetOf(graph, step);
            Use(box, kind, member);
            if (ValueOf(graph, step) is var (valueBox, row)) Use(valueBox, WireMember.Property, row);
        }

        var spot = 0;
        foreach (var reference in boxes) {
            var box = new WireNodeBox { Name = reference.Name };
            AddChild(box);
            var info = InfoOf(graph, reference);
            BuildNodeBox(box, reference, info?.ShowDeclared ?? true, used.GetValueOrDefault(reference.Name) ?? []);
            box.Connect(WireBox.SignalName.RowPicked, new Callable(this, MethodName.OnRowPicked));
            box.Connect(WireBox.SignalName.RowMenuRequested, new Callable(this, MethodName.OnRowMenu));
            TrackMoves(box);
            box.PositionOffset = info != null ? info.Position : new Vector2(40 + ColumnWidth * (spot % 2), 40 + 260 * (spot / 2));
            spot++;
        }

        foreach (var constant in graph.Constants.Where(c => c != null)) {
            var box = new WireConstantBox { Name = ConstantBoxName(constant.Id) };
            AddChild(box);
            box.Build(constant, ConstantNode(mediator, constant)?.Name.ToString() ?? "");
            box.Connect(WireConstantBox.SignalName.ValueEdited, new Callable(this, MethodName.SetConstant));
            box.Connect(WireConstantBox.SignalName.NodePicked, new Callable(this, MethodName.PickConstantNode));
            box.Connect(WireBox.SignalName.RowMenuRequested, new Callable(this, MethodName.OnRowMenu));
            TrackMoves(box);
            box.PositionOffset = constant.GraphPosition;
        }

        spot = 0;
        foreach (var binding in graph.Bindings.Where(b => b != null)) {
            var box = new WireBindingBox { Name = BindingBoxName(binding.Id) };
            AddChild(box);
            BuildBindingBox(box, mediator, binding);
            box.Connect(WireBox.SignalName.RowPicked, new Callable(this, MethodName.OnRowPicked));
            box.Connect(WireBox.SignalName.RowMenuRequested, new Callable(this, MethodName.OnRowMenu));
            TrackMoves(box);
            box.PositionOffset = binding.GraphPosition != Vector2.Zero ? binding.GraphPosition : new Vector2(ColumnWidth / 2, 40 + 200 * spot);
            spot++;
        }

        foreach (var binding in graph.Bindings.Where(b => b != null)) {
            var box = BindingBox(binding.Id);
            foreach (var trigger in binding.Kind == MissSourceKind.Property ? binding.Triggers : []) {
                if (BoxNamed(new BoxRef(MissWiring.NodePart(trigger).ToString(), "", "").Name) is { } emitter
                    && emitter.OutputPort(WireMember.Signal, trigger.GetConcatenatedSubNames()) is var port and >= 0) {
                    ConnectNode(emitter.Name, port, box.Name, box.InputPort(WireBindingBox.Trigger, ""));
                }
            }
            if (!binding.Source.IsEmpty) {
                var (source, kind, member) = SourceOf(graph, binding);
                if (BoxNamed(source.Name) is { } from && from.OutputPort(kind, member) is var port and >= 0) ConnectNode(from.Name, port, box.Name, 0);
            }
            for (var i = 0; i < binding.Steps.Count; i++) {
                if (binding.Steps[i] is not { } step || step.Target.IsEmpty) continue;
                var (target, kind, member) = TargetOf(graph, step);
                if (BoxNamed(target.Name) is { } to && to.InputPort(kind, member) is var input and >= 0) ConnectNode(box.Name, i, to.Name, input);

                var valuePort = box.InputPort(WireBindingBox.Value, i.ToString());
                if (valuePort < 0) continue;
                if (ValueOf(graph, step) is var (valueBox, row) && BoxNamed(valueBox.Name) is { } from && from.OutputPort(WireMember.Property, row) is var output and >= 0) {
                    ConnectNode(from.Name, output, box.Name, valuePort);
                }
                else if (step.ValueConstant != "" && GetNodeOrNull<WireConstantBox>(ConstantBoxName(step.ValueConstant)) is { } constant) {
                    ConnectNode(constant.Name, 0, box.Name, valuePort);
                }
            }
        }

        foreach (var group in graph.Groups.Where(g => g != null)) {
            var members = group.Members.Where(m => GetNodeOrNull<GraphElement>(m) != null).ToList();
            if (members.Count == 0) continue;
            var frame = new WireGroupFrame { Name = FrameName(group.Id) };
            AddChild(frame);
            // Frames lie beneath the boxes.
            MoveChild(frame, 0);
            frame.Build(group);
            frame.Connect(WireGroupFrame.SignalName.MenuRequested, new Callable(this, MethodName.OnFrameMenu));
            foreach (var member in members) AttachGraphElementToFrame(member, frame.Name);
        }
        PlaceWireOverlay();
    }

    void BuildNodeBox(WireNodeBox box, BoxRef reference, bool showDeclared, List<(string Kind, string Name)> used) {
        var node = Mediator?.GetNodeOrNull(reference.Node);
        var isResource = reference.Property != "";
        var obj = ObjectOf(reference);
        var rows = !showDeclared ? [] : isResource ? WireMembers.ResourceOffer(obj) : WireMembers.Declared(node);
        foreach (var (kind, name) in used) {
            if (rows.Any(r => r.Kind == kind && r.Name == name)) continue;
            rows.Add(WireMembers.Find(obj, kind, name) ?? new WireMember(kind, name, 0, false));
        }
        rows = [.. rows.OrderBy(r => r.Kind switch { WireMember.Property => 0, WireMember.Signal => 1, _ => 2 })];
        // A resource class without [GlobalClass] has no name Godot knows; the script of the one held now does.
        rows = [.. rows.Select(r => r.Kind == WireMember.Property && r.HoldsResource && obj?.Get(r.Name).AsGodotObject() is { } held
            && held.GetScript().As<Script>() != null ? r with { Type = TypeName(held) } : r)];

        string title, subtitle, warning = "";
        if (node == null) {
            (title, subtitle, warning) = (reference.Node, "missing", $"no node at '{reference.Node}'");
        }
        else if (isResource) {
            title = $"{node.Name}.{reference.Property.Replace(':', '.')}";
            subtitle = obj == null ? "empty" : $"{TypeName(obj)}{(MissEditorSync.Runs(obj) ? "" : " · no [Tool]")}";
        }
        else {
            title = node.Name;
            subtitle = $"{TypeName(node)}{(MissEditorSync.Runs(node) ? "" : " · no [Tool]")}";
        }
        if (reference.Id != "") subtitle += " · another box";

        box.Build(reference.Node, reference.Property, reference.Id, title, subtitle, warning, rows.Select(r => (r, MissingWarning(obj, r))));
    }

    static string MissingWarning(GodotObject obj, WireMember member) =>
        obj == null || WireMembers.Find(obj, member.Kind, member.Name) != null ? "" : $"'{member.Name}' no longer exists";

    /// <summary>The script's class name, or its file name for a class without one, or the engine class.</summary>
    static string TypeName(GodotObject obj) {
        if (obj.GetScript().As<Script>() is not { } script) return obj.GetClass();
        var name = script.GetGlobalName().ToString();
        return !string.IsNullOrEmpty(name) ? name : script.ResourcePath.GetFile().GetBaseName();
    }

    void BuildBindingBox(WireBindingBox box, Node mediator, MissBinding binding) {
        var source = MissWiring.SourceNode(mediator, binding);
        var sourceName = source?.Name.ToString() ?? binding.Source.ToString();
        var property = binding.Property.ToString().Replace(':', '.');
        var title = binding.Source.IsEmpty ? "(no source)" : binding.Kind switch {
            MissSourceKind.Property => $"on {sourceName}.{property}",
            MissSourceKind.Signal => $"on {sourceName} » {binding.Signal}",
            _ => $"on {sourceName}.{property} » {binding.Signal}",
        };
        var flags = new List<string>();
        if (binding.EditorActive) flags.Add(binding.EditorTrigger == MissEditorTrigger.EveryEdit ? "editor, every edit" : "editor");
        if (binding.PushInitial && binding.Kind != MissSourceKind.Signal) flags.Add("first value");
        if (binding.Kind == MissSourceKind.Property && !binding.FollowChanges) flags.Add("game: first value only");
        else if (binding.Kind == MissSourceKind.Property && MissWiring.ChangeSignalOf(source, binding) is { IsEmpty: false } changed) {
            flags.Add(MissWiring.IsNested(binding) ? $"on {changed} or changed" : $"on {changed}");
        }
        var sourceColor = binding.Kind == MissSourceKind.Property ? WireBox.PropertyColor : WireBox.SignalColor;

        var graph = Graph;
        var steps = binding.Steps.Select(step => {
            if (step == null) return new WireStepView("(empty)", "(empty)", "", WireBox.MethodColor, false);
            var target = MissWiring.TargetOf(mediator, step);
            var name = target is Node node ? node.Name.ToString()
                : mediator.GetNodeOrNull(MissWiring.NodePart(step.Target)) is { } holder ? $"{holder.Name}.{step.Target.GetConcatenatedSubNames().Replace(':', '.')}"
                : step.Target.ToString();
            var takes = WireMembers.ParametersOf(target, step).Count > 0;
            var (value, isVariable) = takes ? ValueText(mediator, graph, binding, step) : ("", false);
            var shown = isVariable ? $"[b]{WireMember.Escape(value)}[/b]" : WireMember.Escape(value);
            var (text, rich) = step.Kind == MissStepKind.Call
                ? ($"{name}.{step.Member}({value})", $"{WireMember.Escape($"{name}.{step.Member}")}({shown})")
                : ($"{name}.{step.Member} = {value}", $"{WireMember.Escape($"{name}.{step.Member}")} = {shown}");
            return new WireStepView(text, rich, StepProblem(mediator, graph, binding, step, target),
                step.Kind == MissStepKind.Call ? WireBox.MethodColor : WireBox.PropertyColor, step.ValueFrom == MissStepValue.Port);
        });
        string triggers = null;
        if (binding.Kind == MissSourceKind.Property) {
            var names = binding.Triggers.Select(t => $"{mediator.GetNodeOrNull(MissWiring.NodePart(t))?.Name ?? t.GetConcatenatedNames()} » {t.GetConcatenatedSubNames()}").ToList();
            if (binding.FollowChanges && MissWiring.ChangeSignalOf(source, binding) is { IsEmpty: false } auto) names.Insert(0, $"{auto} (auto)");
            triggers = names.Count == 0 ? "set off by: drag signals in" : $"set off by: {string.Join(", ", names)}";
        }
        box.Build(binding.Id, title, string.Join(" · ", flags), SourceProblem(binding, source), sourceColor, steps, triggers);
    }

    /// <summary>
    /// How a step's line shows its value: the binding's value or a wired property by name — a
    /// variable, shown bold — a constant as it is written, or <c>?</c> for nothing.
    /// </summary>
    static (string Text, bool Variable) ValueText(Node mediator, MissBindingGraph graph, MissBinding binding, MissBindingStep step) {
        if (step.Arguments.Count > 0) return ("set in the Inspector", false);
        if (step.ValueFrom == MissStepValue.Binding) {
            var inputs = WireMembers.InputNames(mediator, binding);
            return inputs.Count > 0 ? (inputs[0], true) : ("?", false);
        }
        if (step.ValueConstant != "") {
            if (graph?.Constants.FirstOrDefault(c => c != null && c.Id == step.ValueConstant) is not { } constant) return ("?", false);
            return constant.Value.VariantType == Variant.Type.NodePath ? (ConstantNode(mediator, constant)?.Name.ToString() ?? "?", false) : (GD.VarToStr(constant.Value), false);
        }
        if (step.ValueSource.IsEmpty) return ("?", false);
        var holder = mediator.GetNodeOrNull(MissWiring.NodePart(step.ValueSource))?.Name.ToString() ?? step.ValueSource.GetConcatenatedNames();
        return ($"{holder}.{step.ValueSource.GetConcatenatedSubNames().Replace(':', '.')}", true);
    }

    /// <summary>What keeps a binding's source from working as one would expect, or empty.</summary>
    static string SourceProblem(MissBinding binding, Node source) {
        if (binding.Source.IsEmpty) return "no source: drag a wire from a property or signal in here";
        if (source == null) return $"no node at '{binding.Source}'";
        var changed = MissWiring.ChangeSignalOf(source, binding);
        var first = binding.Property.ToString().Split(':')[0];
        if (binding.Kind == MissSourceKind.Property && changed.IsEmpty && binding.FollowChanges && binding.Triggers.Count == 0 && !MissWiring.IsNested(binding))
            return $"no change signal ({first}Changed): in the game the steps only get the first value";
        if (binding.Kind == MissSourceKind.Property && changed.IsEmpty && binding.FollowChanges && binding.Triggers.Count == 0)
            return $"no change signal ({first}Changed): in the game a new {first} goes unnoticed, only changes inside it count";
        if (binding.Kind == MissSourceKind.Signal && binding.EditorActive && !MissEditorSync.Runs(source))
            return $"'{source.Name}' has no [Tool], so its signal never fires in the editor";
        var emitter = binding.Kind == MissSourceKind.ResourceSignal ? MissWiring.HeldResource(source, binding) : source;
        var signal = binding.Kind == MissSourceKind.Property ? changed : binding.Signal;
        if (emitter != null && !signal.IsEmpty && MissSignalRelay.ArgumentCount(emitter, signal) < 0) return $"no signal '{signal}'";
        return "";
    }

    /// <summary>What keeps a step from running, or empty.</summary>
    static string StepProblem(Node mediator, MissBindingGraph graph, MissBinding binding, MissBindingStep step, GodotObject target) {
        if (target == null) return $"nothing at '{step.Target}'";
        var name = MissSignalRelay.Describe(target);
        var parameters = WireMembers.ParametersOf(target, step);
        if (step.Kind == MissStepKind.Set && parameters.Count == 0) return $"no property '{step.Member}'";
        if (step.Kind == MissStepKind.Call && MissWiring.ParameterCount(target, step.Member) == null) return $"no method '{step.Member}'";
        var required = parameters.Count(p => p.Default == null);
        if (step.Kind == MissStepKind.Call && required > 1) return $"'{step.Member}' needs {required} values; a mediator passes one";
        if (step.Kind == MissStepKind.Call && binding.EditorActive && !MissEditorSync.Runs(target)) return $"{name} has no [Tool], so this runs only in the game";
        if (step.Arguments.Count > 0 || (step.Kind == MissStepKind.Call && required == 0 && step.ValueFrom == MissStepValue.Binding)) return "";

        if (parameters.Count == 0) return step.ValueFrom == MissStepValue.Port ? $"'{step.Member}' takes no value" : "";
        var wanted = parameters[0];
        if (step.ValueFrom == MissStepValue.Port) {
            if (step.ValueConstant != "") {
                return graph?.Constants.FirstOrDefault(c => c != null && c.Id == step.ValueConstant) is { } constant
                    ? Mismatch(constant.Value.VariantType == Variant.Type.NodePath
                        ? new WireParameter("", Variant.Type.Object, ConstantNode(mediator, constant)?.GetClass() ?? "", null)
                        : new WireParameter("", constant.Value.VariantType, "", null), wanted)
                    : "its constant is gone";
            }
            if (step.ValueSource.IsEmpty) return "nothing is wired into the value port";
            return ValueType(mediator, step.ValueSource) is { } wired ? Mismatch(wired, wanted) : $"nothing at '{step.ValueSource}'";
        }
        var inputs = InputTypes(mediator, binding);
        if (inputs == null) return "";
        return inputs.Count == 0 ? "the binding hands on no value: right-click → Value via port" : Mismatch(inputs[0], wanted);
    }

    /// <summary>Empty if a value of one type can go into the other, else what to do about it.</summary>
    static string Mismatch(WireParameter value, WireParameter wanted) {
        if (Fits(value, wanted)) return "";
        var hint = "right-click → Value via port";
        return $"gets {value.TypeName}, needs {wanted.TypeName}: {hint}";
    }

    static bool Fits(WireParameter value, WireParameter wanted) {
        if (wanted.Type == Variant.Type.Nil || value.Type == Variant.Type.Nil || value.Type == wanted.Type) return true;
        static bool Number(Variant.Type t) => t is Variant.Type.Int or Variant.Type.Float;
        static bool Text(Variant.Type t) => t is Variant.Type.String or Variant.Type.StringName;
        return (Number(value.Type) && Number(wanted.Type)) || (Text(value.Type) && Text(wanted.Type));
    }

    /// <summary>The types of what a binding hands its steps, or null if that is not known.</summary>
    static List<WireParameter> InputTypes(Node mediator, MissBinding binding) {
        if (MissWiring.SourceNode(mediator, binding) is not { } source) return null;
        if (binding.Kind == MissSourceKind.Property) {
            return ValueType(mediator, $"{mediator.GetPathTo(source)}:{binding.Property}") is { } type ? [type] : null;
        }
        var emitter = binding.Kind == MissSourceKind.ResourceSignal ? MissWiring.HeldResource(source, binding) : source;
        if (emitter == null) return null;
        foreach (var entry in emitter.GetSignalList()) {
            if (entry["name"].AsStringName() != binding.Signal) continue;
            return [.. entry["args"].AsGodotArray().Select(a => a.AsGodotDictionary())
                .Select(a => new WireParameter(a["name"].AsString(), (Variant.Type) a["type"].AsInt32(), a["class_name"].AsString(), null))];
        }
        return null;
    }

    /// <summary>The type of the property a path leads to (<c>../Model:Outfit:Hair</c>), or null if it leads nowhere.</summary>
    static WireParameter? ValueType(Node mediator, NodePath path) {
        if (path.GetSubNameCount() == 0 || mediator.GetNodeOrNull(MissWiring.NodePart(path)) is not { } node) return null;
        var names = path.GetConcatenatedSubNames().Split(':');
        GodotObject holder = names.Length == 1 ? node : node.GetIndexed(string.Join(':', names[..^1])).AsGodotObject();
        if (holder == null) return null;
        foreach (var entry in holder.GetPropertyList()) {
            if (entry["name"].AsString() == names[^1]) return new WireParameter(names[^1], (Variant.Type) entry["type"].AsInt32(), entry["class_name"].AsString(), null);
        }
        return null;
    }

    // ---- picking -----------------------------------------------------------------------------

    void OnNodeSelected(Node node) {
        if (_rebuilding || node is not (WireBindingBox or WireGroupFrame) || _pickedBox == node.Name) return;
        Pick(node.Name, "", "");
    }

    void OnRowPicked(StringName boxName, string kind, string id) => Pick(boxName, kind, id);

    void Pick(StringName boxName, string kind, string id) {
        _pickedBox = boxName;
        _pickedKind = kind;
        _pickedId = id;
        _pickedPart = null;
        RestorePicked();
    }

    /// <summary>Marks the picked row and hands what it stands for to the Inspector, unless it is there already.</summary>
    void RestorePicked() {
        foreach (var box in GetChildren().OfType<WireBox>()) box.ShowPicked(box.Name == _pickedBox ? _pickedKind : null, _pickedId);
        var graph = Graph;
        Resource part = null;
        switch (GetNodeOrNull(_pickedBox?.ToString() ?? "")) {
            case WireBindingBox picked when graph != null:
                var binding = Find(graph, picked.BindingId);
                part = _pickedKind == WireBindingBox.Step && int.TryParse(_pickedId, out var index) && binding != null && index < binding.Steps.Count
                    ? binding.Steps[index]
                    : binding;
                break;
            case WireGroupFrame frame when graph != null:
                part = graph.Groups.FirstOrDefault(g => g != null && g.Id == frame.GroupId);
                break;
        }
        if (part == null || part == _pickedPart) return;
        _pickedPart = part;
        EmitSignal(SignalName.PartPicked, part);
    }

    /// <summary>Whether a resource belongs to the graph on show: the plugin keeps the panel up while it is inspected.</summary>
    public bool Owns(GodotObject obj) {
        if (obj == null || Graph is not { } graph) return false;
        if (obj == graph) return true;
        foreach (var binding in graph.Bindings.Where(b => b != null)) {
            if (obj == binding || binding.Steps.Any(s => s == obj || (s != null && s.Arguments.Any(a => a == obj)))) return true;
        }
        return graph.Boxes.Any(i => i == obj) || graph.Groups.Any(g => g == obj) || graph.Constants.Any(c => c == obj);
    }

    // ---- wiring ------------------------------------------------------------------------------

    // A wire is taken off by grabbing its end at the input and dragging it away: GraphEdit reports
    // that as a disconnection, then carries on dragging from the output. Dropped on another input,
    // the wire is led there instead; dropped anywhere else, it is gone. So the disconnection is only
    // noted here and settled when the drag ends.

    StringName _detachFrom;
    int _detachPort;
    StringName _detachTo;
    int _detachToPort;

    void OnDisconnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort) {
        (_detachFrom, _detachPort, _detachTo, _detachToPort) = (fromNode, (int) fromPort, toNode, (int) toPort);
    }

    bool Detaching(StringName fromNode, long fromPort) => _detachFrom != null && _detachFrom == fromNode && _detachPort == fromPort;

    void OnConnectionDragEnded() {
        if (_detachFrom == null) return;
        // Deferred: the request that may lead the wire elsewhere is handled first.
        CallDeferred(MethodName.SettleDetach);
    }

    void SettleDetach() {
        if (_detachFrom == null) return;
        var (from, port, to, toPort) = (_detachFrom, _detachPort, _detachTo, _detachToPort);
        _detachFrom = null;
        DeleteWire(from, port, to, toPort);
    }

    /// <summary>
    /// Takes a wire away: a step's wire takes the step with it, a source's wire leaves the binding
    /// without a source, and a value's wire leaves the step's port empty.
    /// </summary>
    internal void DeleteWire(StringName fromNode, int fromPort, StringName toNode, int toPort) {
        switch (GetNodeOrNull<WireBox>(fromNode.ToString()), GetNodeOrNull<WireBox>(toNode.ToString())) {
            case (WireBindingBox binding, WireNodeBox):
                EditStep(binding.BindingId, fromPort, MenuDeleteStep);
                break;
            case (WireNodeBox source, WireBindingBox binding) when binding.AtInput(toPort) is (WireBindingBox.Trigger, _):
                var (_, signal) = source.AtOutput(fromPort);
                var path = new NodePath($"{source.Path}:{signal}");
                Commit("Misswire: take the trigger away", graph => Find(graph, binding.BindingId)?.Triggers.Remove(path));
                break;
            case (WireNodeBox or WireConstantBox, WireBindingBox binding) when binding.AtInput(toPort) is (WireBindingBox.Value, var step):
                Commit("Misswire: take the value away", graph => {
                    if (Find(graph, binding.BindingId)?.Steps.ElementAtOrDefault(int.Parse(step)) is { } s) {
                        (s.ValueSource, s.ValueConstant, s.ValueBox) = (new NodePath(), "", "");
                    }
                });
                break;
            case (WireNodeBox, WireBindingBox binding):
                Commit("Misswire: take the source away", graph => {
                    if (Find(graph, binding.BindingId) is not { } b) return;
                    (b.Source, b.Property, b.Signal, b.ChangeSignal, b.SourceBox) = (new NodePath(), new StringName(), new StringName(), new StringName(), "");
                });
                break;
        }
    }

    void OnConnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort) {
        var from = GetNodeOrNull<WireBox>(fromNode.ToString());
        var to = GetNodeOrNull<WireBox>(toNode.ToString());
        if (Detaching(fromNode, fromPort)) {
            // Led back where it was, or a step led to another input: nothing is left to take away.
            var back = toNode == _detachTo && toPort == _detachToPort;
            if (back || from is WireBindingBox) _detachFrom = null;
            if (back) return;
        }
        var into = to is WireBindingBox target0 ? target0.AtInput((int) toPort) : (null, null);
        switch (from, to) {
            case (WireNodeBox source, WireBindingBox binding) when into.Kind == WireBindingBox.Trigger:
                AddTrigger(binding.BindingId, source, (int) fromPort);
                break;
            case (WireNodeBox source, WireBindingBox binding) when into.Kind == WireBindingBox.Value:
                SetStepValue(binding.BindingId, int.Parse(into.Id), source, (int) fromPort);
                break;
            case (WireConstantBox constant, WireBindingBox binding) when into.Kind == WireBindingBox.Value:
                SetStepConstant(binding.BindingId, int.Parse(into.Id), constant.ConstantId);
                break;
            case (WireConstantBox, _):
                Reject("a constant goes into a step's value port");
                break;
            case (WireNodeBox source, WireBindingBox binding):
                SetSource(binding.BindingId, source, (int) fromPort);
                break;
            case (WireNodeBox source, WireNodeBox target):
                AddBinding(source, (int) fromPort, target, (int) toPort, BesideOf(source));
                break;
            case (WireBindingBox binding, WireNodeBox target):
                SetStep(binding.BindingId, (int) fromPort, target, (int) toPort);
                break;
            default:
                Reject("a wire runs from a property or signal to a binding, and from a step to a method or property");
                break;
        }
    }

    /// <summary>
    /// Where a new binding goes when it is not dropped somewhere: right of its source, below the
    /// bindings already there.
    /// </summary>
    Vector2 BesideOf(WireNodeBox source) {
        var at = source.PositionOffset + new Vector2(source.Size.X + 60, 0);
        var size = new Vector2(260, 140);
        for (var tries = 0; tries < 40; tries++) {
            var spot = new Rect2(at, size);
            if (!GetChildren().OfType<GraphNode>().Any(b => new Rect2(b.PositionOffset, b.Size).Intersects(spot))) break;
            at.Y += 150;
        }
        return at;
    }

    void OnConnectionToEmpty(StringName fromNode, long fromPort, Vector2 releasePosition) {
        // A wire pulled off its input and let go: it is to go, not to start a new binding.
        if (Detaching(fromNode, fromPort)) return;
        if (GetNodeOrNull<WireBox>(fromNode.ToString()) is not WireNodeBox source) return;
        AddBinding(source, (int) fromPort, null, -1, (releasePosition + ScrollOffset) / Zoom);
    }

    /// <summary>A binding with its source set from a row of a box, or null if the row is no source.</summary>
    static MissBinding SourceFrom(WireNodeBox box, int port) {
        var (kind, name) = box.AtOutput(port);
        if (kind == null) return null;
        var binding = new MissBinding { Source = box.Path, SourceBox = box.InfoId };
        switch (kind) {
            case WireMember.Property:
                binding.Kind = MissSourceKind.Property;
                binding.Property = box.Property == "" ? name : $"{box.Property}:{name}";
                break;
            case WireMember.Signal when box.Property != "":
                binding.Kind = MissSourceKind.ResourceSignal;
                binding.Property = box.Property;
                binding.Signal = name;
                break;
            default:
                binding.Kind = MissSourceKind.Signal;
                binding.Signal = name;
                break;
        }
        // A signal has no value to start with, and fires in the editor only from a [Tool] node.
        binding.EditorActive = binding.Kind != MissSourceKind.Signal;
        binding.PushInitial = binding.Kind != MissSourceKind.Signal;
        return binding;
    }

    /// <summary>A step doing what a row of a box offers: calling the method, or setting the property.</summary>
    static MissBindingStep StepTo(WireNodeBox box, int port) {
        var (kind, name) = box.AtInput(port);
        if (kind == null) return null;
        return new MissBindingStep {
            Kind = kind == WireMember.Method ? MissStepKind.Call : MissStepKind.Set,
            Target = box.Property == "" ? box.Path : $"{box.Path}:{box.Property}",
            Member = name,
            TargetBox = box.InfoId,
        };
    }

    void AddBinding(WireNodeBox source, int fromPort, WireNodeBox target, int toPort, Vector2 at) {
        if (SourceFrom(source, fromPort) is not { } binding) return;
        binding.GraphPosition = at;
        if (target != null) {
            if (StepTo(target, toPort) is not { } step) {
                Reject("a step calls a method or sets a property");
                return;
            }
            binding.Steps.Add(step);
        }
        Commit($"Misswire: wire {Describe(binding)}", graph => graph.Bindings.Add(binding));
        Pick(BindingBoxName(binding.Id), "", "");
    }

    void SetSource(string bindingId, WireNodeBox source, int fromPort) {
        if (SourceFrom(source, fromPort) is not { } wanted) return;
        Commit($"Misswire: source {Describe(wanted)}", graph => {
            if (Find(graph, bindingId) is not { } binding) return;
            (binding.Kind, binding.Source, binding.Property, binding.Signal, binding.ChangeSignal, binding.SourceBox) =
                (wanted.Kind, wanted.Source, wanted.Property, wanted.Signal, wanted.ChangeSignal, wanted.SourceBox);
        });
    }

    void SetStep(string bindingId, int fromPort, WireNodeBox target, int toPort) {
        if (StepTo(target, toPort) is not { } wanted) {
            Reject("a step calls a method or sets a property");
            return;
        }
        Commit($"Misswire: step {wanted.Member}", graph => {
            if (Find(graph, bindingId) is not { } binding) return;
            if (fromPort >= 0 && fromPort < binding.Steps.Count && binding.Steps[fromPort] is { } step) {
                (step.Kind, step.Target, step.Member, step.TargetBox) = (wanted.Kind, wanted.Target, wanted.Member, wanted.TargetBox);
            }
            else binding.Steps.Add(wanted);
        });
    }

    /// <summary>Wires a signal into a property binding's triggers: it reads the property again and runs the steps.</summary>
    void AddTrigger(string bindingId, WireNodeBox source, int fromPort) {
        var (kind, name) = source.AtOutput(fromPort);
        if (kind != WireMember.Signal || source.Property != "") {
            Reject("a trigger is a signal of a node");
            return;
        }
        var path = new NodePath($"{source.Path}:{name}");
        Commit($"Misswire: trigger {name}", graph => {
            if (Find(graph, bindingId) is { } binding && !binding.Triggers.Contains(path)) binding.Triggers.Add(path);
        });
    }

    /// <summary>Wires a property into a step's value port: the step reads it whenever it runs.</summary>
    void SetStepValue(string bindingId, int stepIndex, WireNodeBox source, int fromPort) {
        var (kind, name) = source.AtOutput(fromPort);
        if (kind != WireMember.Property) {
            Reject("a value port takes a property or a constant; a signal sets a binding off instead");
            return;
        }
        var path = source.Property == "" ? $"{source.Path}:{name}" : $"{source.Path}:{source.Property}:{name}";
        Commit($"Misswire: value {name}", graph => {
            if (Find(graph, bindingId)?.Steps.ElementAtOrDefault(stepIndex) is not { } step) return;
            (step.ValueFrom, step.ValueSource, step.ValueConstant, step.ValueBox) = (MissStepValue.Port, path, "", source.InfoId);
        });
    }

    void SetStepConstant(string bindingId, int stepIndex, string constantId) => Commit("Misswire: value from a constant", graph => {
        if (Find(graph, bindingId)?.Steps.ElementAtOrDefault(stepIndex) is not { } step) return;
        (step.ValueFrom, step.ValueSource, step.ValueConstant, step.ValueBox) = (MissStepValue.Port, new NodePath(), constantId, "");
    });

    /// <summary>Switches a step between the binding's value and its value port.</summary>
    internal void SetValueFrom(string bindingId, int stepIndex, MissStepValue from) => Commit(
        from == MissStepValue.Port ? "Misswire: value via port" : "Misswire: value from the binding", graph => {
            if (Find(graph, bindingId)?.Steps.ElementAtOrDefault(stepIndex) is not { } step) return;
            step.ValueFrom = from;
            if (from == MissStepValue.Binding) (step.ValueSource, step.ValueConstant, step.ValueBox) = (new NodePath(), "", "");
        });

    /// <summary>Adds a constant box, a text to begin with.</summary>
    public string AddConstant(Vector2 at) {
        var constant = new MissGraphConstant { GraphPosition = at };
        Commit("Misswire: constant", graph => graph.Constants.Add(constant));
        return constant.Id;
    }

    /// <summary>The node a constant of type Node points to, or null.</summary>
    static Node ConstantNode(Node mediator, MissGraphConstant constant) =>
        mediator != null && constant.Value.VariantType == Variant.Type.NodePath && !constant.Value.AsNodePath().IsEmpty
            ? mediator.GetNodeOrNull(constant.Value.AsNodePath())
            : null;

    /// <summary>
    /// A node was picked for a constant of type Node. The node selector gives the path from the
    /// scene's root; the constant keeps it from the mediator, like every path in the graph.
    /// </summary>
    internal void PickConstantNode(string constantId, NodePath path) {
        var mediator = Mediator;
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (mediator == null) return;
        var node = root?.GetNodeOrNull(path) ?? (path.IsAbsolute() ? GetNodeOrNull(path) : null);
        if (node == null) {
            Reject($"no node at '{path}'");
            return;
        }
        if (node != root && node.Owner != root) {
            Reject($"'{node.Name}' belongs to an instanced scene; pick it inside that scene");
            return;
        }
        SetConstant(constantId, mediator.GetPathTo(node));
    }

    internal void SetConstant(string constantId, Variant value) => Commit("Misswire: constant value", graph => {
        if (graph.Constants.FirstOrDefault(c => c != null && c.Id == constantId) is { } constant) constant.Value = value;
    });

    static void DeleteConstant(MissBindingGraph graph, string constantId) {
        if (graph.Constants.FirstOrDefault(c => c != null && c.Id == constantId) is { } constant) graph.Constants.Remove(constant);
        foreach (var step in graph.Bindings.Where(b => b != null).SelectMany(b => b.Steps).Where(s => s != null && s.ValueConstant == constantId)) {
            step.ValueConstant = "";
        }
        foreach (var group in graph.Groups.Where(g => g != null)) group.Members.Remove(ConstantBoxName(constantId));
    }

    static MissBinding Find(MissBindingGraph graph, string bindingId) => graph.Bindings.FirstOrDefault(b => b != null && b.Id == bindingId);

    static string Describe(MissBinding binding) => binding.Kind switch {
        MissSourceKind.Property => binding.Property,
        MissSourceKind.Signal => binding.Signal,
        _ => $"{binding.Property}.{binding.Signal}",
    };

    void Reject(string reason) => EmitSignal(SignalName.EditRejected, reason);

    // ---- adding and removing boxes -----------------------------------------------------------

    /// <summary>
    /// Adds a box for each node that is not shown yet. With <paramref name="again"/> a node that
    /// already has one gets a further box instead — what dragging it in once more means. Only nodes
    /// of the mediator's own scene can be wired.
    /// </summary>
    public void AddNodes(IEnumerable<Node> nodes, Vector2 at, bool again = false) {
        var mediator = Mediator;
        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (mediator == null || root == null) return;

        var paths = new List<(string Path, bool Another)>();
        foreach (var node in nodes) {
            if (node == null || node == mediator) continue;
            if (node != root && node.Owner != root) {
                Reject($"'{node.Name}' belongs to an instanced scene; wire it inside that scene");
                continue;
            }
            var path = mediator.GetPathTo(node).ToString();
            var shown = NodeBox(path) != null;
            if ((!shown || again) && paths.All(p => p.Path != path)) paths.Add((path, shown));
        }
        if (paths.Count == 0) return;

        Commit(paths.Count == 1 ? $"Misswire: show {paths[0].Path}" : $"Misswire: show {paths.Count} nodes", graph => {
            var offset = 0;
            foreach (var (path, another) in paths) {
                graph.Boxes.Add(new MissGraphBoxInfo {
                    Id = another ? BlackboardEntry.NewId() : "", Node = path, ShowDeclared = !another,
                    Position = at + new Vector2(ColumnWidth * offset, 0),
                });
                offset++;
            }
        });
    }

    /// <summary>
    /// Shows the resource a property of a box holds as a box of its own, with what it offers:
    /// <paramref name="property"/> of the node, or of the resource the box already stands for.
    /// </summary>
    public void ShowResource(BoxRef holder, string property) {
        var path = holder.Property == "" ? property : $"{holder.Property}:{property}";
        if (NodeBox(holder.Node, path) != null) return;
        var below = BoxNamed(holder.Name);
        var at = (below?.PositionOffset ?? Vector2.Zero) + new Vector2(0, (below?.Size.Y ?? 120) + 30);
        Commit($"Misswire: show {path.Replace(':', '.')}", graph => graph.Boxes.Add(new MissGraphBoxInfo { Node = holder.Node, Property = path, Position = at }));
    }

    /// <summary>A further box of the same node or resource, starting empty, to wire from or to somewhere else in the graph.</summary>
    public void DuplicateBox(BoxRef box) {
        var from = BoxNamed(box.Name);
        var at = (from?.PositionOffset ?? Vector2.Zero) + new Vector2(0, (from?.Size.Y ?? 120) + 30);
        Commit("Misswire: another box", graph => graph.Boxes.Add(new MissGraphBoxInfo {
            Id = BlackboardEntry.NewId(), Node = box.Node, Property = box.Property, Position = at, ShowDeclared = false,
        }));
    }

    public void AddRowToBox(BoxRef box, string kind, string name) => AddRowsToBox(box, [$"{kind}:{name}"]);

    /// <summary>Shows members (<c>kind:name</c>) in a box even while nothing uses them.</summary>
    public void AddRowsToBox(BoxRef box, IReadOnlyCollection<string> rows) {
        if (rows.Count == 0) return;
        Commit(rows.Count == 1 ? $"Misswire: show {rows.First().Split(':')[^1]}" : $"Misswire: show {rows.Count} members", graph => {
            var info = InfoOf(graph, box);
            if (info == null) {
                graph.Boxes.Add(info = new MissGraphBoxInfo {
                    Id = box.Id, Node = box.Node, Property = box.Property, Position = BoxNamed(box.Name)?.PositionOffset ?? Vector2.Zero,
                });
            }
            foreach (var row in rows) {
                if (!info.Rows.Contains(row)) info.Rows.Add(row);
            }
        });
    }

    /// <summary>Whether a wire of any binding ends at this box.</summary>
    static bool InUse(MissBindingGraph graph, string boxName) => graph.Bindings.Where(b => b != null).Any(b =>
        (!b.Source.IsEmpty && SourceOf(graph, b).Box.Name == boxName)
        || b.Steps.Any(s => s != null && ((!s.Target.IsEmpty && TargetOf(graph, s).Box.Name == boxName) || ValueOf(graph, s)?.Box.Name == boxName)));

    static void RemoveBox(MissBindingGraph graph, BoxRef box) {
        if (InfoOf(graph, box) is { } info) graph.Boxes.Remove(info);
        foreach (var group in graph.Groups.Where(g => g != null)) group.Members.Remove(box.Name);
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) =>
        data.VariantType == Variant.Type.Dictionary && data.AsGodotDictionary() is var d && d.ContainsKey("type") && d["type"].AsString() == "nodes";

    public override void _DropData(Vector2 atPosition, Variant data) {
        var nodes = data.AsGodotDictionary()["nodes"].AsGodotArray().Select(p => GetNodeOrNull(p.AsNodePath())).Where(n => n != null);
        AddNodes(nodes, (atPosition + ScrollOffset) / Zoom, again: true);
    }

    // ---- groups ------------------------------------------------------------------------------

    IEnumerable<string> SelectedBoxNames() => GetChildren().OfType<WireBox>().Where(b => b.Selected).Select(b => b.Name.ToString());

    /// <summary>Puts the boxes in a new group, taking them out of any other.</summary>
    public void Group(IEnumerable<string> boxNames) {
        var names = boxNames.ToList();
        if (names.Count == 0) {
            Reject("select the boxes to group first");
            return;
        }
        var group = new MissGraphGroupInfo();
        foreach (var name in names) group.Members.Add(name);
        Commit("Misswire: group", graph => {
            foreach (var other in graph.Groups.Where(g => g != null)) {
                foreach (var name in names) other.Members.Remove(name);
            }
            graph.Groups.Add(group);
        });
        Pick(FrameName(group.Id), "", "");
    }

    /// <summary>Boxes dropped onto a frame join its group.</summary>
    void OnLinkedToFrame(Godot.Collections.Array elements, StringName frameName) {
        if (GetNodeOrNull<WireGroupFrame>(frameName.ToString()) is not { } frame) return;
        var names = elements.Select(e => e.AsStringName().ToString()).Where(n => GetNodeOrNull<WireBox>(n) != null).ToList();
        if (names.Count == 0) return;
        Commit("Misswire: into the group", graph => {
            foreach (var group in graph.Groups.Where(g => g != null)) {
                foreach (var name in names) group.Members.Remove(name);
                if (group.Id == frame.GroupId) {
                    foreach (var name in names) group.Members.Add(name);
                }
            }
        });
    }

    internal void Ungroup(string groupId) => Commit("Misswire: ungroup", graph => {
        if (graph.Groups.FirstOrDefault(g => g != null && g.Id == groupId) is { } group) graph.Groups.Remove(group);
    });

    internal void LeaveGroup(string boxName) => Commit("Misswire: out of the group", graph => {
        foreach (var group in graph.Groups.Where(g => g != null)) group.Members.Remove(boxName);
    });

    static MissGraphGroupInfo GroupOf(MissBindingGraph graph, string boxName) => graph?.Groups.FirstOrDefault(g => g != null && g.Members.Contains(boxName));

    // ---- menus -------------------------------------------------------------------------------

    StringName _menuWireFrom;
    int _menuWirePort;
    StringName _menuWireTo;
    int _menuWireToPort;

    void OnPopupRequest(Vector2 atPosition) {
        _menuPosition = (atPosition + ScrollOffset) / Zoom;
        _menuBox = null;
        _menu.Clear();
        var wire = GetClosestConnectionAtPoint(atPosition, WireReach);
        if (wire.Count > 0) {
            (_menuWireFrom, _menuWirePort, _menuWireTo, _menuWireToPort) = (wire["from_node"].AsStringName(), wire["from_port"].AsInt32(), wire["to_node"].AsStringName(), wire["to_port"].AsInt32());
            _menu.AddItem("Delete wire", MenuDeleteWire);
        }
        _menu.AddItem("Add the selected nodes", MenuAddSelected);
        _menu.AddItem("Add a constant", MenuAddConstant);
        _menu.SetItemDisabled(_menu.ItemCount - 1, Mediator == null);
        if (SelectedBoxNames().Any()) _menu.AddItem("Group the selected boxes", MenuGroupSelected);
        Popup(_menu, GetScreenPosition() + atPosition);
    }

    void OnFrameMenu(StringName frameName, Vector2 screenPosition) {
        _menuBox = frameName;
        _menu.Clear();
        _menu.AddItem("Ungroup", MenuUngroup);
        Popup(_menu, screenPosition);
    }

    void OnRowMenu(StringName boxName, string kind, string id, Vector2 screenPosition) {
        _menuBox = boxName;
        _menuKind = kind;
        _menuId = id;
        _menu.Clear();

        var graph = Graph;
        switch (GetNodeOrNull<WireBox>(boxName.ToString())) {
            case WireNodeBox box:
                if (kind == WireMember.Property && WireMembers.Find(ObjectOf(RefOf(box)), kind, id) is { HoldsResource: true }) {
                    _menu.AddItem($"Open {id} as a box", MenuShowResource);
                }
                _menu.AddItem("Add a member...", MenuAddMember);
                _menu.AddItem("Another box of this", MenuDuplicateBox);
                _menu.AddItem("Remove box", MenuRemoveBox);
                _menu.SetItemDisabled(_menu.ItemCount - 1, graph == null || InUse(graph, box.Name));
                break;
            case WireBindingBox box when graph != null && Find(graph, box.BindingId) is { } binding:
                if (kind == WireBindingBox.Step) {
                    _menu.AddItem("Move up", MenuStepUp);
                    _menu.AddItem("Move down", MenuStepDown);
                    _menu.AddItem("Delete step", MenuDeleteStep);
                    if (int.TryParse(id, out var stepIndex) && binding.Steps.ElementAtOrDefault(stepIndex) is { } picked) {
                        _menu.AddItem(picked.ValueFrom == MissStepValue.Port ? "Value from the binding" : "Value via port", MenuValuePort);
                    }
                }
                else {
                    _menu.AddCheckItem("Active in the editor", MenuEditorActive);
                    _menu.SetItemChecked(_menu.ItemCount - 1, binding.EditorActive);
                    _menu.AddCheckItem("Send the first value", MenuPushInitial);
                    _menu.SetItemChecked(_menu.ItemCount - 1, binding.PushInitial);
                    if (binding.Kind == MissSourceKind.Property) {
                        var follows = MissWiring.ChangeSignalOf(MissWiring.SourceNode(Mediator, binding), binding);
                        _menu.AddCheckItem(follows.IsEmpty ? "Follow changes in the game" : $"Follow {follows} in the game", MenuFollowChanges);
                        _menu.SetItemChecked(_menu.ItemCount - 1, binding.FollowChanges);
                    }
                    _menu.AddItem("Delete binding", MenuDeleteBinding);
                }
                break;
            default:
                return;
        }
        if (SelectedBoxNames().Count() > 1 || (SelectedBoxNames().Any() && GroupOf(graph, boxName) == null)) _menu.AddItem("Group the selected boxes", MenuGroupSelected);
        if (GroupOf(graph, boxName) != null) _menu.AddItem("Out of the group", MenuLeaveGroup);
        Popup(_menu, screenPosition);
    }

    static void Popup(PopupMenu menu, Vector2 screenPosition) {
        menu.ResetSize();
        menu.Position = (Vector2I) screenPosition;
        menu.Popup();
    }

    void OnMenuIdPressed(long id) {
        var element = _menuBox == null ? null : GetNodeOrNull(_menuBox.ToString());
        switch (id) {
            case MenuDeleteWire:
                DeleteWire(_menuWireFrom, _menuWirePort, _menuWireTo, _menuWireToPort);
                break;
            case MenuAddConstant:
                AddConstant(_menuPosition);
                break;
            case MenuValuePort when element is WireBindingBox box && int.TryParse(_menuId, out var index) && Find(Graph, box.BindingId)?.Steps.ElementAtOrDefault(index) is { } step:
                SetValueFrom(box.BindingId, index, step.ValueFrom == MissStepValue.Port ? MissStepValue.Binding : MissStepValue.Port);
                break;
            case MenuAddSelected:
                AddNodes(EditorInterface.Singleton.GetSelection().GetSelectedNodes(), _menuPosition);
                break;
            case MenuGroupSelected:
                Group(SelectedBoxNames());
                break;
            case MenuUngroup when element is WireGroupFrame frame:
                Ungroup(frame.GroupId);
                break;
            case MenuLeaveGroup when element != null:
                LeaveGroup(element.Name);
                break;
            case MenuShowResource when element is WireNodeBox box:
                ShowResource(RefOf(box), _menuId);
                break;
            case MenuAddMember when element is WireNodeBox box:
                OpenMembers(box);
                break;
            case MenuDuplicateBox when element is WireNodeBox box:
                DuplicateBox(RefOf(box));
                break;
            case MenuRemoveBox when element is WireNodeBox box:
                Commit("Misswire: remove box", graph => RemoveBox(graph, RefOf(box)));
                break;
            case MenuEditorActive or MenuPushInitial or MenuFollowChanges or MenuDeleteBinding when element is WireBindingBox box:
                EditBinding(box.BindingId, id);
                break;
            case MenuStepUp or MenuStepDown or MenuDeleteStep when element is WireBindingBox box && int.TryParse(_menuId, out var index):
                EditStep(box.BindingId, index, id);
                break;
        }
    }

    internal void EditBinding(string bindingId, long id) {
        Commit(id switch { MenuEditorActive => "Misswire: editor on/off", MenuPushInitial => "Misswire: first value on/off", MenuFollowChanges => "Misswire: follow changes on/off", _ => "Misswire: delete binding" }, graph => {
            if (Find(graph, bindingId) is not { } binding) return;
            switch (id) {
                case MenuEditorActive: binding.EditorActive = !binding.EditorActive; break;
                case MenuFollowChanges: binding.FollowChanges = !binding.FollowChanges; break;
                case MenuPushInitial: binding.PushInitial = !binding.PushInitial; break;
                default: DeleteBinding(graph, binding); break;
            }
        });
    }

    static void DeleteBinding(MissBindingGraph graph, MissBinding binding) {
        graph.Bindings.Remove(binding);
        foreach (var group in graph.Groups.Where(g => g != null)) group.Members.Remove(BindingBoxName(binding.Id));
    }

    internal void EditStep(string bindingId, int index, long id) {
        Commit(id == MenuDeleteStep ? "Misswire: delete step" : "Misswire: reorder steps", graph => {
            if (Find(graph, bindingId) is not { } binding || index < 0 || index >= binding.Steps.Count) return;
            var step = binding.Steps[index];
            var to = id switch { MenuStepUp => index - 1, MenuStepDown => index + 1, _ => -1 };
            binding.Steps.RemoveAt(index);
            if (id != MenuDeleteStep) binding.Steps.Insert(Math.Clamp(to, 0, binding.Steps.Count), step);
        });
    }

    string _membersBox = "";

    /// <summary>Opens the window that picks members to show in a box, grouped by the class that brings them.</summary>
    public void OpenMembers(WireNodeBox box) {
        var groups = WireMembers.ByClass(ObjectOf(RefOf(box)));
        if (groups.Count == 0) {
            Reject("there is nothing to show this box from");
            return;
        }
        _membersBox = box.Name;
        Members.Open($"Add to {box.Title}", groups, box.Rows().Select(r => $"{r.Kind}:{r.Id}").ToHashSet());
    }

    void OnMembersChosen(string[] choices) {
        if (BoxNamed(_membersBox) is { } box) AddRowsToBox(RefOf(box), choices);
    }

    // ---- deleting and moving -------------------------------------------------------------------

    void OnDeleteNodesRequest(Godot.Collections.Array<StringName> names) {
        var graph = Graph;
        if (graph == null) return;
        var bindings = names.Select(n => GetNodeOrNull<WireBindingBox>(n.ToString())).Where(b => b != null).Select(b => b.BindingId).ToList();
        var boxes = names.Select(n => GetNodeOrNull<WireNodeBox>(n.ToString())).Where(b => b != null).Select(RefOf).ToList();
        var groups = names.Select(n => GetNodeOrNull<WireGroupFrame>(n.ToString())).Where(f => f != null).Select(f => f.GroupId).ToList();
        var constants = names.Select(n => GetNodeOrNull<WireConstantBox>(n.ToString())).Where(c => c != null).Select(c => c.ConstantId).ToList();
        if (bindings.Count == 0 && boxes.Count == 0 && groups.Count == 0 && constants.Count == 0) return;

        Commit("Misswire: delete", g => {
            foreach (var id in bindings) {
                if (Find(g, id) is { } binding) DeleteBinding(g, binding);
            }
            foreach (var box in boxes) {
                if (!InUse(g, box.Name)) RemoveBox(g, box);
            }
            foreach (var id in constants) DeleteConstant(g, id);
            foreach (var id in groups) {
                if (g.Groups.FirstOrDefault(x => x != null && x.Id == id) is { } group) g.Groups.Remove(group);
            }
        });
        if (boxes.Any(b => InUse(Graph, b.Name))) Reject("a box stays while a binding uses it");
    }

    /// <summary>
    /// Keeps where every box is now, whichever moved — a frame takes its boxes along, and none of
    /// them is selected then.
    /// </summary>
    void OnEndNodeMove() {
        var graph = Graph;
        if (graph == null) return;
        var moved = new List<(StringName Name, Vector2 At)>();
        foreach (var box in GetChildren().OfType<WireBox>()) {
            var stored = box switch {
                WireBindingBox b => Find(graph, b.BindingId)?.GraphPosition,
                WireNodeBox n => InfoOf(graph, RefOf(n))?.Position,
                WireConstantBox c => graph.Constants.FirstOrDefault(x => x != null && x.Id == c.ConstantId)?.GraphPosition,
                _ => null,
            };
            if (stored != box.PositionOffset) moved.Add((box.Name, box.PositionOffset));
        }
        if (moved.Count == 0) return;

        Commit("Misswire: move", g => {
            foreach (var (name, at) in moved) {
                switch (GetNodeOrNull<WireBox>(name.ToString())) {
                    case WireBindingBox box when Find(g, box.BindingId) is { } binding:
                        binding.GraphPosition = at;
                        break;
                    case WireConstantBox box when g.Constants.FirstOrDefault(x => x != null && x.Id == box.ConstantId) is { } constant:
                        constant.GraphPosition = at;
                        break;
                    case WireNodeBox box:
                        var reference = RefOf(box);
                        var info = InfoOf(g, reference);
                        if (info == null) g.Boxes.Add(info = new MissGraphBoxInfo { Id = reference.Id, Node = reference.Node, Property = reference.Property });
                        info.Position = at;
                        break;
                }
            }
        });
    }

    // ---- committing --------------------------------------------------------------------------

    /// <summary>
    /// Applies an edit to a copy of the graph and swaps the copy in through the editor's undo
    /// history, so the scene is marked unsaved and Ctrl+Z puts the old graph back.
    /// </summary>
    void Commit(string actionName, Action<MissBindingGraph> mutate) {
        var mediator = Mediator;
        if (mediator == null) return;
        var current = Graph;
        var copy = current == null ? new MissBindingGraph() : (MissBindingGraph) current.DuplicateDeep(Resource.DeepDuplicateMode.Internal);
        mutate(copy);

        var history = EditorInterface.Singleton.GetEditorUndoRedo();
        history.CreateAction(actionName, UndoRedo.MergeMode.Disable, mediator);
        history.AddDoProperty(mediator, MissMediator.PropertyName.Graph, copy);
        history.AddUndoProperty(mediator, MissMediator.PropertyName.Graph, current);
        history.CommitAction();
        QueueRebuild();
    }
}
#endif
