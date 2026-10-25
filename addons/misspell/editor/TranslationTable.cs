#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misspell.Editor;

/// <summary>
/// The spreadsheet: one row per message, one column per language, and below a plural message one
/// row per further plural form — as many as the language with the most forms needs. An empty
/// plural cell says which numbers it is for, so the right text lands in the right form.
/// <para>
/// Cells are edited in place like in a spreadsheet: type to replace, Enter or F2 or a double-click
/// to change. While editing, Enter keeps the text and goes down, Tab goes right, Shift+Enter breaks
/// the line and Escape cancels. Delete empties a cell, Ctrl+C and Ctrl+V copy and paste one.
/// </para>
/// </summary>
[Tool]
public partial class TranslationTable : Tree {
    /// <summary>Something was edited; the panel updates title, numbers and the details.</summary>
    [Signal]
    public delegate void EditedEventHandler();

    /// <summary>An edit was refused, with why.</summary>
    [Signal]
    public delegate void EditRejectedEventHandler(string message);

    /// <summary>A language's column title was right-clicked: its settings are wanted.</summary>
    [Signal]
    public delegate void LanguageMenuRequestedEventHandler(string path);

    public enum Filter { All, Untranslated, Fuzzy, Problems, Plural }

    public enum ColumnKind { None, Context, Source, Language }

    /// <summary>Plain C#, so gone after an assembly reload; the panel hands it over again.</summary>
    public CatalogSet Catalogs;

    public string Search = "";
    public Filter ShowOnly = Filter.All;
    public bool ShowContext = true;

    /// <summary>
    /// Sources that are keys laid out as paths — <c>welcome/option/enter</c> — are shown as a tree:
    /// a row per folder, which can be folded, with how far each language is inside it.
    /// </summary>
    public bool TreeView;

    public const char PathSeparator = '/';

    /// <summary>Marks the metadata of a folder's row, so no message's key is ever mistaken for it.</summary>
    const string GroupMark = "\u0001";

    readonly List<ColumnKind> _kinds = [];
    readonly List<CatalogFile> _columnFiles = [];
    readonly Dictionary<string, TreeItem> _items = [];
    readonly Dictionary<string, TreeItem> _groups = [];
    readonly Dictionary<string, List<CatalogRow>> _groupRows = [];

    /// <summary>Folders the user folded, by path; they stay folded when the table is rebuilt.</summary>
    readonly HashSet<string> _collapsed = [];
    int _sortColumn = -1;
    bool _sortDescending;

    TextEdit _editor;
    TreeItem _editItem;
    int _editColumn = -1;
    bool _committing;
    PopupMenu _menu;
    ConfirmationDialog _deleteDialog;
    string _pendingDelete = "";

    // Colours of the cells, kept faint so text stays readable in light and dark themes.
    static readonly Color Missing = new(1f, 0.35f, 0.3f, 0.13f);
    static readonly Color FuzzyTint = new(1f, 0.72f, 0.2f, 0.2f);
    static readonly Color Unused = new(0.5f, 0.5f, 0.5f, 0.08f);

    public override void _Ready() {
        HideRoot = true;
        SelectMode = SelectModeEnum.Single;
        ColumnTitlesVisible = true;
        AllowRmbSelect = true;
        // Only shows while something is dragged, which only the tree view allows.
        DropModeFlags = (int) DropModeFlagsEnum.OnItem;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        // More air between rows and inside cells, and no lines between parent and child: a table
        // to read, not a node tree.
        AddThemeConstantOverride("v_separation", GetThemeConstant("v_separation") + 6);
        AddThemeConstantOverride("inner_item_margin_left", 6);
        AddThemeConstantOverride("inner_item_margin_right", 6);
        AddThemeConstantOverride("draw_relationship_lines", 0);
        AddThemeConstantOverride("icon_max_width", 16);

        Connect(Tree.SignalName.ItemActivated, new Callable(this, MethodName.OnItemActivated));
        Connect(Tree.SignalName.ColumnTitleClicked, new Callable(this, MethodName.OnColumnTitleClicked));
        Connect(Tree.SignalName.ItemMouseSelected, new Callable(this, MethodName.OnItemMouseSelected));
        Connect(Tree.SignalName.ItemCollapsed, new Callable(this, MethodName.OnItemCollapsed));

        _editor = new TextEdit {
            Name = "CellEditor",
            Visible = false,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            ScrollFitContentHeight = true,
        };
        _editor.Connect(Control.SignalName.GuiInput, new Callable(this, MethodName.OnEditorInput));
        _editor.Connect(Control.SignalName.FocusExited, new Callable(this, MethodName.OnEditorFocusExited));
        _editor.Connect(TextEdit.SignalName.TextChanged, new Callable(this, MethodName.FitEditor));
        AddChild(_editor);

        _menu = new PopupMenu { Name = "CellMenu" };
        _menu.Connect(PopupMenu.SignalName.IdPressed, new Callable(this, MethodName.OnMenuId));
        AddChild(_menu);

        _deleteDialog = new ConfirmationDialog { Name = "DeleteFolderDialog", Title = "Delete folder", OkButtonText = "Delete" };
        _deleteDialog.Connect(AcceptDialog.SignalName.Confirmed, new Callable(this, MethodName.OnDeleteConfirmed));
        AddChild(_deleteDialog);
    }

    // ---- building ----------------------------------------------------------------------------

    /// <summary>Which column shows what, and the file of a language column.</summary>
    public ColumnKind KindOf(int column) => column >= 0 && column < _kinds.Count ? _kinds[column] : ColumnKind.None;

    public CatalogFile FileOf(int column) => column >= 0 && column < _columnFiles.Count ? _columnFiles[column] : null;

    public int ColumnOf(CatalogFile file) => _columnFiles.IndexOf(file);

    public int ColumnOf(ColumnKind kind) => _kinds.IndexOf(kind);

    /// <summary>Rebuilds every row and column, keeping the selected cell where it can.</summary>
    public void Rebuild() {
        CancelEdit();
        var (selectedKey, selectedForm, selectedColumn) = SelectedCellKey();
        var selectedFile = FileOf(selectedColumn);
        var selectedKind = KindOf(selectedColumn);
        var scroll = GetScroll();

        Clear();
        _items.Clear();
        _groups.Clear();
        _groupRows.Clear();
        _kinds.Clear();
        _columnFiles.Clear();
        if (Catalogs == null) {
            Columns = 1;
            SetColumnTitle(0, "");
            return;
        }

        // The context goes first, as it is the first half of what identifies a text. But a Tree
        // indents and draws its fold arrows in the first column only: in the tree view that has to
        // be the source, where the folders are.
        if (TreeView) _kinds.Add(ColumnKind.Source);
        if (ShowContext) _kinds.Add(ColumnKind.Context);
        if (!TreeView) _kinds.Add(ColumnKind.Source);
        foreach (var _ in Catalogs.Languages) _kinds.Add(ColumnKind.Language);
        _columnFiles.AddRange(_kinds.Select(_ => (CatalogFile) null));
        var first = _kinds.IndexOf(ColumnKind.Language);
        var languages = Catalogs.Languages.ToList();
        for (var i = 0; i < languages.Count; i++) _columnFiles[first + i] = languages[i];

        Columns = _kinds.Count;
        for (var c = 0; c < Columns; c++) {
            SetColumnClipContent(c, true);
            switch (_kinds[c]) {
                case ColumnKind.Context:
                    SetColumnExpand(c, true);
                    SetColumnExpandRatio(c, 1);
                    SetColumnCustomMinimumWidth(c, 70);
                    break;
                default:
                    SetColumnExpand(c, true);
                    SetColumnExpandRatio(c, 3);
                    SetColumnCustomMinimumWidth(c, _kinds[c] == ColumnKind.Source && TreeView ? 220 : 140);
                    break;
            }
        }
        UpdateTitles();

        var root = CreateItem();
        var maxForms = Catalogs.MaxForms;
        foreach (var row in SortedRows(VisibleRows())) {
            var item = CreateItem(TreeView ? FolderOf(root, row) : root);
            _items[row.Key] = item;
            Fill(item, row, 0);
            if (!row.IsPlural) continue;
            for (var form = 1; form < maxForms; form++) Fill(CreateItem(item), row, form);
        }
        foreach (var (path, group) in _groups) FillGroup(group, path);

        RestoreSelection(selectedKey, selectedForm, selectedKind == ColumnKind.Language ? ColumnOf(selectedFile) : ColumnOf(selectedKind));
        CallDeferred(MethodName.SetScroll, scroll);
    }

    /// <summary>The folders of a key — <c>a/b/c</c> lies in <c>a</c> and <c>a/b</c> — or none.</summary>
    public static string[] FoldersOf(string id) {
        var parts = (id ?? "").Split(PathSeparator);
        var folders = new string[Math.Max(0, parts.Length - 1)];
        for (var i = 0; i < folders.Length; i++) folders[i] = i == 0 ? parts[0] : $"{folders[i - 1]}{PathSeparator}{parts[i]}";
        return folders;
    }

    /// <summary>The row of the folder a message goes in, made along with the folders above it as needed.</summary>
    TreeItem FolderOf(TreeItem root, CatalogRow row) {
        var parent = root;
        foreach (var path in FoldersOf(row.Id)) {
            if (!_groups.TryGetValue(path, out var group)) {
                group = CreateItem(parent);
                group.SetMetadata(0, GroupMark + path);
                group.SetMetadata(1, 0);
                group.Collapsed = _collapsed.Contains(path);
                _groups[path] = group;
                _groupRows[path] = [];
            }
            _groupRows[path].Add(row);
            parent = group;
        }
        return parent;
    }

    /// <summary>
    /// A folder's row: a folder icon and its name, and per language how many of the texts inside
    /// are translated — green when all are, amber while some are, red while none is.
    /// </summary>
    void FillGroup(TreeItem item, string path) {
        var rows = _groupRows[path];
        var problems = 0;
        for (var c = 0; c < _kinds.Count; c++) {
            item.ClearCustomBgColor(c);
            item.ClearCustomColor(c);
            item.SetIcon(c, null);
            item.SetText(c, "");
            item.SetTooltipText(c, "");
            switch (_kinds[c]) {
                case ColumnKind.Source:
                    item.SetText(c, path[(path.LastIndexOf(PathSeparator) + 1)..]);
                    item.SetTooltipText(c, $"{path}{PathSeparator} — {rows.Count} text{(rows.Count == 1 ? "" : "s")}");
                    break;
                case ColumnKind.Language:
                    var file = _columnFiles[c];
                    var forms = file.Catalog.Plurals.Count;
                    var done = rows.Count(r => Catalogs.EntryOf(r, file) is { Fuzzy: false } entry && entry.IsTranslated(forms));
                    problems += rows.Sum(r => Catalogs.ProblemsOf(r, file).Count);
                    // Coloured text rather than a drawn bar: Tree's custom-drawn cells crash the
                    // editor when the table is rebuilt (seen in Godot 4.7.2).
                    item.SetText(c, $"{done}/{rows.Count}");
                    item.SetCustomColor(c, done == rows.Count ? Success : done > 0 ? Partial : Failed);
                    item.SetTooltipText(c, $"{file.Label}: {done} of {rows.Count} translated in {path}{PathSeparator}");
                    break;
            }
        }

        var source = ColumnOf(ColumnKind.Source);
        if (problems > 0) {
            SetIcon(item, source, "StatusWarning", GetThemeColor("warning_color", "Editor"));
            item.SetTooltipText(source, $"{item.GetTooltipText(source)}\n{problems} problem{(problems == 1 ? "" : "s")} inside");
        } else {
            SetIcon(item, source, "Folder", HasThemeColor("folder_icon_color", "FileDialog") ? GetThemeColor("folder_icon_color", "FileDialog") : Colors.White);
        }
    }

    static readonly Color Success = new(0.45f, 0.8f, 0.5f);
    static readonly Color Partial = new(0.95f, 0.7f, 0.3f);
    static readonly Color Failed = new(1f, 0.45f, 0.4f);

    /// <summary>An icon of the editor's theme in a cell, or none outside the editor.</summary>
    void SetIcon(TreeItem item, int column, string name, Color modulate) {
        if (column < 0 || !HasThemeIcon(name, "EditorIcons")) return;
        item.SetIcon(column, GetThemeIcon(name, "EditorIcons"));
        item.SetIconModulate(column, modulate);
    }

    /// <summary>The path of a folder's row, or null for a message's row.</summary>
    public static string GroupPathOf(TreeItem item) {
        var key = item?.GetMetadata(0).AsString() ?? "";
        return key.StartsWith(GroupMark, StringComparison.Ordinal) ? key[GroupMark.Length..] : null;
    }

    public TreeItem GroupItem(string path) => _groups.GetValueOrDefault(path);

    /// <summary>
    /// Where a new text would go in the tree: the selected folder, or the folder of the selected
    /// text — as a path ending in the separator. Null outside the tree view or with nothing to go by.
    /// </summary>
    public string SelectedFolder() {
        if (!TreeView) return null;
        var item = GetSelected();
        if (GroupPathOf(item) is { } path) return path + PathSeparator;
        var folders = FoldersOf(RowOf(item)?.Id);
        return folders.Length > 0 ? folders[^1] + PathSeparator : null;
    }

    void OnItemCollapsed(TreeItem item) {
        if (GroupPathOf(item) is not { } path) return;
        if (item.Collapsed) _collapsed.Add(path);
        else _collapsed.Remove(path);
    }

    // ---- moving by drag and drop -------------------------------------------------------------

    const string DragKey = "misspell_move";

    /// <summary>
    /// What dragging a row of the tree carries: a text by its key, or a folder by its path. A row
    /// of a further plural form carries its text. Nothing outside the tree view.
    /// </summary>
    public Godot.Collections.Dictionary DragDataFor(TreeItem item) {
        if (!TreeView || Catalogs == null || item == null) return null;
        if (GroupPathOf(item) is { } folder) return new() { [DragKey] = folder, ["folder"] = true };
        return RowOf(item) is { } row ? new() { [DragKey] = row.Key, ["folder"] = false } : null;
    }

    public override Variant _GetDragData(Vector2 atPosition) {
        if (IsEditing) return default;
        var data = DragDataFor(GetItemAtPosition(atPosition));
        if (data == null) return default;

        var preview = new Label { Text = data["folder"].AsBool() ? $"{data[DragKey].AsString()}{PathSeparator}" : RowOf(GetItemAtPosition(atPosition)).Id };
        preview.AddThemeColorOverride("font_color", GetThemeColor("font_color", "Tree"));
        SetDragPreview(preview);
        return data;
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) => MovesFor(data, FolderAt(atPosition)) is { Count: > 0 };

    public override void _DropData(Vector2 atPosition, Variant data) => Move(data, FolderAt(atPosition));

    /// <summary>
    /// Where a drop lands: on a folder, in it; on a text, beside it in its folder; below the
    /// rows, at the top level.
    /// </summary>
    public string FolderAt(Vector2 position) {
        var item = GetItemAtPosition(position);
        if (item == null) return "";
        if (GroupPathOf(item) is { } folder) return folder;
        var folders = FoldersOf(RowOf(item)?.Id);
        return folders.Length > 0 ? folders[^1] : "";
    }

    /// <summary>
    /// The new keys of what is dragged when dropped in <paramref name="target"/>: what it lies in
    /// is swapped for the target, the rest of each key stays. Null when there is nothing to move —
    /// already there, or a folder dropped into itself.
    /// </summary>
    public List<(CatalogRow Row, string Id)> MovesFor(Variant data, string target) {
        if (!TreeView || Catalogs == null || data.VariantType != Variant.Type.Dictionary) return null;
        var dict = data.AsGodotDictionary();
        if (!dict.ContainsKey(DragKey)) return null;
        var dragged = dict[DragKey].AsString();
        var isFolder = dict["folder"].AsBool();

        List<CatalogRow> rows;
        string parent;
        if (isFolder) {
            if (target == dragged || target.StartsWith(dragged + PathSeparator, StringComparison.Ordinal)) return null;
            rows = [.. Catalogs.Rows.Where(r => r.Id.StartsWith(dragged + PathSeparator, StringComparison.Ordinal))];
            var folders = FoldersOf(dragged + PathSeparator + "x");
            parent = folders.Length > 1 ? folders[^2] : "";
        } else {
            var row = Catalogs.Rows.FirstOrDefault(r => r.Key == dragged);
            if (row == null) return null;
            rows = [row];
            var folders = FoldersOf(row.Id);
            parent = folders.Length > 0 ? folders[^1] : "";
        }
        if (parent == target || rows.Count == 0) return null;

        var strip = parent.Length == 0 ? 0 : parent.Length + 1;
        return [.. rows.Select(r => (r, target.Length == 0 ? r.Id[strip..] : $"{target}{PathSeparator}{r.Id[strip..]}"))];
    }

    /// <summary>Moves what is dragged into a folder: every key renamed in every file, as one undo step.</summary>
    public void Move(Variant data, string target) {
        var moves = MovesFor(data, target);
        if (moves == null || moves.Count == 0) return;

        var isFolder = data.AsGodotDictionary()["folder"].AsBool();
        var column = GetSelectedColumn() >= 0 ? GetSelectedColumn() : ColumnOf(ColumnKind.Source);
        var where = target.Length == 0 ? "the top level" : $"{target}{PathSeparator}";
        var error = Catalogs.RenameRows($"Move to {where}", moves);
        if (error != null) {
            EmitSignal(SignalName.EditRejected, error);
            return;
        }

        Rebuild();
        if (isFolder) {
            var name = data.AsGodotDictionary()[DragKey].AsString();
            name = name[(name.LastIndexOf(PathSeparator) + 1)..];
            SelectItem(GroupItem(target.Length == 0 ? name : $"{target}{PathSeparator}{name}"), column);
        } else {
            var (row, id) = moves[0];
            SelectCell(Catalogs.Rows.FirstOrDefault(r => r.Key == PoEntry.KeyOf(row.Context, id)), 0, column);
        }
        EmitSignal(SignalName.Edited);
    }

    /// <summary>The column titles: what a column shows, and for a language how far it is.</summary>
    public void UpdateTitles() {
        for (var c = 0; c < _kinds.Count; c++) {
            var arrow = c == _sortColumn ? (_sortDescending ? " ▼" : " ▲") : "";
            SetColumnTitle(c, _kinds[c] switch {
                ColumnKind.Context => "Context" + arrow,
                ColumnKind.Source => (TreeView ? "Key" : "Source") + arrow,
                ColumnKind.Language => LanguageTitle(_columnFiles[c]) + arrow,
                _ => "",
            });
        }
    }

    /// <summary><c>de · German  75%</c>, with a dot in front while the file has unsaved changes, and a tick once it is complete.</summary>
    string LanguageTitle(CatalogFile file) {
        var stats = Catalogs.StatsOf(file);
        var percent = stats.Total == 0 ? 100 : stats.Translated * 100 / stats.Total;
        var dirty = file.Dirty ? "● " : "";
        var name = string.IsNullOrEmpty(file.Locale) ? "" : TranslationServer.GetLocaleName(file.Locale);
        var label = string.IsNullOrEmpty(name) || name == file.Label ? file.Label : $"{file.Label} · {name}";
        return $"{dirty}{label}  {(percent == 100 ? "✓" : $"{percent}%")}";
    }

    IEnumerable<CatalogRow> VisibleRows() {
        var search = Search?.Trim() ?? "";
        foreach (var row in Catalogs.Rows) {
            if (search.Length > 0 && !Matches(row, search)) continue;
            if (!Passes(row)) continue;
            yield return row;
        }
    }

    bool Matches(CatalogRow row, string search) {
        bool Has(string text) => text != null && text.Contains(search, StringComparison.OrdinalIgnoreCase);
        if (Has(row.Id) || Has(row.IdPlural) || Has(row.Context)) return true;
        return Catalogs.Languages.Any(f => Catalogs.EntryOf(row, f) is { } entry && entry.Strings.Any(Has));
    }

    bool Passes(CatalogRow row) {
        switch (ShowOnly) {
            case Filter.Untranslated:
                return Catalogs.Languages.Any(f => Catalogs.EntryOf(row, f) is not { } entry || (!entry.Fuzzy && !entry.IsTranslated(f.Catalog.Plurals.Count)));
            case Filter.Fuzzy:
                return Catalogs.Languages.Any(f => Catalogs.EntryOf(row, f)?.Fuzzy == true);
            case Filter.Problems:
                return Catalogs.Languages.Any(f => Catalogs.ProblemsOf(row, f).Count > 0);
            case Filter.Plural:
                return row.IsPlural;
            default:
                return true;
        }
    }

    IEnumerable<CatalogRow> SortedRows(IEnumerable<CatalogRow> rows) {
        if (_sortColumn < 0 || _sortColumn >= _kinds.Count || _kinds[_sortColumn] == ColumnKind.None) return rows;
        var column = _sortColumn;
        Func<CatalogRow, string> key = _kinds[column] switch {
            ColumnKind.Context => r => r.Context ?? "",
            ColumnKind.Source => r => r.Id,
            _ => r => Catalogs.EntryOf(r, _columnFiles[column])?.StringAt(0) ?? "",
        };
        return _sortDescending ? rows.OrderByDescending(key, StringComparer.CurrentCultureIgnoreCase) : rows.OrderBy(key, StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>Writes the cells of one row of the table: a message, or one of its further plural forms.</summary>
    void Fill(TreeItem item, CatalogRow row, int form) {
        item.SetMetadata(0, row.Key);
        item.SetMetadata(1, form);
        var hint = GetThemeColor("font_placeholder_color", "LineEdit");

        for (var c = 0; c < _kinds.Count; c++) {
            item.SetTextOverrunBehavior(c, TextServer.OverrunBehavior.TrimEllipsis);
            item.ClearCustomBgColor(c);
            item.ClearCustomColor(c);
            item.SetIcon(c, null);
            item.SetTooltipText(c, "");

            switch (_kinds[c]) {
                case ColumnKind.Context:
                    item.SetText(c, form == 0 ? OneLine(row.Context ?? "") : "");
                    if (form == 0 && row.Context != null) item.SetCustomColor(c, Accent);
                    break;
                case ColumnKind.Source:
                    FillSource(item, row, form, c, hint);
                    break;
                default:
                    FillTranslation(item, row, form, c, hint);
                    break;
            }
        }
    }

    /// <summary>
    /// The source cell: the text — in the tree only the last part of its key — with a warning icon
    /// when something is wrong in a language, which the tooltip names. A row of a further plural
    /// form shows the plural source, or which form it is.
    /// </summary>
    void FillSource(TreeItem item, CatalogRow row, int form, int column, Color hint) {
        if (form > 0) {
            item.SetText(column, form == 1 ? OneLine(row.IdPlural) : $"form {form}");
            item.SetCustomColor(column, hint);
            item.SetTooltipText(column, form == 1 ? $"Plural source: {row.IdPlural}" : $"Plural form {form}; the languages' cells say which numbers it is for");
            return;
        }

        // In the tree its folders are already above it; the last part of the key is enough.
        var shown = TreeView && FoldersOf(row.Id).Length > 0 ? row.Id[(row.Id.LastIndexOf(PathSeparator) + 1)..] : row.Id;
        // Without the context column, the context still shows, after the text.
        if (!ShowContext && row.Context != null) shown += $"   ‹{row.Context}›";
        item.SetText(column, OneLine(shown));

        var problems = Catalogs.Languages.SelectMany(f => Catalogs.ProblemsOf(row, f).Select(p => $"⚠ {f.Label}: {p}")).ToList();
        var fuzzy = Catalogs.Languages.Where(f => Catalogs.EntryOf(row, f)?.Fuzzy == true).Select(f => f.Label).ToList();
        var tips = new List<string> { row.Context != null ? $"{row.Id}  (context: {row.Context})" : row.Id };
        if (row.IsPlural) tips.Add("Plural: one row per plural form below");
        if (fuzzy.Count > 0) tips.Add($"Fuzzy in {string.Join(", ", fuzzy)}");
        tips.AddRange(problems);
        item.SetTooltipText(column, string.Join("\n", tips));
        if (problems.Count > 0) SetIcon(item, column, "StatusWarning", GetThemeColor("warning_color", "Editor"));
    }

    /// <summary>The colour contexts are written in, so they stand apart from the text.</summary>
    Color Accent => HasThemeColor("accent_color", "Editor") ? GetThemeColor("accent_color", "Editor") : new Color(0.55f, 0.7f, 1f);

    /// <summary>Whether a message has a problem in any language — what the warning icon says.</summary>
    public bool HasProblems(CatalogRow row) => Catalogs.Languages.Any(f => Catalogs.ProblemsOf(row, f).Count > 0);

    void FillTranslation(TreeItem item, CatalogRow row, int form, int column, Color hint) {
        var file = _columnFiles[column];
        var plurals = file.Catalog.Plurals;
        var forms = row.IsPlural ? plurals.Count : 1;

        if (form >= forms) {
            // The language has fewer forms than the one with the most; nothing to write here.
            item.SetText(column, "");
            item.SetCustomBgColor(column, Unused);
            item.SetTooltipText(column, $"{file.Label} has {plurals.Count} plural form{(plurals.Count == 1 ? "" : "s")}");
            return;
        }

        var entry = Catalogs.EntryOf(row, file);
        var text = entry?.StringAt(form) ?? "";
        var label = row.IsPlural ? $"{plurals.Names[form]} · n = {plurals.SampleText(form)}" : null;

        if (text.Length == 0) {
            item.SetText(column, label ?? "");
            item.SetCustomColor(column, hint);
        } else {
            item.SetText(column, OneLine(text));
        }

        if (entry?.Fuzzy == true) item.SetCustomBgColor(column, FuzzyTint);
        else if (text.Length == 0) item.SetCustomBgColor(column, Missing);

        var tip = label != null ? $"{file.Label} · {label}" : file.Label;
        if (entry?.Fuzzy == true) tip += " · fuzzy";
        if (entry == null) tip += " · not in this file yet";
        if (text.Length > 0) tip += $"\n\n{text}";
        item.SetTooltipText(column, tip);
    }

    /// <summary>Line breaks as ↵, so every row is one line high.</summary>
    static string OneLine(string text) => (text ?? "").Replace("\r", "").Replace("\n", "↵");

    /// <summary>Writes one row again, after an edit that did not change which rows there are.</summary>
    public void RefreshRow(CatalogRow row) {
        if (row == null || !_items.TryGetValue(row.Key, out var item)) return;
        Fill(item, row, 0);
        var form = 1;
        for (var child = item.GetFirstChild(); child != null; child = child.GetNext()) Fill(child, row, form++);
        // The folders above count this text too.
        for (var parent = item.GetParent(); GroupPathOf(parent) is { } path; parent = parent.GetParent()) FillGroup(parent, path);
        UpdateTitles();
    }

    // ---- selection ---------------------------------------------------------------------------

    /// <summary>The message and plural form of the selected row, or nulls.</summary>
    public CatalogRow SelectedRow => RowOf(GetSelected());

    public int SelectedForm => GetSelected()?.GetMetadata(1).AsInt32() ?? 0;

    public CatalogFile SelectedFile => GetSelected() == null ? null : FileOf(GetSelectedColumn());

    public CatalogRow RowOf(TreeItem item) {
        if (item == null || Catalogs == null) return null;
        var key = item.GetMetadata(0).AsString();
        return Catalogs.Rows.FirstOrDefault(r => r.Key == key);
    }

    (string Key, int Form, int Column) SelectedCellKey() {
        var item = GetSelected();
        return item == null ? (null, 0, -1) : (item.GetMetadata(0).AsString(), item.GetMetadata(1).AsInt32(), GetSelectedColumn());
    }

    public TreeItem ItemOf(CatalogRow row, int form = 0) {
        if (row == null || !_items.TryGetValue(row.Key, out var item)) return null;
        if (form == 0) return item;
        var child = item.GetFirstChild();
        for (var i = 1; i < form && child != null; i++) child = child.GetNext();
        return child;
    }

    /// <summary>Selects a cell: the message's row, or the row of one of its plural forms.</summary>
    public void SelectCell(CatalogRow row, int form, int column) => SelectItem(ItemOf(row, form), column);

    /// <summary>Selects a cell of a row, unfolding the folders it is in.</summary>
    public void SelectItem(TreeItem item, int column) {
        if (item == null || column < 0 || column >= Columns) return;
        for (var parent = item.GetParent(); parent != null; parent = parent.GetParent()) parent.Collapsed = false;
        item.Select(column);
        ScrollToItem(item);
    }

    void RestoreSelection(string key, int form, int column) {
        if (key == null) return;
        if (key.StartsWith(GroupMark, StringComparison.Ordinal)) {
            SelectItem(GroupItem(key[GroupMark.Length..]), column < 0 ? ColumnOf(ColumnKind.Source) : column);
            return;
        }
        var row = Catalogs.Rows.FirstOrDefault(r => r.Key == key);
        if (row == null) return;
        SelectCell(row, form, column < 0 ? ColumnOf(ColumnKind.Source) : column);
    }

    void SetScroll(Vector2 scroll) {
        // Tree has no setter for its scroll; its scrollbars do.
        foreach (var bar in GetChildren(true).OfType<ScrollBar>()) bar.Value = bar is VScrollBar ? scroll.Y : scroll.X;
    }

    // ---- editing -----------------------------------------------------------------------------

    public bool IsEditing => _editItem != null;

    /// <summary>Whether a cell can be typed into: sources, contexts, and every form a language has.</summary>
    public bool CanEdit(TreeItem item, int column) {
        // A folder's name is its source cell: changing it renames every key inside.
        if (GroupPathOf(item) != null) return KindOf(column) == ColumnKind.Source;
        var row = RowOf(item);
        if (row == null) return false;
        var form = item.GetMetadata(1).AsInt32();
        return KindOf(column) switch {
            ColumnKind.Context => form == 0,
            ColumnKind.Source => form <= 1,
            ColumnKind.Language => form < (row.IsPlural ? FileOf(column).Catalog.Plurals.Count : 1),
            _ => false,
        };
    }

    /// <summary>The text a cell holds, line breaks and all — not what it shows.</summary>
    public string CellText(TreeItem item, int column) {
        // The whole path, so a folder can be moved by editing it as well as renamed.
        if (GroupPathOf(item) is { } folder) return KindOf(column) == ColumnKind.Source ? folder : "";
        var row = RowOf(item);
        if (row == null) return "";
        var form = item.GetMetadata(1).AsInt32();
        return KindOf(column) switch {
            ColumnKind.Context => row.Context ?? "",
            ColumnKind.Source => form == 0 ? row.Id : row.IdPlural ?? "",
            ColumnKind.Language => Catalogs.EntryOf(row, FileOf(column))?.StringAt(form) ?? "",
            _ => "",
        };
    }

    /// <summary>Opens the cell editor on the selected cell; with <paramref name="replaceWith"/>, starting from that text instead.</summary>
    public bool BeginEdit(string replaceWith = null) {
        var item = GetSelected();
        var column = GetSelectedColumn();
        if (item == null || !CanEdit(item, column)) return false;

        ScrollToItem(item);
        _editItem = item;
        _editColumn = column;
        _editor.Text = replaceWith ?? CellText(item, column);
        _editor.PlaceholderText = KindOf(column) == ColumnKind.Language ? item.GetTooltipText(column).Split('\n')[0] : "";
        _editor.Visible = true;
        FitEditor();
        _editor.GrabFocus();
        _editor.SetCaretLine(_editor.GetLineCount() - 1);
        _editor.SetCaretColumn(_editor.GetLine(_editor.GetLineCount() - 1).Length);
        if (replaceWith == null) _editor.SelectAll();
        return true;
    }

    void FitEditor() {
        if (_editItem == null) return;
        var rect = GetItemAreaRect(_editItem, _editColumn);
        var lineHeight = _editor.GetLineHeight();
        var lines = Math.Clamp(_editor.GetTotalVisibleLineCount(), 1, 8);
        var height = Math.Max(rect.Size.Y, lines * lineHeight + 10);
        _editor.Position = rect.Position;
        _editor.Size = new Vector2(Math.Max(rect.Size.X, 160), height);
    }

    public void CommitEdit() {
        if (_editItem == null || _committing) return;
        _committing = true;
        var item = _editItem;
        var column = _editColumn;
        var text = _editor.Text;
        _editItem = null;
        _editor.Visible = false;
        WriteCell(item, column, text);
        _committing = false;
    }

    public void CancelEdit() {
        if (_editItem == null) return;
        _editItem = null;
        _editor.Visible = false;
        GrabFocus();
    }

    /// <summary>Writes a cell through the set, so it is an undo step, and redraws what changed.</summary>
    public void WriteCell(TreeItem item, int column, string text) {
        if (GroupPathOf(item) is { } folder) {
            if (CanEdit(item, column)) RenameFolder(folder, text, column);
            return;
        }
        var row = RowOf(item);
        if (row == null || !CanEdit(item, column)) return;
        var form = item.GetMetadata(1).AsInt32();
        var kind = KindOf(column);

        if (kind == ColumnKind.Language) {
            Catalogs.SetTranslation(row, FileOf(column), form, text);
            RefreshRow(row);
        } else {
            var error = kind == ColumnKind.Context ? Catalogs.EditSource(row, text, row.Id, row.IdPlural)
                : form == 0 ? Catalogs.EditSource(row, row.Context, text, row.IdPlural)
                : Catalogs.EditSource(row, row.Context, row.Id, string.IsNullOrEmpty(text) ? null : text);
            if (error != null) {
                EmitSignal(SignalName.EditRejected, error);
                return;
            }
            // The key changed, so the row is found by its new one.
            var key = kind == ColumnKind.Context ? PoEntry.KeyOf(string.IsNullOrEmpty(text) ? null : text, row.Id) : form == 0 ? PoEntry.KeyOf(row.Context, text) : row.Key;
            Rebuild();
            if (Catalogs.Rows.FirstOrDefault(r => r.Key == key) is { } moved) SelectCell(moved, Math.Min(form, moved.IsPlural ? 1 : 0), column);
        }
        EmitSignal(SignalName.Edited);
    }

    /// <summary>
    /// Gives a folder a new path: every key inside is renamed in every file, as one undo step.
    /// Changing only the last part renames the folder, changing the rest moves it.
    /// </summary>
    public void RenameFolder(string folder, string path, int column = -1) {
        path = (path ?? "").Replace("\r", "").Replace("\n", "").Trim().Trim(PathSeparator);
        if (path.Length == 0) {
            EmitSignal(SignalName.EditRejected, "A folder needs a name.");
            return;
        }
        if (path == folder) return;

        var renames = Catalogs.Rows.Where(r => r.Id.StartsWith(folder + PathSeparator, StringComparison.Ordinal))
            .Select(r => (r, path + r.Id[folder.Length..])).ToList();
        var error = Catalogs.RenameRows($"Rename folder {folder}{PathSeparator}", renames);
        if (error != null) {
            EmitSignal(SignalName.EditRejected, error);
            return;
        }

        // Folded folders stay folded under their new name, the ones inside too.
        foreach (var old in _collapsed.Where(p => p == folder || p.StartsWith(folder + PathSeparator, StringComparison.Ordinal)).ToList()) {
            _collapsed.Remove(old);
            _collapsed.Add(path + old[folder.Length..]);
        }
        Rebuild();
        SelectItem(GroupItem(path), column >= 0 ? column : ColumnOf(ColumnKind.Source));
        EmitSignal(SignalName.Edited);
    }

    /// <summary>Asks before deleting a folder, saying how many texts go with it.</summary>
    public void AskDeleteFolder(string folder) {
        var count = RowsIn(folder).Count;
        _pendingDelete = folder;
        _deleteDialog.DialogText = $"Delete {folder}{PathSeparator} and the {count} text{(count == 1 ? "" : "s")} in it from every file?\nUndo brings them back.";
        _deleteDialog.PopupCentered();
    }

    void OnDeleteConfirmed() => DeleteFolder(_pendingDelete);

    /// <summary>Deletes every text in a folder, and the folders inside, from every file — one undo step.</summary>
    public void DeleteFolder(string folder) {
        var rows = RowsIn(folder);
        if (rows.Count == 0) return;
        var parent = FoldersOf(folder + PathSeparator + "x") is { Length: > 1 } folders ? folders[^2] : null;
        var column = GetSelectedColumn() >= 0 ? GetSelectedColumn() : ColumnOf(ColumnKind.Source);

        Catalogs.DeleteRows($"Delete folder {folder}{PathSeparator}", rows);
        _collapsed.RemoveWhere(p => p == folder || p.StartsWith(folder + PathSeparator, StringComparison.Ordinal));
        Rebuild();
        // The folder it was in, if that is still there.
        if (parent != null) SelectItem(GroupItem(parent), column);
        EmitSignal(SignalName.Edited);
    }

    List<CatalogRow> RowsIn(string folder) => [.. Catalogs.Rows.Where(r => r.Id.StartsWith(folder + PathSeparator, StringComparison.Ordinal))];

    void OnEditorInput(InputEvent @event) {
        if (@event is not InputEventKey { Pressed: true } key) return;
        switch (key.Keycode) {
            case Key.Enter or Key.KpEnter when !key.ShiftPressed && !key.AltPressed:
                _editor.AcceptEvent();
                CommitAndMove(down: 1, right: 0);
                break;
            case Key.Tab:
                _editor.AcceptEvent();
                CommitAndMove(down: 0, right: key.ShiftPressed ? -1 : 1);
                break;
            case Key.Escape:
                _editor.AcceptEvent();
                CancelEdit();
                break;
        }
    }

    void OnEditorFocusExited() {
        // Clicking elsewhere keeps what was typed, as a spreadsheet does.
        if (_editItem != null) CallDeferred(MethodName.CommitEdit);
    }

    void CommitAndMove(int down, int right) {
        var item = _editItem;
        var column = _editColumn;
        var key = item?.GetMetadata(0).AsString();
        var form = item?.GetMetadata(1).AsInt32() ?? 0;
        CommitEdit();
        GrabFocus();

        // The edit may have rebuilt the table; find the cell again by its message.
        var row = Catalogs?.Rows.FirstOrDefault(r => r.Key == key) ?? SelectedRow;
        var current = ItemOf(row, form) ?? GetSelected();
        if (current == null) return;

        if (right != 0) {
            var target = column + right;
            if (KindOf(target) == ColumnKind.None) target += right;
            if (target >= 0 && target < Columns) current.Select(target);
            else current.Select(column);
            return;
        }
        var next = current.GetNextVisible();
        if (next != null) {
            next.Select(column);
            ScrollToItem(next);
        } else {
            current.Select(column);
        }
    }

    // ---- keys and mouse ----------------------------------------------------------------------

    public override void _GuiInput(InputEvent @event) {
        if (IsEditing || Catalogs == null || @event is not InputEventKey { Pressed: true } key) return;
        var item = GetSelected();
        var column = GetSelectedColumn();
        if (item == null) return;

        var command = key.IsCommandOrControlPressed();
        if (GroupPathOf(item) != null) {
            // Enter folds and unfolds a folder; F2, or typing on its name, renames it.
            var source = ColumnOf(ColumnKind.Source);
            if (key.Keycode is Key.Enter or Key.KpEnter) {
                item.Collapsed = !item.Collapsed;
                AcceptEvent();
            } else if (key.Keycode == Key.F2) {
                item.Select(source);
                if (BeginEdit()) AcceptEvent();
            } else if (key.Keycode == Key.Delete && !command) {
                AskDeleteFolder(GroupPathOf(item));
                AcceptEvent();
            } else if (command && key.Keycode == Key.C) {
                DisplayServer.ClipboardSet(CellText(item, source));
                AcceptEvent();
            } else if (!command && !key.AltPressed && key.Unicode > 32 && column == source) {
                if (BeginEdit(char.ConvertFromUtf32((int) key.Unicode))) AcceptEvent();
            }
            return;
        }
        if (command && key.Keycode == Key.C) {
            DisplayServer.ClipboardSet(CellText(item, column));
            AcceptEvent();
        } else if (command && key.Keycode == Key.X && CanEdit(item, column) && KindOf(column) == ColumnKind.Language) {
            DisplayServer.ClipboardSet(CellText(item, column));
            WriteCell(item, column, "");
            AcceptEvent();
        } else if (command && key.Keycode == Key.V && CanEdit(item, column)) {
            WriteCell(item, column, DisplayServer.ClipboardGet().Replace("\r\n", "\n"));
            AcceptEvent();
        } else if (key.Keycode is Key.Enter or Key.KpEnter or Key.F2 && !command) {
            if (BeginEdit()) AcceptEvent();
        } else if (key.Keycode is Key.Delete or Key.Backspace && !command && KindOf(column) == ColumnKind.Language && CanEdit(item, column)) {
            WriteCell(item, column, "");
            AcceptEvent();
        } else if (!command && !key.AltPressed && key.Unicode >= 32 && key.Keycode != Key.Space || (key.Keycode == Key.Space && !command && CanEdit(item, column))) {
            // Typing into a cell replaces it, as in a spreadsheet.
            if (BeginEdit(char.ConvertFromUtf32((int) key.Unicode))) AcceptEvent();
        }
    }

    void OnItemActivated() {
        if (GetSelected() is { } item && GroupPathOf(item) != null) item.Collapsed = !item.Collapsed;
        else BeginEdit();
    }

    void OnColumnTitleClicked(int column, int mouseButton) {
        if (mouseButton == (int) MouseButton.Right) {
            if (FileOf(column) is { } file) EmitSignal(SignalName.LanguageMenuRequested, file.Path);
            return;
        }
        if (KindOf(column) == ColumnKind.None) return;
        // Click once to sort up, again to sort down, a third time for the order of the file.
        if (_sortColumn != column) {
            _sortColumn = column;
            _sortDescending = false;
        } else if (!_sortDescending) {
            _sortDescending = true;
        } else {
            _sortColumn = -1;
        }
        Rebuild();
    }

    public enum MenuId { Edit, CopySource, ToggleFuzzy, Clear, MakePlural, MakeSingular, Delete, LanguageSettings, RenameFolder, ToggleFold, DeleteFolder }

    void OnItemMouseSelected(Vector2 position, int mouseButton) {
        if (mouseButton != (int) MouseButton.Right) return;
        var item = GetSelected();
        var column = GetSelectedColumn();
        var row = RowOf(item);
        _menu.Clear();

        if (GroupPathOf(item) != null) {
            _menu.AddItem("Rename folder", (int) MenuId.RenameFolder);
            _menu.AddItem(item.Collapsed ? "Unfold" : "Fold", (int) MenuId.ToggleFold);
            _menu.AddSeparator();
            _menu.AddItem("Delete folder…", (int) MenuId.DeleteFolder);
        } else if (row != null) {
            FillTextMenu(item, column, row);
        } else {
            return;
        }

        _menu.Position = (Vector2I) (GetScreenPosition() + position);
        _menu.ResetSize();
        _menu.Popup();
    }

    void FillTextMenu(TreeItem item, int column, CatalogRow row) {
        _menu.AddItem("Edit", (int) MenuId.Edit);
        _menu.SetItemDisabled(_menu.ItemCount - 1, !CanEdit(item, column));
        if (FileOf(column) is { } file) {
            var entry = Catalogs.EntryOf(row, file);
            _menu.AddItem("Copy source to translation", (int) MenuId.CopySource);
            _menu.AddCheckItem("Fuzzy", (int) MenuId.ToggleFuzzy);
            _menu.SetItemChecked(_menu.ItemCount - 1, entry?.Fuzzy == true);
            _menu.SetItemDisabled(_menu.ItemCount - 1, entry == null);
            _menu.AddItem("Clear", (int) MenuId.Clear);
            _menu.AddSeparator();
            _menu.AddItem($"{file.Label}: language and plural forms…", (int) MenuId.LanguageSettings);
        }
        _menu.AddSeparator();
        if (row.IsPlural) _menu.AddItem("Make singular", (int) MenuId.MakeSingular);
        else _menu.AddItem("Make plural", (int) MenuId.MakePlural);
        _menu.AddItem("Delete message", (int) MenuId.Delete);
    }

    void OnMenuId(long id) => RunMenu((MenuId) id);

    /// <summary>What the cell menu does, on the selected cell. Public for the tests.</summary>
    public void RunMenu(MenuId id) {
        var item = GetSelected();
        var column = GetSelectedColumn();
        var row = RowOf(item);
        var file = FileOf(column);
        if (GroupPathOf(item) != null) {
            if (id == MenuId.RenameFolder) {
                item.Select(ColumnOf(ColumnKind.Source));
                BeginEdit();
            } else if (id == MenuId.ToggleFold) {
                item.Collapsed = !item.Collapsed;
            } else if (id == MenuId.DeleteFolder) {
                AskDeleteFolder(GroupPathOf(item));
            }
            return;
        }
        if (row == null) return;

        string error = null;
        switch (id) {
            case MenuId.Edit:
                BeginEdit();
                return;
            case MenuId.CopySource:
                Catalogs.CopySource(row, file);
                RefreshRow(row);
                break;
            case MenuId.ToggleFuzzy:
                Catalogs.SetFuzzy(row, file, Catalogs.EntryOf(row, file)?.Fuzzy != true);
                RefreshRow(row);
                break;
            case MenuId.Clear:
                WriteCell(item, column, "");
                return;
            case MenuId.LanguageSettings:
                if (file != null) EmitSignal(SignalName.LanguageMenuRequested, file.Path);
                return;
            case MenuId.MakePlural:
                error = Catalogs.MakePlural(row);
                Rebuild();
                break;
            case MenuId.MakeSingular:
                error = Catalogs.MakeSingular(row);
                Rebuild();
                break;
            case MenuId.Delete:
                Catalogs.DeleteRow(row);
                Rebuild();
                break;
        }
        if (error != null) EmitSignal(SignalName.EditRejected, error);
        EmitSignal(SignalName.Edited);
    }
}
#endif
