#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;
using Misscore.Editor;

namespace Misspeak.Editor;

/// <summary>
/// The authoring surface of a dialogue: sections as boxes with their lines listed inside, options as
/// wires from a row of the section they leave to the section they lead to. Wires, reroutes and how
/// ports are grabbed come from <see cref="MissGraphEdit"/>.
/// <para>
/// Every structural change goes through <see cref="Commit"/>, which records a before/after snapshot
/// with the editor's undo manager. The properties of a section, a line, an option, an action or a
/// condition are edited in the Inspector, which keeps its own undo steps.
/// </para>
/// </summary>
[Tool]
public partial class DialogueGraphEdit : MissGraphEdit {
    /// <summary>The dialogue resource was changed and needs saving.</summary>
    [Signal]
    public delegate void DialogueDirtiedEventHandler();

    /// <summary>What is selected changed. Listeners read the actual selection themselves.</summary>
    [Signal]
    public delegate void SelectionMovedEventHandler();

    /// <summary>An edit was refused, with a short message saying why.</summary>
    [Signal]
    public delegate void EditRejectedEventHandler(string reason);

    /// <summary>
    /// Undo or redo reached an edit of a dialogue other than the open one. Expected to open that
    /// dialogue right away, before the signal returns.
    /// </summary>
    [Signal]
    public delegate void DialogueRequestedEventHandler(Resource dialogue);

    // Untyped so an assembly reload can restore them — see ReloadSafe.
    GodotObject _dialogue;
    GodotObject _picker;

    public Dialogue Dialogue => ReloadSafe.Get<Dialogue>(ref _dialogue);

    /// <summary>The node picker shared with the other Missfits editors, narrowed to actions or to conditions.</summary>
    public CreateNodeDialog Picker => ReloadSafe.Get<CreateNodeDialog>(ref _picker);

    internal const int MenuAddSection = 0;
    internal const int MenuSetStart = 1;
    internal const int MenuDeleteSection = 2;
    internal const int MenuAddLine = 3;
    internal const int MenuAddReroute = 4;
    internal const int MenuDeleteReroute = 5;
    internal const int MenuAddPort = 7;
    internal const int MenuTogglePort = 8;
    internal const int MenuAddOption = 6;
    internal const int MenuRowUp = 10;
    internal const int MenuRowDown = 11;
    internal const int MenuRowDelete = 12;
    internal const int MenuAddCondition = 13;
    internal const int MenuAddAction = 14;
    internal const int MenuToggleBack = 15;

    PopupMenu _menu;
    bool _rebuilding;
    bool _rebuildQueued;
    Vector2 _menuPosition;
    StringName _menuBox;
    string _menuKind;
    string _menuRow;

    /// <summary>What the picker was opened to add: an action, or a condition of a line or of an option.</summary>
    string _adding;

    /// <summary>The wire the canvas menu was opened on — the box and port it leaves — or null for none.</summary>
    StringName _menuWireFrom;
    int _menuWirePort;

    /// <summary>What the graph was last built from, so an edit made elsewhere is noticed.</summary>
    string _builtFrom = "";

    public override void _Ready() {
        base._Ready();
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

    public void LoadDialogue(Dialogue dialogue) {
        _dialogue = dialogue;
        dialogue?.EnsureNodeIds();
        RebuildGraph();
    }

    IEnumerable<SectionBox> Boxes() => GetChildren().OfType<SectionBox>();

    public SectionBox BoxFor(string sectionId)
        => string.IsNullOrEmpty(sectionId) ? null : Boxes().FirstOrDefault(b => b.Name == sectionId);

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
    /// Rebuilds every box and wire from the dialogue, keeping what was selected.
    /// <para>
    /// Never call this straight out of a GraphEdit signal handler — freeing the boxes GraphEdit is
    /// working with crashes the editor. Use <see cref="QueueRebuild"/> from anything signal-driven.
    /// </para>
    /// </summary>
    public void RebuildGraph() {
        _rebuildQueued = false;
        _rebuilding = true;

        var selected = Boxes().FirstOrDefault(b => b.Selected);
        var selectedSection = selected?.Name.ToString();
        var pickedKind = selected?.PickedKind ?? "";
        var pickedId = selected?.PickedId ?? "";
        var selectedReroutes = RerouteBoxes().Where(b => b.Selected).Select(b => b.Name.ToString()).ToList();

        ClearGraph();

        var dialogue = Dialogue;
        if (dialogue != null) {
            var spot = 0;
            foreach (var section in dialogue.Sections) {
                if (section == null || BoxFor(section.Id) != null) continue;

                // Named before entering the tree so it can never collide with a leftover sibling.
                var box = new SectionBox { Name = section.Id };
                AddChild(box);
                box.Bind(section);
                box.Connect(SectionBox.SignalName.MenuRequested, new Callable(this, MethodName.OnBoxMenu));
                box.Connect(SectionBox.SignalName.RowPicked, new Callable(this, MethodName.OnRowPicked));
                box.Connect(SectionBox.SignalName.RowMenuRequested, new Callable(this, MethodName.OnRowMenu));
                box.Connect(SectionBox.SignalName.AddLineRequested, new Callable(this, MethodName.OnAddLineRequested));
                TrackMoves(box);

                // A dialogue built in code has no positions yet; spread it out rather than piling it up.
                box.PositionOffset = section.GraphPosition != Vector2.Zero || dialogue.Sections.Count == 1
                    ? section.GraphPosition
                    : new Vector2(60 + 340 * (spot % 4), 60 + 260 * (spot / 4));
                spot++;
            }

            foreach (var reroute in dialogue.Reroutes) {
                if (reroute != null && !HasBox(reroute.Id)) AddRerouteBox(reroute);
            }

            foreach (var section in dialogue.Sections) {
                if (section == null) continue;
                var port = 0;
                foreach (var option in section.Options) {
                    if (option == null) continue;
                    if (!option.Back && HasBox(option.TargetSectionId)) ConnectNode(section.Id, port, option.TargetSectionId, 0);
                    port++;
                }
            }
            foreach (var reroute in dialogue.Reroutes) {
                if (IsWired(reroute) && HasBox(reroute.TargetId)) ConnectNode(reroute.Id, 0, reroute.TargetId, 0);
            }
        }

        _builtFrom = Shape();
        RefreshBoxes();
        UpdateFlips();
        _rebuilding = false;

        if (BoxFor(selectedSection) is { } again) {
            again.Selected = true;
            again.ShowPicked(pickedKind, pickedId);
        }
        foreach (var name in selectedReroutes) {
            if (RerouteBoxFor(name) is { } reroute) reroute.Selected = true;
        }

        UpdateHint();

        // A paused game sends nothing further, so an edit that rebuilds the graph has to repaint it.
        if (_live) PaintLive();
    }

    // ---- live debugging ----------------------------------------------------------------------

    /// <summary>Where a running game last reported the dialogue to be, kept so a rebuild can show it again.</summary>
    bool _live;
    string _liveSection = "";
    string _liveLine = "";
    string _liveEnteredBy = "";

    /// <summary>The options taken since the player last did something, in order, and where leading back would go.</summary>
    string[] _liveTrail = [];
    string _liveBackTo = "";

    /// <summary>Shows where a running dialogue is: its section, the line it is at, and how it got there.</summary>
    /// <param name="sectionId">Empty while the runner has no dialogue under way.</param>
    /// <param name="enteredBy">The option that led into the section.</param>
    /// <param name="trail">Every option taken since the player last did something, that one last. Null for just that one.</param>
    /// <param name="backTo">The section an option that leads back would go to, or empty.</param>
    public void ShowLive(string sectionId, string lineId, string enteredBy = "", string[] trail = null, string backTo = "") {
        _live = true;
        _liveSection = sectionId ?? "";
        _liveLine = lineId ?? "";
        _liveEnteredBy = enteredBy ?? "";
        _liveTrail = trail is { Length: > 0 } ? trail : _liveEnteredBy == "" ? [] : [_liveEnteredBy];
        _liveBackTo = backTo ?? "";
        PaintLive();
    }

    void PaintLive() {
        // Between talks nothing stands out, and nothing fades either.
        if (_liveSection == "") {
            StopShowingLive();
            return;
        }

        // Leading back draws no wire. The option's row names where it would go, and that section
        // gets a quiet outline — but only while the current section can lead back at all.
        var dialogue = Dialogue;
        var target = dialogue?.FindSection(_liveBackTo);
        var backTo = target == null ? null : string.IsNullOrEmpty(target.Name) ? "(unnamed)" : target.Name;
        var canGoBack = dialogue?.FindSection(_liveSection)?.Options.Any(o => o != null && o.Back) == true;
        var wentBackBy = _liveTrail.Length > 0 && Find(_liveTrail[^1], out _, out _, out var last) && last.Back ? last.Id : "";

        foreach (var box in Boxes()) {
            box.ShowLive(box.Name == _liveSection, _liveLine, dialogue, backTo, wentBackBy);
            box.Awaited = canGoBack && target != null && box.Name == target.Id;
        }
        PlaceWireOverlay();
        WireOverlay?.ShowWires(LiveWires());
    }

    public void ClearLive() {
        if (!_live) return;
        _live = false;
        StopShowingLive();
    }

    void StopShowingLive() {
        foreach (var box in Boxes()) {
            box.ClearLive(Dialogue);
            box.Awaited = false;
        }
        WireOverlay?.ClearWires();
    }

    /// <summary>
    /// The wires to highlight. As taken: every option on the trail, so the way is drawn from where
    /// the player last did something, through any sections the dialogue passed by itself. As
    /// waiting: every way out of the current section that follows a wire. Wires that ports hide are
    /// drawn all the same — while a game runs, where it goes matters more than tidiness. An option
    /// that leads back has no wire; its row and its target say it instead. An option that was edited
    /// away since the game started is simply not found.
    /// </summary>
    List<LiveWire> LiveWires() {
        var wires = new List<LiveWire>();
        var dialogue = Dialogue;
        if (dialogue == null) return wires;

        for (var i = 0; i < _liveTrail.Length; i++) {
            if (!Find(_liveTrail[i], out var from, out var port, out var option)) continue;

            // Where the option led: the section the next one on the trail leaves, or the current one.
            var led = i + 1 < _liveTrail.Length && Find(_liveTrail[i + 1], out var next, out _, out _) ? next.Id : _liveSection;
            if (!option.Back && dialogue.Destination(option.TargetSectionId)?.Id == led) FollowWire(wires, from.Id, port, option.TargetSectionId, taken: true);
        }

        if (dialogue.FindSection(_liveSection) is { } current) {
            var port = 0;
            foreach (var option in current.Options) {
                if (option == null) continue;
                if (!option.Back) FollowWire(wires, current.Id, port, option.TargetSectionId, taken: false);
                port++;
            }
        }
        return wires;
    }

    /// <summary>An option of the dialogue by its id, with the section it leaves and the port it has there.</summary>
    bool Find(string optionId, out DialogueSection section, out int port, out DialogueOption option) {
        foreach (var candidate in Dialogue.Sections) {
            if (candidate == null) continue;
            port = 0;
            foreach (var item in candidate.Options) {
                if (item == null) continue;
                if (item.Id == optionId) {
                    (section, option) = (candidate, item);
                    return true;
                }
                port++;
            }
        }
        (section, port, option) = (null, 0, null);
        return false;
    }

    /// <summary>Marks, quietly, the section the picked option leads to — or the selected reroute or port.</summary>
    void UpdateHint() {
        var box = Boxes().FirstOrDefault(b => b.Selected);
        var option = box?.PickedKind == SpeakRow.Option ? box.Section?.Options.FirstOrDefault(o => o != null && o.Id == box.PickedId) : null;
        HintTarget(option == null || option.Back ? null : option.TargetSectionId);
    }

    // ---- keeping up with the dialogue --------------------------------------------------------

    void QueueRebuild() {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        CallDeferred(MethodName.RebuildGraph);
    }

    /// <summary>Re-reads names, texts and warnings without rebuilding.</summary>
    public void RefreshBoxes() {
        foreach (var box in Boxes()) box.Refresh(Dialogue);
        foreach (var port in RerouteBoxes().Where(box => box.IsPort)) {
            var target = Dialogue?.Destination(port.Reroute.TargetId);
            port.ShowTarget(target == null ? null : string.IsNullOrEmpty(target.Name) ? "(unnamed)" : target.Name);
        }
    }

    /// <summary>
    /// Brings the graph up to date after something outside it changed the dialogue — the Inspector,
    /// say. Rebuilds only when sections, lines or options came, went or moved; otherwise just re-reads.
    /// </summary>
    public void SyncWithDialogue() {
        Dialogue?.EnsureNodeIds();
        if (Shape() != _builtFrom) QueueRebuild();
        else if (!_rebuildQueued) RefreshBoxes();
    }

    /// <summary>The dialogue's structure in one string: which sections, lines and options, leading where.</summary>
    string Shape() {
        var dialogue = Dialogue;
        if (dialogue == null) return "";

        static string Ids(IEnumerable<MissNode> nodes) => string.Join(" ", nodes.Where(n => n != null).Select(n => n.Id));
        return string.Join("|", dialogue.Sections.Where(s => s != null).Select(s =>
                   $"{s.Id}:{string.Join(",", s.Lines.Where(l => l != null).Select(l => $"{l.Id}[{Ids(l.Conditions)}][{Ids(l.Actions)}]"))}"
                   + $":{string.Join(",", s.Options.Where(o => o != null).Select(o => $"{o.Id}>{(o.Back ? "<back" : o.TargetSectionId)}[{Ids(o.Conditions)}]"))}"))
               + "#" + string.Join("|", dialogue.Reroutes.Where(r => r != null).Select(r => $"{r.Id}>{r.TargetId}{(r.Wireless ? "~" : "")}"));
    }

    // ---- selection ---------------------------------------------------------------------------

    void OnNodeSelected(Node node) {
        if (_rebuilding) return;
        UpdateHint();
        EmitSignal(SignalName.SelectionMoved);
    }

    void OnNodeDeselected(Node node) {
        if (_rebuilding) return;
        UpdateHint();
        EmitSignal(SignalName.SelectionMoved);
    }

    void OnRowPicked(StringName boxName, string kind, string id) => Pick(boxName, kind, id);

    /// <summary>Selects a section's box and, within it, a row — or the section itself, with an empty id.</summary>
    public void Pick(StringName boxName, string kind = "", string id = "") {
        var box = BoxFor(boxName);
        if (box == null) return;

        foreach (var other in Boxes()) other.Selected = ReferenceEquals(other, box);
        foreach (var reroute in RerouteBoxes()) reroute.Selected = false;
        box.ShowPicked(kind, id);
        UpdateHint();
        EmitSignal(SignalName.SelectionMoved);
    }

    /// <summary>What is selected: a line, an option, an action or a condition, else its section, else nothing.</summary>
    public Resource SelectedResource() {
        var box = Boxes().FirstOrDefault(b => b.Selected);
        var section = box?.Section;
        if (section == null) return null;
        if (box.PickedId == "") return section;

        return Siblings(section, box.PickedKind, box.PickedId)?.Select(item => item.AsGodotObject() as Resource)
                   .FirstOrDefault(item => item != null && IdOf(item) == box.PickedId)
               ?? section;
    }

    // ---- menus -------------------------------------------------------------------------------

    void OnPopupRequest(Vector2 atPosition) {
        if (Dialogue == null) return;

        _menuPosition = (atPosition + ScrollOffset) / Zoom;
        _menuBox = null;
        _menuRow = null;

        // On a wire, a reroute is put into that wire; anywhere else it starts out loose.
        TryWireAt(atPosition, out _menuWireFrom, out _menuWirePort);

        _menu.Clear();
        _menu.AddItem("Add section", MenuAddSection);
        _menu.AddItem(_menuWireFrom != null ? "Add reroute to this wire" : "Add reroute", MenuAddReroute);
        _menu.AddItem(_menuWireFrom != null ? "Add port to this wire" : "Add port", MenuAddPort);
        PopupAt(_menu, GetScreenPosition() + atPosition);
    }

    protected override void RerouteMenuRequested(StringName boxName, Vector2 screenPosition) {
        if (RerouteBoxFor(boxName) == null) return;

        _menuBox = boxName;
        _menuRow = null;
        _menuPosition = screenPosition;
        _menu.Clear();
        _menu.AddItem(RerouteBoxFor(boxName).IsPort ? "Show its wire (make it a reroute)" : "Hide its wire (make it a port)", MenuTogglePort);
        _menu.AddSeparator();
        _menu.AddItem("Delete", MenuDeleteReroute);
        PopupAt(_menu, screenPosition);
    }

    void OnBoxMenu(StringName boxName, Vector2 screenPosition) {
        var box = BoxFor(boxName);
        if (box == null) return;

        Pick(boxName);
        _menuBox = boxName;
        _menuRow = null;
        _menuPosition = screenPosition;
        _menu.Clear();
        _menu.AddItem("Add line", MenuAddLine);
        _menu.AddItem("Add option", MenuAddOption);
        _menu.AddSeparator();
        _menu.AddItem("Start the dialogue here", MenuSetStart);
        _menu.SetItemDisabled(_menu.ItemCount - 1, ReferenceEquals(Dialogue.StartSection, box.Section));
        _menu.AddSeparator();
        _menu.AddItem("Delete", MenuDeleteSection);
        PopupAt(_menu, screenPosition);
    }

    void OnRowMenu(StringName boxName, string kind, string id, Vector2 screenPosition) {
        var section = BoxFor(boxName)?.Section;
        if (!Place(section, kind, id, out var index, out var count)) return;

        _menuBox = boxName;
        _menuKind = kind;
        _menuRow = id;
        _menuPosition = screenPosition;
        _menu.Clear();
        _menu.AddItem("Move up", MenuRowUp);
        _menu.SetItemDisabled(_menu.ItemCount - 1, index == 0);
        _menu.AddItem("Move down", MenuRowDown);
        _menu.SetItemDisabled(_menu.ItemCount - 1, index == count - 1);

        if (kind is SpeakRow.Line or SpeakRow.Option) {
            _menu.AddSeparator();
            if (kind == SpeakRow.Line) _menu.AddItem("Add action…", MenuAddAction);
            _menu.AddItem("Add condition…", MenuAddCondition);
            if (kind == SpeakRow.Option) {
                _menu.AddCheckItem("Lead back", MenuToggleBack);
                _menu.SetItemChecked(_menu.ItemCount - 1, section.Options[index]?.Back == true);
                _menu.SetItemTooltip(_menu.ItemCount - 1, "Back to the section in which the player last made a choice");
            }
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
            case MenuAddSection: AddSection(_menuPosition); break;
            case MenuAddLine: AddLine(_menuBox); break;
            case MenuAddOption: AddOption(_menuBox); break;
            case MenuSetStart: SetStart(_menuBox); break;
            case MenuDeleteSection:
            case MenuDeleteReroute: DeleteBoxes([_menuBox]); break;
            case MenuAddReroute: AddReroute(_menuPosition, _menuWireFrom, _menuWirePort); break;
            case MenuAddPort: AddReroute(_menuPosition, _menuWireFrom, _menuWirePort, port: true); break;
            case MenuTogglePort: SetPort(_menuBox, RerouteBoxFor(_menuBox)?.IsPort != true); break;
            case MenuRowUp: MoveRow(_menuBox, _menuKind, _menuRow, -1); break;
            case MenuRowDown: MoveRow(_menuBox, _menuKind, _menuRow, 1); break;
            case MenuRowDelete: DeleteRow(_menuBox, _menuKind, _menuRow); break;
            case MenuAddAction: OfferTypes(typeof(ActionNode), SpeakRow.Action); break;
            case MenuToggleBack: SetBack(_menuBox, _menuRow, BoxFor(_menuBox)?.Section?.Options.FirstOrDefault(o => o?.Id == _menuRow)?.Back != true); break;
            case MenuAddCondition: OfferTypes(typeof(ConditionNode), _menuKind == SpeakRow.Line ? SpeakRow.LineCondition : SpeakRow.OptionCondition); break;
        }
    }

    void OnAddLineRequested(StringName boxName) => AddLine(boxName);

    /// <summary>
    /// Opens the node picker, showing only what fits: actions or conditions for the line, or
    /// conditions for the option, in <see cref="_menuRow"/>.
    /// </summary>
    void OfferTypes(Type kind, string adding) {
        var section = BoxFor(_menuBox)?.Section;
        if (section == null) return;

        _adding = adding;
        var where = string.IsNullOrEmpty(section.Name) ? "a section" : section.Name;
        Picker.OpenFor(adding switch {
            SpeakRow.Action => $"Add action to a line of {where}",
            SpeakRow.LineCondition => $"Add condition to a line of {where}",
            _ => $"Add condition to an option of {where}",
        }, "Add", kind);
    }

    internal void OnTypeChosen(string typeName) {
        var type = NodeTypeRegistry.FindByName(typeName)?.Type;
        if (type == null) return;

        switch (_adding) {
            case SpeakRow.Action: AddAction(_menuBox, _menuRow, type); break;
            case SpeakRow.LineCondition: AddLineCondition(_menuBox, _menuRow, type); break;
            case SpeakRow.OptionCondition: AddOptionCondition(_menuBox, _menuRow, type); break;
        }
    }

    // ---- sections ----------------------------------------------------------------------------

    public DialogueSection AddSection(Vector2 position, string name = null) {
        if (Dialogue == null) return null;

        var section = new DialogueSection { Name = UniqueName(string.IsNullOrWhiteSpace(name) ? "Section" : name.Trim()), GraphPosition = position };
        section.Lines.Add(new DialogueLine());
        Commit($"Misspeak: add section {section.Name}", () => Dialogue.Sections.Add(section));
        return section;
    }

    string UniqueName(string wanted) {
        if (Dialogue.FindSectionByName(wanted) == null) return wanted;
        for (var i = 2; ; i++) {
            if (Dialogue.FindSectionByName($"{wanted}{i}") == null) return $"{wanted}{i}";
        }
    }

    public void SetStart(StringName boxName) {
        var section = BoxFor(boxName)?.Section;
        if (section == null || ReferenceEquals(Dialogue.StartSection, section)) return;
        Commit($"Misspeak: start with {section.Name}", () => Dialogue.StartSectionId = section.Id, structural: false);
    }

    /// <summary>
    /// Deletes sections and reroutes in one step. Options that led to a deleted section are kept and
    /// flagged as leading nowhere, rather than silently taking their conditions with them. What led
    /// to a deleted reroute leads on to where the reroute led, so taking one out of a wire leaves
    /// the wire whole.
    /// </summary>
    public void DeleteBoxes(IEnumerable<StringName> boxNames) {
        var names = boxNames.ToList();
        var sections = names.Select(n => BoxFor(n)?.Section).Where(s => s != null).ToList();
        var reroutes = names.Select(n => RerouteBoxFor(n)?.Reroute).Where(r => r != null).ToList();
        if (sections.Count + reroutes.Count == 0) return;

        var what = (sections.Count, reroutes.Count) switch {
            (1, 0) => $"delete section {sections[0].Name}",
            (_, 0) => "delete sections",
            (0, 1) => "delete reroute",
            (0, _) => "delete reroutes",
            _ => "delete",
        };
        Commit($"Misspeak: {what}", () => {
            foreach (var reroute in reroutes) Bypass(reroute);
            foreach (var section in sections) Dialogue.Sections.Remove(section);
        });
    }

    void Bypass(MissReroute reroute) {
        foreach (var option in Dialogue.Sections.Where(s => s != null).SelectMany(s => s.Options)) {
            if (option?.TargetSectionId == reroute.Id) option.TargetSectionId = reroute.TargetId;
        }
        foreach (var other in Dialogue.Reroutes) {
            if (other != null && other.TargetId == reroute.Id) other.TargetId = reroute.TargetId;
        }
        Dialogue.Reroutes.Remove(reroute);
    }

    void OnDeleteNodesRequest(Godot.Collections.Array<StringName> nodes) {
        if (Dialogue == null) return;

        // Delete with a row picked removes that line, option, action or condition, not its section.
        var picked = Boxes().FirstOrDefault(b => b.Selected && b.PickedId != "");
        if (picked != null && nodes.Count == 1 && nodes[0] == picked.Name) {
            DeleteRow(picked.Name, picked.PickedKind, picked.PickedId);
            return;
        }
        DeleteBoxes(nodes);
    }

    void OnEndNodeMove() {
        if (Dialogue == null || _rebuilding) return;

        Commit("Misspeak: move", () => {
            foreach (var box in Boxes()) {
                if (box.Section != null) box.Section.GraphPosition = box.PositionOffset;
            }
            foreach (var box in RerouteBoxes()) {
                if (box.Reroute != null) box.Reroute.GraphPosition = box.PositionOffset;
            }
        }, structural: false);
    }

    // ---- lines -------------------------------------------------------------------------------

    public DialogueLine AddLine(StringName boxName) {
        var section = BoxFor(boxName)?.Section;
        if (section == null) return null;

        // The speaker carries over: most of the time the same one goes on talking.
        var line = new DialogueLine { Speaker = section.Lines.LastOrDefault(l => l != null)?.Speaker ?? "" };
        Commit($"Misspeak: add line to {section.Name}", () => section.Lines.Add(line));
        return line;
    }

    public MissNode AddAction(StringName boxName, string lineId, Type type) {
        var line = BoxFor(boxName)?.Section?.Lines.FirstOrDefault(l => l != null && l.Id == lineId);
        if (line == null || type == null) return null;

        var action = NodeTypeRegistry.Create(type);
        Commit($"Misspeak: add {NodeAttributes.NameOf(type)}", () => line.Actions.Add(action));
        return action;
    }

    public MissNode AddLineCondition(StringName boxName, string lineId, Type type) {
        var line = BoxFor(boxName)?.Section?.Lines.FirstOrDefault(l => l != null && l.Id == lineId);
        if (line == null || type == null) return null;

        var condition = NodeTypeRegistry.Create(type);
        Commit($"Misspeak: add condition {NodeAttributes.NameOf(type)}", () => line.Conditions.Add(condition));
        return condition;
    }

    // ---- options -----------------------------------------------------------------------------

    /// <summary>Adds an option that leads nowhere yet — which, taken, ends the dialogue.</summary>
    public DialogueOption AddOption(StringName boxName) {
        var section = BoxFor(boxName)?.Section;
        if (section == null) return null;

        var option = new DialogueOption();
        Commit($"Misspeak: add option to {section.Name}", () => section.Options.Add(option));
        return option;
    }

    public MissNode AddOptionCondition(StringName boxName, string optionId, Type type) {
        var option = BoxFor(boxName)?.Section?.Options.FirstOrDefault(o => o != null && o.Id == optionId);
        if (option == null || type == null) return null;

        var condition = NodeTypeRegistry.Create(type);
        Commit($"Misspeak: add condition {NodeAttributes.NameOf(type)}", () => option.Conditions.Add(condition));
        return condition;
    }

    /// <summary>
    /// A wire was dragged from an output port onto a box: from an option's own port it leads that
    /// option there, from the spare port it adds a new one, from a reroute it leads the reroute on.
    /// </summary>
    void OnConnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort) {
        if (!HasBox(toNode)) return;

        if (BoxFor(fromNode)?.Section is { } from) LeadTo(from, (int) fromPort, toNode);
        else if (RerouteBoxFor(fromNode)?.Reroute is { } reroute) LeadOn(reroute, toNode);
    }

    /// <summary>Dropping a wire on empty canvas makes a new section there, and leads the option to it.</summary>
    void OnConnectionToEmpty(StringName fromNode, long fromPort, Vector2 releasePosition) {
        var from = BoxFor(fromNode)?.Section;
        var reroute = RerouteBoxFor(fromNode)?.Reroute;
        if (from == null && reroute == null) return;

        var section = new DialogueSection { Name = UniqueName("Section"), GraphPosition = (releasePosition + ScrollOffset) / Zoom };
        section.Lines.Add(new DialogueLine());
        Commit($"Misspeak: add section {section.Name}", () => {
            Dialogue.Sections.Add(section);
            if (from != null) Lead(from, (int) fromPort, section.Id);
            else reroute.TargetId = section.Id;
        });
    }

    /// <summary>Leads the option behind <paramref name="port"/> to a section, adding one for the spare port.</summary>
    public void LeadTo(DialogueSection from, int port, DialogueSection to) => LeadTo(from, port, to.Id);

    /// <summary>As above, to a section or a reroute by id.</summary>
    public void LeadTo(DialogueSection from, int port, string targetId) {
        var existing = OptionAt(from, port);
        if (existing != null && existing.TargetSectionId == targetId && !existing.Back) return;

        Commit(existing == null ? $"Misspeak: add option to {TargetName(targetId)}" : $"Misspeak: lead option to {TargetName(targetId)}",
            () => Lead(from, port, targetId));
    }

    static void Lead(DialogueSection from, int port, string targetId) {
        if (OptionAt(from, port) is { } option) {
            // A wire says where to: that is the end of leading back.
            option.TargetSectionId = targetId;
            option.Back = false;
        }
        else {
            from.Options.Add(new DialogueOption { TargetSectionId = targetId });
        }
    }

    /// <summary>
    /// Has an option lead back — to the section in which the player last made a choice — instead of
    /// along a wire, or stops it doing so. Where its wire led is kept, in case it is wanted again.
    /// </summary>
    public void SetBack(StringName boxName, string optionId, bool back) {
        var option = BoxFor(boxName)?.Section?.Options.FirstOrDefault(o => o != null && o.Id == optionId);
        if (option == null || option.Back == back) return;
        Commit(back ? "Misspeak: lead option back" : "Misspeak: stop leading option back", () => option.Back = back);
    }

    /// <summary>The option behind an output port, or null for the spare port after the last one.</summary>
    static DialogueOption OptionAt(DialogueSection section, int port) {
        var options = section.Options.Where(o => o != null).ToList();
        return port >= 0 && port < options.Count ? options[port] : null;
    }

    string TargetName(string targetId) => Dialogue.FindSection(targetId)?.Name ?? "a reroute";

    // ---- reroutes ----------------------------------------------------------------------------

    /// <summary>
    /// Adds a reroute, centred on <paramref name="position"/>. Given the box and port a wire leaves,
    /// the reroute is put into that wire: the wire now ends at it, and it leads on to where the wire went.
    /// </summary>
    /// <param name="port">Makes it a port: its outgoing wire is not drawn, it names its target instead.</param>
    public MissReroute AddReroute(Vector2 position, StringName wireFrom = null, int wirePort = 0, bool port = false) {
        if (Dialogue == null) return null;

        var option = BoxFor(wireFrom)?.Section is { } section ? OptionAt(section, wirePort) : null;
        var before = RerouteBoxFor(wireFrom)?.Reroute;

        var reroute = new MissReroute {
            Wireless = port,
            GraphPosition = position - (RerouteBox.BodySize / 2),
            TargetId = option?.TargetSectionId ?? before?.TargetId ?? "",
        };
        Commit(port ? "Misspeak: add port" : "Misspeak: add reroute", () => {
            Dialogue.Reroutes.Add(reroute);
            if (option != null) option.TargetSectionId = reroute.Id;
            else if (before != null) before.TargetId = reroute.Id;
        });
        return reroute;
    }

    /// <summary>Turns a reroute into a port — its wire hidden, its target named — or back.</summary>
    public void SetPort(StringName boxName, bool port) {
        var reroute = RerouteBoxFor(boxName)?.Reroute;
        if (reroute == null || reroute.Wireless == port) return;
        Commit(port ? "Misspeak: hide a reroute's wire" : "Misspeak: show a reroute's wire", () => reroute.Wireless = port);
    }

    /// <summary>A double-click on a wire puts a reroute into it.</summary>
    protected override void WireDoubleClicked(Vector2 position, StringName fromNode, int fromPort) {
        if (Dialogue != null) AddReroute(position, fromNode, fromPort);
    }

    /// <summary>Leads a reroute on to a section or another reroute — unless that would send its wires round in a circle.</summary>
    public void LeadOn(MissReroute reroute, string targetId) {
        if (reroute == null || reroute.TargetId == targetId) return;

        if (MissReroute.WouldLoop(Dialogue.Reroutes, reroute, targetId)) {
            EmitSignal(SignalName.EditRejected, "A reroute cannot lead back to itself.");
            return;
        }

        Commit($"Misspeak: lead reroute to {TargetName(targetId)}", () => reroute.TargetId = targetId);
    }

    // ---- rows --------------------------------------------------------------------------------

    static string IdOf(GodotObject item) => item switch {
        DialogueLine line => line.Id,
        DialogueOption option => option.Id,
        MissNode node => node.Id,
        _ => "",
    };

    /// <summary>
    /// The list a row sits in: the section's lines or options, or — for an action or a condition —
    /// the list of the line or option it belongs to. Untyped, so that rows of any kind can be moved
    /// and deleted the same way; it is the same list underneath, not a copy.
    /// </summary>
    static Godot.Collections.Array Siblings(DialogueSection section, string kind, string id) {
        if (section == null) return null;
        bool Has(Godot.Collections.Array<MissNode> nodes) => nodes.Any(n => n != null && n.Id == id);

        return kind switch {
            SpeakRow.Line => (Godot.Collections.Array) section.Lines,
            SpeakRow.Option => (Godot.Collections.Array) section.Options,
            SpeakRow.Action => section.Lines.FirstOrDefault(l => l != null && Has(l.Actions)) is { } line ? (Godot.Collections.Array) line.Actions : null,
            SpeakRow.LineCondition => section.Lines.FirstOrDefault(l => l != null && Has(l.Conditions)) is { } gated ? (Godot.Collections.Array) gated.Conditions : null,
            SpeakRow.OptionCondition => section.Options.FirstOrDefault(o => o != null && Has(o.Conditions)) is { } option ? (Godot.Collections.Array) option.Conditions : null,
            _ => null,
        };
    }

    /// <summary>Where a row sits among its siblings, and how many of them there are.</summary>
    static bool Place(DialogueSection section, string kind, string id, out int index, out int count) {
        (index, count) = (-1, 0);
        if (string.IsNullOrEmpty(id) || Siblings(section, kind, id) is not { } siblings) return false;

        count = siblings.Count;
        for (var i = 0; i < count && index < 0; i++) {
            if (IdOf(siblings[i].AsGodotObject()) == id) index = i;
        }
        return index >= 0;
    }

    /// <summary>
    /// Lines are spoken, actions run, options are considered and conditions are checked top to
    /// bottom — so the order of the rows matters.
    /// </summary>
    public void MoveRow(StringName boxName, string kind, string id, int by) {
        var section = BoxFor(boxName)?.Section;
        if (!Place(section, kind, id, out var index, out var count)) return;
        var target = index + by;
        if (target < 0 || target >= count) return;

        Commit($"Misspeak: reorder {Word(kind)}", () => {
            var siblings = Siblings(section, kind, id);
            var item = siblings[index];
            siblings.RemoveAt(index);
            siblings.Insert(target, item);
        });
    }

    public void DeleteRow(StringName boxName, string kind, string id) {
        var section = BoxFor(boxName)?.Section;
        if (!Place(section, kind, id, out var index, out _)) return;

        Commit($"Misspeak: delete {Word(kind)}", () => Siblings(section, kind, id).RemoveAt(index));
    }

    static string Word(string kind) => kind switch {
        SpeakRow.LineCondition or SpeakRow.OptionCondition => "condition",
        _ => kind,
    };

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
            // Not marked unsaved in Godot's history: saving the dialogue cannot mark that history
            // saved again. The panel tracks unsaved dialogues.
            undoRedo.CreateAction(actionName, UndoRedo.MergeMode.Disable, Dialogue, false, false);
            undoRedo.AddDoMethod(this, MethodName.RestoreSnapshot, after);
            undoRedo.AddUndoMethod(this, MethodName.RestoreSnapshot, before);

            // The mutation is already applied, so do not let CommitAction replay it.
            undoRedo.CommitAction(false);
        }

        if (structural) QueueRebuild();
        else RefreshBoxes();

        EmitSignal(SignalName.DialogueDirtied);
    }

    /// <summary>
    /// The structure of the dialogue: its sections in order, where each sits, its lines in order
    /// with what each checks and runs, its options in order with where each leads and what it
    /// checks — and its reroutes. The objects themselves travel along, so undo can bring a deleted
    /// one back as the same object.
    /// </summary>
    internal Godot.Collections.Dictionary TakeSnapshot() {
        var sections = new Godot.Collections.Array();
        foreach (var section in Dialogue.Sections) {
            if (section == null) continue;

            var lines = new Godot.Collections.Array();
            foreach (var line in section.Lines) {
                if (line == null) continue;
                lines.Add(new Godot.Collections.Dictionary {
                    { "line", line },
                    { "conditions", Nodes(line.Conditions) },
                    { "actions", Nodes(line.Actions) },
                });
            }
            var options = new Godot.Collections.Array();
            foreach (var option in section.Options) {
                if (option == null) continue;
                options.Add(new Godot.Collections.Dictionary {
                    { "option", option },
                    { "target", option.TargetSectionId },
                    { "back", option.Back },
                    { "conditions", Nodes(option.Conditions) },
                });
            }
            sections.Add(new Godot.Collections.Dictionary {
                { "section", section },
                { "pos", section.GraphPosition },
                { "lines", lines },
                { "options", options },
            });
        }

        var reroutes = new Godot.Collections.Array();
        foreach (var reroute in Dialogue.Reroutes) {
            if (reroute == null) continue;
            reroutes.Add(new Godot.Collections.Dictionary {
                { "reroute", reroute },
                { "pos", reroute.GraphPosition },
                { "target", reroute.TargetId },
                { "wireless", reroute.Wireless },
            });
        }

        return new Godot.Collections.Dictionary {
            { "dialogue", Dialogue },
            { "start", Dialogue.StartSectionId },
            { "sections", sections },
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
        // Undo is shared by every dialogue edited this session: an edit of another dialogue is shown
        // by opening that dialogue, never applied to whichever one happens to be open.
        if (snapshot.TryGetValue("dialogue", out var owner) && owner.AsGodotObject() is Dialogue other && !ReferenceEquals(other, Dialogue)) {
            EmitSignal(SignalName.DialogueRequested, other);
            if (!ReferenceEquals(other, Dialogue)) return;
        }
        if (Dialogue == null) return;

        var sections = new Godot.Collections.Array<DialogueSection>();
        foreach (var item in snapshot["sections"].AsGodotArray()) {
            var data = item.AsGodotDictionary();
            if (data["section"].AsGodotObject() is not DialogueSection section) continue;

            section.GraphPosition = data["pos"].AsVector2();

            var lines = new Godot.Collections.Array<DialogueLine>();
            foreach (var entry in data["lines"].AsGodotArray()) {
                var kept = entry.AsGodotDictionary();
                if (kept["line"].AsGodotObject() is not DialogueLine line) continue;
                line.Conditions = NodesFrom(kept["conditions"]);
                line.Actions = NodesFrom(kept["actions"]);
                lines.Add(line);
            }
            section.Lines = lines;

            var options = new Godot.Collections.Array<DialogueOption>();
            foreach (var entry in data["options"].AsGodotArray()) {
                var kept = entry.AsGodotDictionary();
                if (kept["option"].AsGodotObject() is not DialogueOption option) continue;
                option.TargetSectionId = kept["target"].AsString();
                if (kept.TryGetValue("back", out var back)) option.Back = back.AsBool();
                option.Conditions = NodesFrom(kept["conditions"]);
                options.Add(option);
            }
            section.Options = options;
            sections.Add(section);
        }
        Dialogue.Sections = sections;
        Dialogue.StartSectionId = snapshot["start"].AsString();

        var reroutes = new Godot.Collections.Array<MissReroute>();
        foreach (var item in snapshot["reroutes"].AsGodotArray()) {
            var data = item.AsGodotDictionary();
            if (data["reroute"].AsGodotObject() is not MissReroute reroute) continue;

            reroute.GraphPosition = data["pos"].AsVector2();
            reroute.TargetId = data["target"].AsString();
            if (data.TryGetValue("wireless", out var wireless)) reroute.Wireless = wireless.AsBool();
            reroutes.Add(reroute);
        }
        Dialogue.Reroutes = reroutes;

        QueueRebuild();
        EmitSignal(SignalName.DialogueDirtied);
    }
}
#endif
