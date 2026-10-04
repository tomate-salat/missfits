#if TOOLS
using System;
using System.Linq;
using Godot;

namespace Misscore.Editor;

/// <summary>
/// Searchable picker listing the node types found in the project, in groups and with their icons.
/// Used to create a node, to pick the type an existing node should be replaced with, or — narrowed
/// to one kind — to pick what can go into a particular place.
/// </summary>
[Tool]
public partial class CreateNodeDialog : ConfirmationDialog {
    /// <summary>Raised with the full .NET name of the chosen type.</summary>
    [Signal]
    public delegate void TypeChosenEventHandler(string typeName);

    LineEdit _filter;
    Tree _tree;

    /// <summary>In replace mode, the type being replaced — pointless to offer it again.</summary>
    string _excludedType = "";

    /// <summary>Group listed first, so the likely replacements are at the top.</summary>
    string _preferredGroup = "";

    /// <summary>Base type every listed type must derive from, by name so it survives a reload; empty for any.</summary>
    string _requiredName = "";

    public override void _Ready() {
        MinSize = new Vector2I(420, 460);

        var box = new VBoxContainer();
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(box);

        _filter = new LineEdit { PlaceholderText = "Filter…", ClearButtonEnabled = true };
        box.AddChild(_filter);

        _tree = new Tree {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HideRoot = true,
            AllowReselect = true,
        };
        box.AddChild(_tree);

        // Native method callables, not +=: a delegate connection does not survive the assembly
        // reload that pressing play triggers.
        _filter.Connect(LineEdit.SignalName.TextChanged, new Callable(this, MethodName.OnFilterChanged));
        _filter.Connect(LineEdit.SignalName.TextSubmitted, new Callable(this, MethodName.OnFilterSubmitted));
        _filter.Connect(Control.SignalName.GuiInput, new Callable(this, MethodName.OnFilterGuiInput));
        _tree.Connect(Control.SignalName.GuiInput, new Callable(this, MethodName.OnTreeGuiInput));
        _tree.Connect(Tree.SignalName.ItemActivated, new Callable(this, MethodName.ConfirmSelection));
        Connect(AcceptDialog.SignalName.Confirmed, new Callable(this, MethodName.ConfirmSelection));
    }

    /// <param name="required">When set, only types deriving from it are listed — e.g. what a list can hold.</param>
    public void Open(Vector2 graphPosition, Type required = null) {
        NodeTypeRegistry.Refresh();
        Title = required == null ? "Create behavior node" : $"Add {required.Name} to list";
        OkButtonText = required == null ? "Create" : "Add";
        _excludedType = "";
        _preferredGroup = "";
        _requiredName = required?.FullName ?? "";
        Present(graphPosition);
    }

    /// <summary>
    /// Opens the picker for one kind of node only — the actions a state can run, say — under a
    /// title and a button that say what the choice is for.
    /// </summary>
    public void OpenFor(string title, string okText, Type required) {
        NodeTypeRegistry.Refresh();
        Title = title;
        OkButtonText = okText;
        _excludedType = "";
        _preferredGroup = "";
        _requiredName = required?.FullName ?? "";
        Present(Vector2.Zero);
    }

    public void OpenForReplace(MissNode node) {
        NodeTypeRegistry.Refresh();
        Title = $"Replace {node.GetLabel()} with";
        OkButtonText = "Replace";
        _excludedType = node.GetType().FullName;
        _requiredName = "";
        _preferredGroup = NodeTypeRegistry.Find(node.GetType())?.Group ?? "";
        Present(node.GraphPosition);
    }

    void Present(Vector2 graphPosition) {
        PendingPosition = graphPosition;
        Populate();
        PopupCentered(MinSize);
        _filter.Clear();
        _filter.GrabFocus();
    }

    /// <summary>Where the created node should land, in graph coordinates.</summary>
    public Vector2 PendingPosition { get; private set; }

    void OnFilterChanged(string text) => Populate();

    void Populate() {
        _tree.Clear();

        var root = _tree.CreateItem();
        var needle = _filter?.Text?.Trim() ?? "";
        var groups = NodeTypeRegistry.Groups.OrderBy(g => g == _preferredGroup ? 0 : 1);
        // Looked up in our own assembly: Type.GetType would search the default load context, which
        // the editor's reloadable assembly is not part of.
        var required = string.IsNullOrEmpty(_requiredName) ? null : typeof(MissNode).Assembly.GetType(_requiredName);

        foreach (var group in groups) {
            var types = NodeTypeRegistry.InGroup(group)
                .Where(type => type.Type.FullName != _excludedType)
                .Where(type => required == null || required.IsAssignableFrom(type.Type))
                .Where(type => needle.Length == 0 || Matches(type, needle))
                .ToList();
            if (types.Count == 0) continue;

            AddLevel(CreateGroupItem(root, group), types, 0);
        }

        // With an active filter, drop straight onto the first match so Enter just works.
        if (needle.Length > 0) SelectItem(FirstType());
    }

    /// <summary>
    /// Lists <paramref name="types"/> below <paramref name="parent"/>: the sub-group folders at
    /// <paramref name="depth"/> first, each filled the same way, then the types filed right here — in
    /// the registry's order, lists first.
    /// </summary>
    void AddLevel(TreeItem parent, System.Collections.Generic.List<NodeTypeInfo> types, int depth) {
        var folders = types
            .Where(t => t.SubGroup.Length > depth)
            .GroupBy(t => t.SubGroup[depth], StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders) {
            var folderItem = CreateGroupItem(parent, folder.First().SubGroup[depth]);
            // Folded away until needed — but open while filtering, so every match is in view.
            folderItem.Collapsed = _filter.Text.Trim().Length == 0;
            AddLevel(folderItem, [.. folder], depth + 1);
        }

        foreach (var type in types.Where(t => t.SubGroup.Length == depth)) {
            var item = _tree.CreateItem(parent);
            item.SetText(0, type.Name);
            item.SetTooltipText(0, type.Name == type.Type.Name ? type.Description : $"{type.Type.Name}\n{type.Description}");
            // The type travels on the item itself rather than in a side dictionary, which
            // would be one more piece of C#-only state for a reload to wipe.
            item.SetMetadata(0, type.Type.FullName);

            if (!string.IsNullOrEmpty(type.IconPath)) {
                var icon = ResourceLoader.Load<Texture2D>(type.IconPath);
                if (icon != null) item.SetIcon(0, icon);
            }

            if (!type.IsGlobalClass) {
                item.SetCustomColor(0, new Color("#e0b400"));
                item.SetTooltipText(0,
                    $"{type.Description}\n\nMissing [GlobalClass] — this node cannot be saved into a resource.");
            }
        }
    }

    /// <summary>Enter in the filter takes the selected type, or the first one listed when none is.</summary>
    public void OnFilterSubmitted(string text) {
        if (_tree.GetSelected() == null) SelectItem(FirstType());
        ConfirmSelection();
    }

    /// <summary>Arrow down leaves the filter for the list, landing on the first type.</summary>
    public void OnFilterGuiInput(InputEvent @event) {
        if (@event is not InputEventKey { Pressed: true, Keycode: Key.Down }) return;

        var first = FirstType();
        if (first == null) return;

        _filter.AcceptEvent();
        _tree.GrabFocus();
        SelectItem(first);
    }

    /// <summary>Arrow up on the first type goes back to the filter, so the two feel like one control.</summary>
    public void OnTreeGuiInput(InputEvent @event) {
        if (@event is not InputEventKey { Pressed: true, Keycode: Key.Up }) return;
        if (_tree.GetSelected() is not { } selected || selected != FirstType()) return;

        _tree.AcceptEvent();
        _filter.GrabFocus();
        _filter.CaretColumn = _filter.Text.Length;
    }

    /// <summary>
    /// A type is found by its shown name, its class name, or the name of a sub-group it is in — so
    /// typing "enemies" lists everything filed under Enemies.
    /// </summary>
    static bool Matches(NodeTypeInfo type, string needle)
        => type.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
           || type.Type.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
           || type.SubGroup.Any(level => level.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The first selectable entry in view, top to bottom — group and folder headers are not, and
    /// folded folders are skipped. When everything is folded away, the first entry at all.
    /// </summary>
    TreeItem FirstType() => FirstSelectable(_tree.GetRoot(), skipCollapsed: true) ?? FirstSelectable(_tree.GetRoot(), skipCollapsed: false);

    static TreeItem FirstSelectable(TreeItem parent, bool skipCollapsed) {
        for (var item = parent?.GetFirstChild(); item != null; item = item.GetNext()) {
            if (item.IsSelectable(0)) return item;
            if (skipCollapsed && item.Collapsed) continue;
            if (FirstSelectable(item, skipCollapsed) is { } nested) return nested;
        }
        return null;
    }

    void SelectItem(TreeItem item) {
        if (item == null) return;
        // A selection hidden inside a folded folder would be confirmed by Enter unseen.
        for (var parent = item.GetParent(); parent != null; parent = parent.GetParent()) parent.Collapsed = false;
        item.Select(0);
        _tree.ScrollToItem(item);
    }

    TreeItem CreateGroupItem(TreeItem parent, string group) {
        var item = _tree.CreateItem(parent);
        item.SetText(0, group);
        item.SetSelectable(0, false);
        item.SetCustomColor(0, new Color(1, 1, 1, 0.5f));
        return item;
    }

    void ConfirmSelection() {
        var typeName = _tree.GetSelected()?.GetMetadata(0).AsString();
        if (string.IsNullOrEmpty(typeName)) return;

        Hide();
        EmitSignal(SignalName.TypeChosen, typeName);
    }
}
#endif
