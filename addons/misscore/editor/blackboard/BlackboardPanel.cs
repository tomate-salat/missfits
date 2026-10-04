#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misscore.Editor;

/// <summary>
/// The blackboard of the open <see cref="IBlackboardSource"/>: every entry with its name, type and default value,
/// all editable in place. It also owns every edit that touches entries or the links to them — made
/// from here or from a parameter in the Inspector — so each is one undo step.
/// <para>
/// Undo restores a snapshot of the entries plus every host's parameters, rather than replaying the
/// edit: a rename, for one, also rewrites the entry name each linked parameter carries for display.
/// </para>
/// </summary>
[Tool]
public partial class BlackboardPanel : VBoxContainer {
    /// <summary>Entries or links changed; the source needs saving and whatever shows it a refresh.</summary>
    [Signal]
    public delegate void BlackboardEditedEventHandler();

    [Signal]
    public delegate void EditRejectedEventHandler(string reason);

    /// <summary>Undo or redo reached an edit of another source, which should be opened right away.</summary>
    [Signal]
    public delegate void SourceRequestedEventHandler(Resource source);

    // Untyped so an assembly reload can restore it — see ReloadSafe.
    GodotObject _source;

    public IBlackboardSource Source => ReloadSafe.Get<IBlackboardSource>(ref _source);

    /// <summary>Leads every undo step's name, so the history tells which editor an edit came from.</summary>
    public string UndoPrefix { get; set; } = "Blackboard";

    /// <summary>What a parameter's menu says while its host is not part of the open source.</summary>
    public string NotOpenHint { get; set; } = "Open this in its editor to link entries";

    /// <summary>Entry objects this session has seen, so undoing a removal brings back the same object.</summary>
    readonly Dictionary<string, BlackboardEntry> _pool = [];

    VBoxContainer _rows;
    Label _empty;
    Label _count;
    MenuButton _add;
    PopupMenu _typeMenu;

    /// <summary>Entry whose type is being picked, or empty while picking the type of a new entry.</summary>
    string _typeTarget = "";

    bool _refreshQueued;

    const int PickNode = 1000;
    const int PickResource = 1001;

    static readonly (string Label, Variant.Type Type)[] TypeChoices = [
        ("bool", Variant.Type.Bool),
        ("int", Variant.Type.Int),
        ("float", Variant.Type.Float),
        ("String", Variant.Type.String),
        ("StringName", Variant.Type.StringName),
        ("Vector2", Variant.Type.Vector2),
        ("Vector3", Variant.Type.Vector3),
        ("Color", Variant.Type.Color),
        ("Variant (any)", Variant.Type.Nil),
    ];

    /// <summary>Every blackboard panel in the editor is in this group, whichever addon it belongs to.</summary>
    public const string PanelGroup = "misscore_blackboard_panels";

    public override void _Ready() {
        Name = "Blackboard";
        AddToGroup(PanelGroup);
        CustomMinimumSize = new Vector2(240, 0);
        AddThemeConstantOverride("separation", 6);

        var header = new HBoxContainer { Name = "Header" };
        header.AddThemeConstantOverride("separation", 6);
        // Keeps the icon off the panel's edge; with the separation it lines up with the cards' content below.
        header.AddChild(new Control { Name = "LeadingPad", CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore });
        header.AddChild(new TextureRect {
            Texture = ResourceLoader.Load<Texture2D>("res://addons/misscore/icons/blackboard.svg"),
            CustomMinimumSize = new Vector2(16, 16),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        });
        var title = new Label { Text = "Blackboard" };
        if (BlackboardStyles.BoldFont() is { } bold) title.AddThemeFontOverride("font", bold);
        header.AddChild(title);

        _count = new Label { Name = "Count", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _count.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.4f));
        header.AddChild(_count);

        var addIcon = BlackboardStyles.EditorIcon("Add");
        _add = new MenuButton { Text = addIcon == null ? "+" : "", Icon = addIcon, Flat = true, TooltipText = "Add an entry" };
        _add.GetPopup().Connect(PopupMenu.SignalName.AboutToPopup, new Callable(this, MethodName.OnAddAboutToPopup));
        FillTypeMenu(_add.GetPopup());
        _add.GetPopup().Connect(PopupMenu.SignalName.IdPressed, new Callable(this, MethodName.OnTypePicked));
        header.AddChild(_add);
        AddChild(header);

        var scroll = new ScrollContainer {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        _rows = new VBoxContainer { Name = "Rows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_rows);
        AddChild(scroll);

        _empty = new Label {
            Text = "No entries yet.\nAdd one with + above, or link a parameter to a new entry in the Inspector.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var emptyStyle = new StyleBoxFlat {
            BgColor = new Color(1, 1, 1, 0.025f),
            BorderColor = new Color(1, 1, 1, 0.12f),
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 14,
            ContentMarginBottom = 14,
        };
        emptyStyle.SetBorderWidthAll(1);
        emptyStyle.SetCornerRadiusAll(5);
        _empty.AddThemeStyleboxOverride("normal", emptyStyle);
        _empty.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.45f));
        _empty.AddThemeFontSizeOverride("font_size", 12);
        _rows.AddChild(_empty);

        _typeMenu = new PopupMenu { Name = "TypeMenu" };
        FillTypeMenu(_typeMenu);
        _typeMenu.Connect(PopupMenu.SignalName.IdPressed, new Callable(this, MethodName.OnTypePicked));
        AddChild(_typeMenu, false, InternalMode.Back);

        Refresh();
    }

    static void FillTypeMenu(PopupMenu menu) {
        menu.Clear();
        for (var i = 0; i < TypeChoices.Length; i++) {
            if (BlackboardStyles.TypeIcon(TypeChoices[i].Type, "") is { } icon) menu.AddIconItem(icon, TypeChoices[i].Label, i);
            else menu.AddItem(TypeChoices[i].Label, i);
        }
        menu.AddSeparator();
        AddPickItem(menu, "Node…", PickNode, "Node");
        AddPickItem(menu, "Resource…", PickResource, "Resource");
    }

    static void AddPickItem(PopupMenu menu, string label, int id, string iconName) {
        if (BlackboardStyles.EditorIcon(iconName) is { } icon) menu.AddIconItem(icon, label, id);
        else menu.AddItem(label, id);
    }

    public void ShowSource(IBlackboardSource source) {
        _source = source as GodotObject;
        _pool.Clear();
        EnsurePool();
        Refresh();
    }

    void EnsurePool() {
        if (Source == null) return;
        foreach (var entry in Source.Blackboard) {
            if (entry != null) _pool.TryAdd(entry.Id, entry);
        }
    }

    // ---- rows --------------------------------------------------------------------------------

    void QueueRefresh() {
        if (_refreshQueued) return;
        _refreshQueued = true;
        CallDeferred(MethodName.Refresh);
    }

    /// <summary>Rebuilds every row from the source.</summary>
    public void Refresh() {
        _refreshQueued = false;
        if (_rows == null) return;

        foreach (var child in _rows.GetChildren().Where(c => c != _empty).ToList()) {
            _rows.RemoveChild(child);
            child.QueueFree();
        }

        _add.Disabled = Source == null;
        var entries = Source?.Blackboard.Where(e => e != null).ToList() ?? [];
        _empty.Visible = Source != null && entries.Count == 0;
        _count.Text = entries.Count > 0 ? entries.Count.ToString() : "";

        foreach (var entry in entries) _rows.AddChild(BuildRow(entry));
    }

    Control BuildRow(BlackboardEntry entry) {
        var row = new BlackboardEntryRow();
        row.Build(entry);
        row.Connect(BlackboardEntryRow.SignalName.RenameRequested, new Callable(this, MethodName.OnRenameRequested));
        row.Connect(BlackboardEntryRow.SignalName.TypeRequested, new Callable(this, MethodName.OnTypeRequested));
        row.Connect(BlackboardEntryRow.SignalName.RemoveRequested, new Callable(this, MethodName.RemoveEntry));
        row.Connect(BlackboardEntryRow.SignalName.DefaultEdited, new Callable(this, MethodName.SetEntryDefault));
        return row;
    }

    void OnRenameRequested(string id, string name) {
        var entry = Source?.FindEntry(id);
        if (entry != null && name.Trim() != entry.Name) RenameEntry(id, name);
    }

    void OnAddAboutToPopup() => _typeTarget = "";

    void OnTypeRequested(string id, Vector2 screenPosition) {
        _typeTarget = id;
        _typeMenu.Position = (Vector2I) screenPosition;
        _typeMenu.ResetSize();
        _typeMenu.Popup();
    }

    void OnTypePicked(long id) {
        if (id is PickNode or PickResource) {
            if (!Engine.IsEditorHint()) return;
            var baseType = id == PickNode ? "Node" : "Resource";
            EditorInterface.Singleton.PopupCreateDialog(new Callable(this, MethodName.OnClassPicked), baseType, "",
                $"Blackboard entry type ({baseType})");
            return;
        }
        if (id < 0 || id >= TypeChoices.Length) return;
        ApplyPickedType(TypeChoices[id].Type, "");
    }

    void OnClassPicked(string className) {
        // Script classes come back as their script path; the entry's default name wants the class name.
        if (!string.IsNullOrEmpty(className)) ApplyPickedType(Variant.Type.Object, BbTypes.ClassNameOf(className));
    }

    void ApplyPickedType(Variant.Type type, string className) {
        if (string.IsNullOrEmpty(_typeTarget)) AddEntry(type, className);
        else SetEntryType(_typeTarget, type, className);
        _typeTarget = "";
    }

    // ---- edits -------------------------------------------------------------------------------

    public BlackboardEntry AddEntry(Variant.Type type, string className = "", string name = null) {
        if (Source == null) return null;

        var entry = new BlackboardEntry {
            Name = UniqueName(string.IsNullOrWhiteSpace(name) ? DefaultNameFor(type, className) : name),
            VariantType = type,
            ClassName = type == Variant.Type.Object ? className : "",
            Default = BbTypes.DefaultOf(type),
        };
        _pool[entry.Id] = entry;

        Commit($"{UndoPrefix}: add blackboard entry {entry.Name}", () => Source.Blackboard.Add(entry));
        return entry;
    }

    public bool RenameEntry(string id, string name) {
        var entry = Source?.FindEntry(id);
        if (entry == null) return false;

        name = name?.Trim() ?? "";
        if (name == entry.Name) return true;

        var problem = string.IsNullOrEmpty(name) ? "An entry needs a name."
            : name.Contains('/') ? "Entry names cannot contain '/'."
            : Source.FindEntryByName(name) != null ? $"There already is an entry called {name}."
            : null;
        if (problem != null) {
            EmitSignal(SignalName.EditRejected, problem);
            QueueRefresh();
            return false;
        }

        Commit($"{UndoPrefix}: rename blackboard entry to {name}", () => {
            entry.Name = name;
            foreach (var host in Source.ParamHosts()) {
                foreach (var (_, param) in BbParams.On(host)) {
                    if (param.EntryId == id) param.EntryName = name;
                }
            }
        });
        return true;
    }

    /// <summary>Changes an entry's type. The default is kept when it still fits, otherwise reset.</summary>
    public void SetEntryType(string id, Variant.Type type, string className = "") {
        var entry = Source?.FindEntry(id);
        if (entry == null) return;
        className = type == Variant.Type.Object ? className : "";
        if (entry.VariantType == type && entry.ClassName == className) return;

        Commit($"{UndoPrefix}: change type of {entry.Name}", () => {
            entry.VariantType = type;
            entry.ClassName = className;
            if (type != Variant.Type.Nil && entry.Default.VariantType != type) entry.Default = BbTypes.DefaultOf(type);
        });
    }

    public void SetEntryDefault(string id, Variant value) {
        var entry = Source?.FindEntry(id);
        if (entry == null || BbTypes.SameValue(entry.Default, value)) return;

        // Merged with the previous step while the same entry keeps changing, so one slider drag is one undo.
        Commit($"{UndoPrefix}: set default of {entry.Id}", () => entry.Default = value, rebuildRows: false,
            merge: UndoRedo.MergeMode.Ends);
    }

    /// <summary>
    /// Removes an entry. Parameters linked to it keep the link — undo would otherwise have to restore
    /// them one by one — and show a warning on their box until they are relinked or the removal undone.
    /// </summary>
    public void RemoveEntry(string id) {
        var entry = Source?.FindEntry(id);
        if (entry == null) return;
        Commit($"{UndoPrefix}: remove blackboard entry {entry.Name}", () => Source.Blackboard.Remove(entry));
    }

    public void LinkParam(IBbParamHost host, string member, string entryId) {
        var param = ParamOf(host, member, out _);
        var entry = Source?.FindEntry(entryId);
        if (param == null || entry == null) return;

        Commit($"{UndoPrefix}: link {member} to {entry.Name}", () => {
            param.EntryId = entry.Id;
            param.EntryName = entry.Name;
        });
    }

    public void UnlinkParam(IBbParamHost host, string member) {
        var param = ParamOf(host, member, out _);
        if (param is not { IsLinked: true }) return;

        Commit($"{UndoPrefix}: unlink {member}", () => {
            param.EntryId = "";
            param.EntryName = "";
        });
    }

    /// <summary>
    /// Adds an entry shaped after a parameter — its type, and its current fixed value as the default —
    /// and links the parameter to it, as one undo step.
    /// </summary>
    public BlackboardEntry CreateEntryForParam(IBbParamHost host, string member, string name) {
        var param = ParamOf(host, member, out var info);
        if (param == null || Source == null) return null;

        var (type, className) = BbTypes.Describe(info.ValueType);
        var literal = param.Literal;
        if (type == Variant.Type.Nil && literal.VariantType != Variant.Type.Nil) type = literal.VariantType;

        var entry = new BlackboardEntry {
            Name = UniqueName(string.IsNullOrWhiteSpace(name) ? member : name.Trim().Replace("/", "")),
            VariantType = type,
            ClassName = className,
            Default = info.EntryOnly || literal.VariantType == Variant.Type.Nil ? BbTypes.DefaultOf(type) : literal,
        };
        _pool[entry.Id] = entry;

        Commit($"{UndoPrefix}: new blackboard entry {entry.Name} for {member}", () => {
            Source.Blackboard.Add(entry);
            param.EntryId = entry.Id;
            param.EntryName = entry.Name;
        });
        return entry;
    }

    IBbParam ParamOf(IBbParamHost host, string member, out BbParamMember info) {
        info = host == null ? null : BbParams.Find(host.GetType(), member);
        return info?.On(host);
    }

    public bool Contains(IBbParamHost host) => host != null && Source != null && Source.ParamHosts().Contains(host);

    /// <summary>
    /// Whether this panel is the one to edit the parameters of <paramref name="host"/>. Several
    /// addons each bring a panel and an Inspector plugin, and a parameter must get one editor, not
    /// one per addon: the panel whose source holds the host claims it, and a host in no open source
    /// goes to the first panel, which then only says where to open it.
    /// <para>
    /// The panels find each other through a node group rather than a static list, since group
    /// membership is the engine's and survives an assembly reload.
    /// </para>
    /// </summary>
    public bool Claims(IBbParamHost host) {
        if (host == null || !IsInsideTree()) return false;

        BlackboardPanel first = null;
        foreach (var node in GetTree().GetNodesInGroup(PanelGroup)) {
            if (node is not BlackboardPanel panel) continue;
            first ??= panel;
            if (panel.Contains(host)) return ReferenceEquals(panel, this);
        }
        return ReferenceEquals(first, this);
    }

    public string UniqueName(string wanted) {
        wanted = string.IsNullOrWhiteSpace(wanted) ? "Entry" : wanted.Trim();
        if (Source?.FindEntryByName(wanted) == null) return wanted;
        for (var i = 2; ; i++) {
            if (Source.FindEntryByName($"{wanted}{i}") == null) return $"{wanted}{i}";
        }
    }

    static string DefaultNameFor(Variant.Type type, string className) {
        var label = BbTypes.Label(type, className);
        return type == Variant.Type.Nil ? "Value" : char.ToUpperInvariant(label[0]) + label[1..];
    }

    // ---- undo/redo ---------------------------------------------------------------------------

    void Commit(string actionName, Action mutate, bool rebuildRows = true, UndoRedo.MergeMode merge = UndoRedo.MergeMode.Disable) {
        var before = TakeSnapshot();
        mutate();
        var after = TakeSnapshot();

        var undoRedo = Engine.IsEditorHint() ? EditorInterface.Singleton?.GetEditorUndoRedo() : null;
        if (undoRedo != null) {
            // No history context of its own: whoever shows the source tracks what is unsaved.
            undoRedo.CreateAction(actionName, merge, Source as GodotObject, false, false);
            undoRedo.AddDoMethod(this, MethodName.RestoreSnapshot, after);
            undoRedo.AddUndoMethod(this, MethodName.RestoreSnapshot, before);
            undoRedo.CommitAction(false);
        }

        Changed(rebuildRows);
    }

    internal Godot.Collections.Dictionary TakeSnapshot() {
        var entries = new Godot.Collections.Array();
        foreach (var entry in Source.Blackboard) {
            if (entry == null) continue;
            entries.Add(new Godot.Collections.Dictionary {
                { "id", entry.Id },
                { "name", entry.Name },
                { "type", (int) entry.VariantType },
                { "class", entry.ClassName },
                { "default", entry.Default },
            });
        }

        var parameters = new Godot.Collections.Dictionary();
        foreach (var host in Source.ParamHosts()) {
            var stored = new Godot.Collections.Dictionary();
            foreach (var (member, param) in BbParams.On(host)) stored[member.Name] = BbParams.ToStorage(param);
            if (stored.Count > 0) parameters[host.Id] = stored;
        }

        return new Godot.Collections.Dictionary { { "source", Source as GodotObject }, { "entries", entries }, { "params", parameters } };
    }

    public void RestoreSnapshot(Godot.Collections.Dictionary snapshot) {
        // Another source's edit opens that source rather than landing in this one.
        if (snapshot.TryGetValue("source", out var owner) && owner.AsGodotObject() is IBlackboardSource other
            && !ReferenceEquals(other, Source)) {
            EmitSignal(SignalName.SourceRequested, owner);
            if (!ReferenceEquals(other, Source)) return;
        }
        if (Source == null) return;
        EnsurePool();

        var entries = new Godot.Collections.Array<BlackboardEntry>();
        foreach (var item in snapshot["entries"].AsGodotArray()) {
            var data = item.AsGodotDictionary();
            var id = data["id"].AsString();
            if (!_pool.TryGetValue(id, out var entry)) {
                entry = new BlackboardEntry { Id = id };
                _pool[id] = entry;
            }
            entry.Name = data["name"].AsString();
            entry.VariantType = (Variant.Type) data["type"].AsInt32();
            entry.ClassName = data["class"].AsString();
            entry.Default = data["default"];
            entries.Add(entry);
        }
        Source.Blackboard = entries;

        var parameters = snapshot["params"].AsGodotDictionary();
        foreach (var host in Source.ParamHosts()) {
            if (!parameters.TryGetValue(host.Id, out var stored)) continue;
            var values = stored.AsGodotDictionary();
            foreach (var (member, param) in BbParams.On(host)) {
                if (values.TryGetValue(member.Name, out var value)) BbParams.FromStorage(param, value);
            }
        }

        Changed(rebuildRows: true);
    }

    void Changed(bool rebuildRows) {
        if (rebuildRows) {
            QueueRefresh();
            // The Inspector rebuilds from this, so a parameter's link menu and label follow at once.
            foreach (var host in Source.ParamHosts()) (host as GodotObject)?.NotifyPropertyListChanged();
        }
        // Whatever uses the source in an open scene may list the entries in its Inspector.
        (Source as Resource)?.EmitChanged();
        EmitSignal(SignalName.BlackboardEdited);
    }
}
#endif
