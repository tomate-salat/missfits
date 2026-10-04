#if TOOLS
using System.Linq;
using Godot;
using Misscore.Editor;
using Misscore;

namespace Missbehave.Editor;

/// <summary>
/// One box in the graph. Wraps a definition node, or stands in for the tree's entry point when
/// <see cref="IsRoot"/> is set.
/// <para>
/// The tree grows top to bottom: a node's parent connects into the port on top of its box, its
/// children hang off the one at the bottom. GraphNode itself only knows ports on the left and right,
/// so those still exist — GraphEdit keeps its connections by them — but are drawn invisibly, and the
/// visible ports are drawn here. <see cref="BehaviorTreeGraphEdit"/> moves the wires and the grab
/// zones to match.
/// </para>
/// <para>
/// Icon, name and order badge share one row; GraphNode's own title bar is left empty. A box is
/// therefore one line tall, plus a small second line when the node has a summary.
/// </para>
/// </summary>
[Tool]
public partial class BehaviorTreeGraphNode : GraphNode {
    /// <summary>The box was right-clicked; the position is in screen coordinates, ready for a popup.</summary>
    [Signal]
    public delegate void ContextMenuRequestedEventHandler(StringName boxName, Vector2 screenPosition);

    /// <summary>A click picked an entry of a list — or the list itself, with an empty id.</summary>
    [Signal]
    public delegate void EntryPickedEventHandler(StringName boxName, string entryId);

    /// <summary>An entry row of a list was right-clicked; the position is in screen coordinates.</summary>
    [Signal]
    public delegate void EntryContextMenuRequestedEventHandler(StringName boxName, string entryId, Vector2 screenPosition);

    GodotObject _node;

    /// <summary>Stored untyped so an assembly reload can restore it — see <see cref="ReloadSafe"/>.</summary>
    public MissNode Node => ReloadSafe.Get<MissNode>(ref _node);
    public bool IsRoot { get; private set; }

    public const string RootName = "__root__";

    const string RootIcon = "res://addons/missbehave/icons/tree.svg";

    TextureRect _icon;
    TextureRect _modeIcon;
    Label _name;
    Label _warning;
    Label _order;
    Label _summary;
    VBoxContainer _entries;
    Control _entriesPad;
    Control _entriesTopPad;

    /// <summary>True while a running game streams statuses for this graph.</summary>
    bool _live;
    MissStatus? _status;

    /// <summary>Live status per entry id of a list, for the entries that were ticked.</summary>
    Godot.Collections.Dictionary<string, int> _entryStatuses = new();

    /// <summary>The entry of a list shown in the Inspector, or empty for the list itself.</summary>
    public string SelectedEntry { get; private set; } = "";

    public AListNode List => Node as AListNode;

    /// <summary>Category shown by shape, or null for the root entry.</summary>
    string Group => IsRoot || Node == null ? null : NodeTypeRegistry.GroupOf(Node.GetType());

    /// <summary>Room below a list's last entry, so the rows do not sit on the box's bottom edge.</summary>
    const float ListBottomPadding = 6f;

    /// <summary>Room between the rule under a list's header and its first entry.</summary>
    const float ListTopPadding = 4f;

    /// <summary>Radius of a drawn port, in graph units.</summary>
    public const float PortRadius = 5f;

    /// <summary>Replaces GraphNode's own port icon, so the left/right ports it still keeps stay invisible.</summary>
    static ImageTexture _noPort;

    /// <summary>Whether a parent can connect into this box, i.e. whether it has a port on top.</summary>
    public bool HasInput => !IsRoot;

    /// <summary>Whether children can hang off this box, i.e. whether it has a port at the bottom.</summary>
    public bool HasOutput => IsRoot || Node?.MaxChildren > 0;

    /// <summary>Where the top port sits, relative to the box and unscaled like GraphNode's own port positions.</summary>
    public Vector2 InputAnchor => new(Size.X / 2f, 0f);

    /// <summary>Where the bottom port sits, relative to the box and unscaled.</summary>
    public Vector2 OutputAnchor => new(Size.X / 2f, Size.Y);

    public void Bind(MissNode node, bool isRoot) {
        _node = node;
        IsRoot = isRoot;
        Name = isRoot ? RootName : node.Id;

        BuildUi();
        Refresh();
    }

    void BuildUi() {
        if (_name != null) return;

        // Slot 0 is this row. Its left/right ports only exist for GraphEdit's bookkeeping.
        if (_noPort == null) {
            var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            _noPort = ImageTexture.CreateFromImage(image);
        }
        AddThemeIconOverride("port", _noPort);
        Connect(Control.SignalName.Resized, new Callable(this, CanvasItem.MethodName.QueueRedraw));

        var row = new HBoxContainer { Name = "Row", CustomMinimumSize = new Vector2(0, 22) };
        row.AddThemeConstantOverride("separation", 6);

        _icon = new TextureRect {
            Name = "Icon",
            CustomMinimumSize = new Vector2(16, 16),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        // A list shows its mode beside its kind: condition icon, then selector or sequence.
        _modeIcon = new TextureRect {
            Name = "ModeIcon",
            CustomMinimumSize = new Vector2(16, 16),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Visible = false,
        };
        _name = new Label { Name = "NodeName", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _warning = new Label { Name = "Warning", Text = "" };
        _warning.AddThemeColorOverride("font_color", new Color("#ffd24a"));
        _order = new Label { Name = "Order", Text = "" };
        _order.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.45f));
        _order.AddThemeFontSizeOverride("font_size", 11);

        row.AddChild(_icon);
        row.AddChild(_modeIcon);
        row.AddChild(_name);
        row.AddChild(_warning);
        row.AddChild(_order);
        AddChild(row);

        _summary = new Label { Name = "Summary", AutowrapMode = TextServer.AutowrapMode.Off };
        _summary.AddThemeFontSizeOverride("font_size", 11);
        _summary.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.55f));
        AddChild(_summary);

        // Room between the rule under the header and the first entry; the rule sits above this pad.
        _entriesTopPad = new Control {
            Name = "EntriesTopPad", Visible = false, MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0, ListTopPadding),
        };
        AddChild(_entriesTopPad);

        _entries = new VBoxContainer { Name = "Entries", Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _entries.AddThemeConstantOverride("separation", 2);
        AddChild(_entries);

        _entriesPad = new Control {
            Name = "EntriesPad", Visible = false, MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0, ListBottomPadding),
        };
        AddChild(_entriesPad);

        Connect(GraphElement.SignalName.NodeDeselected, new Callable(this, MethodName.OnDeselected));
        Connect(GraphElement.SignalName.NodeSelected, new Callable(this, MethodName.StyleEntries));

        // Title stays set — it is what the editor and tooltips refer to — but is not drawn twice.
        foreach (var label in GetTitlebarHBox().GetChildren(includeInternal: true).OfType<Label>()) {
            label.Visible = false;
        }
    }

    /// <summary>Re-reads name, summary, warnings, ports and styling from the bound definition node.</summary>
    /// <param name="treeWarnings">Problems only the tree can see, such as a link to a removed blackboard entry.</param>
    /// <param name="entryWarnings">For a list, the tree's problems with one of its entries.</param>
    public void Refresh(int siblingOrder = 0, int siblingCount = 1, string[] treeWarnings = null,
        System.Func<MissNode, string[]> entryWarnings = null) {
        if (IsRoot) {
            Title = "Root";
            _name.Text = Title;
            _summary.Visible = false;
            _order.Text = "";
            _warning.Text = "";
            _icon.Texture = ResourceLoader.Load<Texture2D>(RootIcon);
            SetSlot(0, false, 0, PortColor, true, 0, PortColor);
            Draggable = true;
            ApplyStyle();
            QueueRedraw();
            return;
        }

        Title = Node.GetLabel();
        _name.Text = Title;
        _summary.Text = Node.GetSummary();
        _summary.Visible = !string.IsNullOrEmpty(_summary.Text);

        _order.Text = siblingCount > 1 ? $"#{siblingOrder + 1}" : "";

        string[] warnings = [.. Node.GetConfigurationWarnings(), .. treeWarnings ?? []];
        _warning.Text = warnings.Length > 0 ? "⚠" : "";
        _warning.TooltipText = string.Join("\n", warnings);

        var iconPath = NodeTypeRegistry.IconFor(Node);
        _icon.Texture = string.IsNullOrEmpty(iconPath) ? null : ResourceLoader.Load<Texture2D>(iconPath);
        _icon.Visible = _icon.Texture != null;

        var acceptsChildren = Node.MaxChildren > 0;
        SetSlot(0, true, 0, PortColor, acceptsChildren, 0, PortColor);

        TooltipText = $"{Node.GetType().Name}\n{Group}";
        RefreshEntries(entryWarnings);
        ApplyStyle();
        QueueRedraw();
    }

    // ---- list entries ------------------------------------------------------------------------

    const string ModeIconSequence = "res://addons/missbehave/icons/sequence.svg";
    const string ModeIconSelector = "res://addons/missbehave/icons/selector.svg";

    /// <summary>Rows are recreated on every refresh: a list rarely holds more than a handful.</summary>
    void RefreshEntries(System.Func<MissNode, string[]> entryWarnings) {
        var list = List;
        _modeIcon.Visible = list != null;
        _entries.Visible = list != null;
        _entriesPad.Visible = list != null;
        _entriesTopPad.Visible = list != null;

        var hadRows = _entries.GetChildCount();
        foreach (var row in _entries.GetChildren()) {
            _entries.RemoveChild(row);
            row.QueueFree();
        }
        if (list == null) return;

        _modeIcon.Texture = ResourceLoader.Load<Texture2D>(list.Mode == ListMode.Selector ? ModeIconSelector : ModeIconSequence);
        _modeIcon.Material = GraphNodeStyles.IconMaterialFor(NodeTypeRegistry.GroupComposite);
        _modeIcon.TooltipText = list.Mode.ToString();

        // An entry that was taken out must not stay the one being inspected.
        if (!list.Children.Any(e => e?.Id == SelectedEntry)) SelectedEntry = "";

        foreach (var entry in list.Children) {
            if (entry == null) continue;
            _entries.AddChild(EntryRow(entry, entryWarnings?.Invoke(entry) ?? []));
        }

        // A box grows with its content on its own, but never shrinks back.
        if (_entries.GetChildCount() < hadRows) ResetSize();
        StyleEntries();
    }

    Control EntryRow(MissNode entry, string[] treeWarnings) {
        var row = new PanelContainer { Name = entry.Id, MouseFilter = MouseFilterEnum.Ignore };
        string[] warnings = [.. entry.GetConfigurationWarnings(), .. treeWarnings];
        var summary = entry.GetSummary();
        // The summary stays off the box to keep the list compact; it is one hover away.
        row.SetMeta("tooltip", string.Join("\n", new[] { entry.GetType().Name, summary }.Concat(warnings).Where(s => !string.IsNullOrEmpty(s))));

        var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 6);
        row.AddChild(line);

        var iconPath = NodeTypeRegistry.IconFor(entry);
        line.AddChild(new TextureRect {
            CustomMinimumSize = new Vector2(12, 12),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
            Texture = string.IsNullOrEmpty(iconPath) ? null : ResourceLoader.Load<Texture2D>(iconPath),
            Material = GraphNodeStyles.IconMaterialFor(NodeTypeRegistry.GroupOf(entry.GetType())),
        });

        var name = new Label { Name = "EntryName", Text = entry.GetLabel(), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        name.AddThemeFontSizeOverride("font_size", 13);
        line.AddChild(name);

        if (warnings.Length > 0) {
            var warning = new Label { Text = "⚠" };
            warning.AddThemeColorOverride("font_color", new Color("#ffd24a"));
            line.AddChild(warning);
        }
        return row;
    }

    /// <summary>The ids of the rows, top to bottom — the order the entries run in.</summary>
    public string[] EntryRowIds => [.. _entries.GetChildren().Select(row => row.Name.ToString())];

    /// <summary>Live status of one entry row, or null when it was not reached; for the test.</summary>
    public MissStatus? EntryStatus(string entryId)
        => _entryStatuses.TryGetValue(entryId, out var status) ? (MissStatus) status : null;

    void StyleEntries() {
        foreach (var row in _entries.GetChildren().OfType<PanelContainer>()) {
            var id = row.Name.ToString();
            MissStatus? status = _entryStatuses.TryGetValue(id, out var raw) ? (MissStatus) raw : null;
            var picked = Selected && id == SelectedEntry;
            row.AddThemeStyleboxOverride("panel", GraphNodeStyles.EntryRow(status, picked));
            // Like a box, an entry the tick never reached fades — but only while something is streaming.
            row.Modulate = new Color(1, 1, 1, _live && status == null ? GraphNodeStyles.DimmedAlpha : 1f);
        }
    }

    /// <summary>The entry row under a point in this box's own coordinates, or null.</summary>
    string EntryAt(Vector2 localPosition) {
        if (!_entries.Visible) return null;
        var global = GetGlobalTransform() * localPosition;
        foreach (var row in _entries.GetChildren().OfType<Control>()) {
            var local = row.GetGlobalTransform().AffineInverse() * global;
            if (new Rect2(Vector2.Zero, row.Size).HasPoint(local)) return row.Name.ToString();
        }
        return null;
    }

    /// <summary>Picks an entry as if its row had been clicked. Public for the test.</summary>
    public void PickEntry(string entryId) {
        SelectedEntry = entryId ?? "";
        StyleEntries();
        EmitSignal(SignalName.EntryPicked, Name, SelectedEntry);
    }

    /// <summary>Marks an entry as the inspected one without announcing it, e.g. when the editor already inspects it.</summary>
    public void ShowEntryPicked(string entryId) {
        SelectedEntry = entryId ?? "";
        StyleEntries();
    }

    void OnDeselected() {
        SelectedEntry = "";
        StyleEntries();
    }

    public override string _GetTooltip(Vector2 atPosition) {
        var id = EntryAt(atPosition);
        if (id != null && _entries.GetNodeOrNull(id) is { } row) return row.GetMeta("tooltip", "").AsString();
        return TooltipText;
    }

    static Color PortColor => new("#8ab4f8");

    public override void _Draw() {
        // A list sets its header off from the entries with a rule in its category's colour, which is
        // what makes it stand out from a plain box at a glance.
        if (List != null && GetNodeOrNull<Control>("Row") is { } header && _entries.GetChildCount() > 0) {
            var first = (Control) _entries.GetChild(0);
            // Centred in the gap above the top pad, so the pad is all room below the rule.
            var y = (header.Position.Y + header.Size.Y + _entriesTopPad.Position.Y) / 2f;
            var inset = _entries.Position.X + first.Position.X;
            DrawLine(new Vector2(inset, y), new Vector2(Size.X - inset, y),
                new Color(GraphNodeStyles.IconColor(Group), 0.6f), 1f, antialiased: true);
        }
        if (HasInput) DrawCircle(InputAnchor, PortRadius, PortColor, antialiased: true);
        if (HasOutput) DrawCircle(OutputAnchor, PortRadius, PortColor, antialiased: true);
    }

    public override void _GuiInput(InputEvent @event) {
        if (IsRoot || @event is not InputEventMouseButton { Pressed: true } click) return;

        var entry = List != null ? EntryAt(click.Position) : null;

        if (click.ButtonIndex == MouseButton.Left) {
            // Not consumed: GraphEdit still selects and drags the box as usual.
            if (List != null && (entry ?? "") != SelectedEntry) PickEntry(entry);
            return;
        }
        if (click.ButtonIndex != MouseButton.Right) return;

        var screen = GetScreenTransform() * click.Position;
        if (entry != null) EmitSignal(SignalName.EntryContextMenuRequested, Name, entry, screen);
        else EmitSignal(SignalName.ContextMenuRequested, Name, screen);
        // Consumed here so the canvas does not also open its create dialog underneath the menu.
        AcceptEvent();
    }

    /// <summary>
    /// Live tick status from a running game. Null means this node was not reached this tick, and it
    /// fades so that the path the tree actually took stands out.
    /// </summary>
    public void ShowLiveStatus(MissStatus? status) {
        if (_live && _status == status) return;
        _live = true;
        _status = status;
        ApplyStyle();
    }

    /// <summary>
    /// Live status of a list's entries, which the running tree ticks and reports like any other node.
    /// </summary>
    /// <param name="ticked">Status per node id, for the nodes that were ticked.</param>
    public void ShowLiveEntryStatuses(System.Collections.Generic.IReadOnlyDictionary<string, MissStatus> ticked) {
        _entryStatuses.Clear();
        foreach (var entry in List?.Children ?? []) {
            if (entry != null && ticked.TryGetValue(entry.Id, out var status)) _entryStatuses[entry.Id] = (int) status;
        }
        StyleEntries();
    }

    /// <summary>Back to the plain editing look once nothing is streaming any more.</summary>
    public void ClearLiveStatus() {
        _entryStatuses.Clear();
        if (!_live) return;
        _live = false;
        _status = null;
        ApplyStyle();
    }

    /// <summary>Whether the box is faded out as not reached by the running tree.</summary>
    public bool IsDimmed => _live && _status == null;

    void ApplyStyle() {
        var group = Group;
        GraphNodeStyles.Apply(this, group, _status);
        _icon.Material = GraphNodeStyles.IconMaterialFor(group);
        Modulate = new Color(1, 1, 1, IsDimmed ? GraphNodeStyles.DimmedAlpha : 1f);
        StyleEntries();
    }
}
#endif
