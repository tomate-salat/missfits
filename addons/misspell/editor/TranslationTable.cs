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

    public enum ColumnKind { Status, Context, Source, Language }

    /// <summary>Plain C#, so gone after an assembly reload; the panel hands it over again.</summary>
    public CatalogSet Catalogs;

    public string Search = "";
    public Filter ShowOnly = Filter.All;
    public bool ShowContext = true;

    readonly List<ColumnKind> _kinds = [];
    readonly List<CatalogFile> _columnFiles = [];
    readonly Dictionary<string, TreeItem> _items = [];
    int _sortColumn = -1;
    bool _sortDescending;

    TextEdit _editor;
    TreeItem _editItem;
    int _editColumn = -1;
    bool _committing;
    PopupMenu _menu;

    // Colours of the cells, kept faint so text stays readable in light and dark themes.
    static readonly Color Missing = new(1f, 0.35f, 0.3f, 0.13f);
    static readonly Color FuzzyTint = new(1f, 0.72f, 0.2f, 0.2f);
    static readonly Color Unused = new(0.5f, 0.5f, 0.5f, 0.08f);

    public override void _Ready() {
        HideRoot = true;
        SelectMode = SelectModeEnum.Single;
        ColumnTitlesVisible = true;
        AllowRmbSelect = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;

        Connect(Tree.SignalName.ItemActivated, new Callable(this, MethodName.OnItemActivated));
        Connect(Tree.SignalName.ColumnTitleClicked, new Callable(this, MethodName.OnColumnTitleClicked));
        Connect(Tree.SignalName.ItemMouseSelected, new Callable(this, MethodName.OnItemMouseSelected));

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
    }

    // ---- building ----------------------------------------------------------------------------

    /// <summary>Which column shows what, and the file of a language column.</summary>
    public ColumnKind KindOf(int column) => column >= 0 && column < _kinds.Count ? _kinds[column] : ColumnKind.Status;

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
        _kinds.Clear();
        _columnFiles.Clear();
        if (Catalogs == null) {
            Columns = 1;
            SetColumnTitle(0, "");
            return;
        }

        _kinds.Add(ColumnKind.Status);
        if (ShowContext) _kinds.Add(ColumnKind.Context);
        _kinds.Add(ColumnKind.Source);
        foreach (var _ in Catalogs.Languages) _kinds.Add(ColumnKind.Language);
        _columnFiles.AddRange(_kinds.Select(_ => (CatalogFile) null));
        var first = _kinds.IndexOf(ColumnKind.Language);
        var languages = Catalogs.Languages.ToList();
        for (var i = 0; i < languages.Count; i++) _columnFiles[first + i] = languages[i];

        Columns = _kinds.Count;
        for (var c = 0; c < Columns; c++) {
            SetColumnClipContent(c, true);
            switch (_kinds[c]) {
                case ColumnKind.Status:
                    SetColumnExpand(c, false);
                    SetColumnCustomMinimumWidth(c, 40);
                    break;
                case ColumnKind.Context:
                    SetColumnExpand(c, true);
                    SetColumnExpandRatio(c, 1);
                    SetColumnCustomMinimumWidth(c, 70);
                    break;
                default:
                    SetColumnExpand(c, true);
                    SetColumnExpandRatio(c, 3);
                    SetColumnCustomMinimumWidth(c, 140);
                    break;
            }
        }
        UpdateTitles();

        var root = CreateItem();
        var maxForms = Catalogs.MaxForms;
        foreach (var row in SortedRows(VisibleRows())) {
            var item = CreateItem(root);
            _items[row.Key] = item;
            Fill(item, row, 0);
            if (!row.IsPlural) continue;
            for (var form = 1; form < maxForms; form++) Fill(CreateItem(item), row, form);
        }

        RestoreSelection(selectedKey, selectedForm, selectedKind == ColumnKind.Language ? ColumnOf(selectedFile) : ColumnOf(selectedKind));
        CallDeferred(MethodName.SetScroll, scroll);
    }

    /// <summary>The column titles: what a column shows, and for a language how far it is.</summary>
    public void UpdateTitles() {
        for (var c = 0; c < _kinds.Count; c++) {
            var arrow = c == _sortColumn ? (_sortDescending ? " ▼" : " ▲") : "";
            SetColumnTitle(c, _kinds[c] switch {
                ColumnKind.Status => "",
                ColumnKind.Context => "Context" + arrow,
                ColumnKind.Source => (Catalogs?.Template != null ? "Source" : "Source (no template)") + arrow,
                _ => LanguageTitle(_columnFiles[c]) + arrow,
            });
        }
    }

    string LanguageTitle(CatalogFile file) {
        var stats = Catalogs.StatsOf(file);
        var percent = stats.Total == 0 ? 100 : stats.Translated * 100 / stats.Total;
        var dirty = file.Dirty ? "*" : "";
        return $"{file.Label}{dirty}  {percent}%";
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
        if (_sortColumn < 0 || _sortColumn >= _kinds.Count || _kinds[_sortColumn] == ColumnKind.Status) return rows;
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
            item.SetTooltipText(c, "");

            switch (_kinds[c]) {
                case ColumnKind.Status:
                    FillStatus(item, row, form, c);
                    break;
                case ColumnKind.Context:
                    item.SetText(c, form == 0 ? OneLine(row.Context ?? "") : "");
                    break;
                case ColumnKind.Source:
                    if (form == 0) {
                        item.SetText(c, OneLine(row.Id));
                        item.SetTooltipText(c, row.Id);
                    } else if (form == 1) {
                        item.SetText(c, OneLine(row.IdPlural));
                        item.SetTooltipText(c, $"Plural source: {row.IdPlural}");
                    } else {
                        item.SetText(c, "");
                    }
                    if (form > 0) item.SetCustomColor(c, hint);
                    break;
                default:
                    FillTranslation(item, row, form, c, hint);
                    break;
            }
        }
    }

    void FillStatus(TreeItem item, CatalogRow row, int form, int column) {
        if (form > 0) {
            item.SetText(column, $"  {form}");
            item.SetCustomColor(column, GetThemeColor("font_placeholder_color", "LineEdit"));
            item.SetTooltipText(column, $"Plural form {form}");
            return;
        }

        var problems = Catalogs.Languages.SelectMany(f => Catalogs.ProblemsOf(row, f).Select(p => $"{f.Label}: {p}")).ToList();
        var fuzzy = Catalogs.Languages.Any(f => Catalogs.EntryOf(row, f)?.Fuzzy == true);
        item.SetText(column, problems.Count > 0 ? "⚠" : fuzzy ? "~" : row.IsPlural ? "  0" : "");
        var tips = new List<string>();
        if (row.IsPlural) tips.Add("Plural message: one row per plural form");
        if (fuzzy) tips.Add("Fuzzy in: " + string.Join(", ", Catalogs.Languages.Where(f => Catalogs.EntryOf(row, f)?.Fuzzy == true).Select(f => f.Label)));
        tips.AddRange(problems);
        item.SetTooltipText(column, string.Join("\n", tips));
        if (problems.Count > 0) item.SetCustomColor(column, GetThemeColor("warning_color", "Editor"));
    }

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
    public void SelectCell(CatalogRow row, int form, int column) {
        var item = ItemOf(row, form);
        if (item == null || column < 0 || column >= Columns) return;
        item.Select(column);
        ScrollToItem(item);
    }

    void RestoreSelection(string key, int form, int column) {
        if (key == null) return;
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
            current.Select(Math.Clamp(column + right, 1, Columns - 1));
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

    void OnItemActivated() => BeginEdit();

    void OnColumnTitleClicked(int column, int mouseButton) {
        if (mouseButton == (int) MouseButton.Right) {
            if (FileOf(column) is { } file) EmitSignal(SignalName.LanguageMenuRequested, file.Path);
            return;
        }
        if (KindOf(column) == ColumnKind.Status) return;
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

    public enum MenuId { Edit, CopySource, ToggleFuzzy, Clear, MakePlural, MakeSingular, Delete, LanguageSettings }

    void OnItemMouseSelected(Vector2 position, int mouseButton) {
        if (mouseButton != (int) MouseButton.Right) return;
        var item = GetSelected();
        var column = GetSelectedColumn();
        var row = RowOf(item);
        if (row == null) return;

        _menu.Clear();
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

        _menu.Position = (Vector2I) (GetScreenPosition() + position);
        _menu.ResetSize();
        _menu.Popup();
    }

    void OnMenuId(long id) => RunMenu((MenuId) id);

    /// <summary>What the cell menu does, on the selected cell. Public for the tests.</summary>
    public void RunMenu(MenuId id) {
        var item = GetSelected();
        var column = GetSelectedColumn();
        var row = RowOf(item);
        var file = FileOf(column);
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
