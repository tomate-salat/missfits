#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misspell.Editor;

/// <summary>
/// The Misspell screen: toolbar, the table with the details beside it, and a status line.
/// <para>
/// All wiring uses native method callables and Godot signals, never <c>+=</c> or C# delegates:
/// pressing play reloads the assembly, and anything delegate-based comes back dead. The open
/// <see cref="CatalogSet"/> is plain C# and is lost by a reload too, so the panel keeps what it
/// needs to get it back in Godot types — the path, and the text of every unsaved file.
/// </para>
/// </summary>
[Tool]
public partial class MisspellPanel : VBoxContainer {
    /// <summary>Unsaved changes came or went, so the tab can say so.</summary>
    [Signal]
    public delegate void DirtyStateChangedEventHandler(bool dirty);

    /// <summary>A set of catalogs was opened, by the path it was opened with.</summary>
    [Signal]
    public delegate void CatalogOpenedEventHandler(string path);

    CatalogSet _set;

    /// <summary>The open set, reopened from what survived if an assembly reload took it.</summary>
    public CatalogSet Catalogs {
        get {
            if (_set == null && !string.IsNullOrEmpty(_openPath)) Restore();
            return _set;
        }
    }

    // What survives an assembly reload.
    string _openPath = "";
    Godot.Collections.Dictionary<string, string> _unsaved = [];
    Godot.Collections.Array<string> _register = [];
    bool _wasDirty;

    public TranslationTable Table { get; private set; }
    public EntryDetails Details { get; private set; }

    OptionButton _catalogs;
    Button _save;
    Button _reload;
    Button _merge;
    Button _addLanguage;
    Button _addMessage;
    Button _addFolder;
    LineEdit _search;
    OptionButton _filter;
    Button _treeToggle;
    Button _contextToggle;
    Button _detailsToggle;
    Label _status;
    LanguageDialog _languageDialog;
    ConfirmationDialog _messageDialog;
    ConfirmationDialog _codeDialog;
    Label _codeSummary;
    Tree _codeTree;
    Label _codeCheckTitle;
    RichTextLabel _codeCheck;
    /// <summary>The uses a rename would change, while the dialog asks; plain C#, gone after a reload like the question.</summary>
    List<CodeRename.Change> _codeChanges;
    object _codeStep;
    LineEdit _newContext;
    LineEdit _newSource;
    LineEdit _newPlural;
    FileDialog _openDialog;
    Control _empty;
    Control _split;
    readonly List<string> _catalogPaths = [];

    public override void _Ready() {
        Name = "Misspell";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;

        AddThemeConstantOverride("separation", 6);
        AddChild(BuildToolbar());

        _empty = BuildEmptyState();
        AddChild(_empty);

        var split = new HSplitContainer { Name = "Split", SizeFlagsVertical = SizeFlags.ExpandFill };
        _split = split;
        AddChild(split);

        Table = new TranslationTable { Name = "Table" };
        split.AddChild(Table);
        Table.Connect(TranslationTable.SignalName.Edited, new Callable(this, MethodName.OnEdited));
        Table.Connect(TranslationTable.SignalName.EditRejected, new Callable(this, MethodName.OnRejected));
        Table.Connect(TranslationTable.SignalName.LanguageMenuRequested, new Callable(this, MethodName.OnLanguageMenu));
        Table.Connect(TranslationTable.SignalName.NewTextRequested, new Callable(this, MethodName.AddMessagePressed));
        Table.Connect(Tree.SignalName.CellSelected, new Callable(this, MethodName.OnCellSelected));

        Details = new EntryDetails { Name = "Details" };
        split.AddChild(Details);
        Details.Connect(EntryDetails.SignalName.Edited, new Callable(this, MethodName.OnDetailsEdited));

        _status = new Label { Name = "Status" };
        _status.AddThemeFontSizeOverride("font_size", 11);
        AddChild(_status);

        _languageDialog = new LanguageDialog();
        _languageDialog.Connect(LanguageDialog.SignalName.Chosen, new Callable(this, MethodName.OnLanguageChosen));
        AddChild(_languageDialog);
        AddChild(BuildMessageDialog());
        AddChild(BuildCodeDialog());

        _openDialog = new FileDialog {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Resources,
            Filters = ["*.po, *.pot ; Gettext catalogs"],
            UseNativeDialog = false,
        };
        _openDialog.Connect(FileDialog.SignalName.FileSelected, new Callable(this, MethodName.Open));
        AddChild(_openDialog);

        RefreshCatalogList();
        UpdateUi();
    }

    /// <summary>
    /// File on the left (catalog, open, reload, save), editing next (add text, add language, update
    /// from the template), finding in the middle (search, filter), and on the right how to look at
    /// it (tree, context, details).
    /// </summary>
    Control BuildToolbar() {
        var bar = new HBoxContainer { Name = "Toolbar" };
        bar.AddThemeConstantOverride("separation", 2);

        _catalogs = new OptionButton { TooltipText = "The catalogs in this project: a template and its languages", CustomMinimumSize = new Vector2(240, 0), FitToLongestItem = false };
        _catalogs.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnCatalogPicked));
        bar.AddChild(_catalogs);
        bar.AddChild(Tool("", "Open a .po or .pot file, and the files that go with it", MethodName.ShowOpenDialog, "Load"));
        _reload = Tool("", "Read the files from disk again, dropping unsaved changes", MethodName.ReloadPressed, "Reload");
        bar.AddChild(_reload);
        _save = Tool("Save", "Write every changed file (Ctrl+S)", MethodName.SavePressed, "Save");
        bar.AddChild(_save);
        bar.AddChild(Gap());

        _addMessage = Tool("Text", "Add a text by hand to every file", MethodName.AddMessagePressed, "Add");
        bar.AddChild(_addMessage);
        _addFolder = Tool("Folder", "Add a folder in the selected one. It stays empty, and is not saved, until a text is put in it", MethodName.AddFolderPressed, "FolderCreate");
        bar.AddChild(_addFolder);
        _addLanguage = Tool("Language", "Add a language: a new .po file with every text of the template", MethodName.AddLanguagePressed, "Translation");
        bar.AddChild(_addLanguage);
        _merge = Tool("Update", "Update from the template: add its new texts to every language, and mark texts gone from it obsolete", MethodName.MergePressed, "ImportCheck");
        bar.AddChild(_merge);
        bar.AddChild(Gap());

        _search = new LineEdit { PlaceholderText = "Search keys and translations  (Ctrl+F)", ClearButtonEnabled = true, CustomMinimumSize = new Vector2(180, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        if (Icon("Search") is { } search) _search.RightIcon = search;
        _search.Connect(LineEdit.SignalName.TextChanged, new Callable(this, MethodName.OnSearchChanged));
        bar.AddChild(_search);

        _filter = new OptionButton { TooltipText = "Which texts to show" };
        _filter.AddItem("All texts", (int) TranslationTable.Filter.All);
        _filter.AddItem("Untranslated", (int) TranslationTable.Filter.Untranslated);
        _filter.AddItem("Fuzzy", (int) TranslationTable.Filter.Fuzzy);
        _filter.AddItem("With problems", (int) TranslationTable.Filter.Problems);
        _filter.AddItem("Plural", (int) TranslationTable.Filter.Plural);
        _filter.AddItem("Probably unused", (int) TranslationTable.Filter.Unused);
        _filter.SetItemTooltip(_filter.ItemCount - 1, TranslationTable.UnusedTip);
        _filter.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnFilterPicked));
        bar.AddChild(_filter);
        bar.AddChild(Gap());

        _treeToggle = Toggle("Tree", "Show keys like welcome/option/enter as folders", MethodName.OnTreeToggled, "Folder");
        bar.AddChild(_treeToggle);
        _contextToggle = Toggle("Context", "Show the context column; without it, a context shows after its text", MethodName.OnContextToggled, "");
        bar.AddChild(_contextToggle);
        _detailsToggle = Toggle("Details", "Show the details of the selected text", MethodName.OnDetailsToggled, "");
        _detailsToggle.SetPressedNoSignal(true);
        bar.AddChild(_detailsToggle);
        return bar;
    }

    Button Tool(string text, string tooltip, StringName method, string icon) {
        var button = new Button { Text = text, TooltipText = tooltip, Flat = true, Icon = Icon(icon) };
        button.Connect(BaseButton.SignalName.Pressed, new Callable(this, method));
        return button;
    }

    Button Toggle(string text, string tooltip, StringName method, string icon) {
        var button = new Button { Text = text, TooltipText = tooltip, Flat = true, ToggleMode = true, Icon = Icon(icon) };
        button.Connect(BaseButton.SignalName.Toggled, new Callable(this, method));
        return button;
    }

    static Control Gap() => new Control { CustomMinimumSize = new Vector2(12, 0) };

    /// <summary>An icon of the editor's theme; none outside the editor, where the buttons keep their words.</summary>
    Texture2D Icon(string name) => name.Length > 0 && HasThemeIcon(name, "EditorIcons") ? GetThemeIcon(name, "EditorIcons") : null;

    /// <summary>What shows while no catalog is open: what this is, and how to start.</summary>
    Control BuildEmptyState() {
        var center = new CenterContainer { Name = "Empty", SizeFlagsVertical = SizeFlags.ExpandFill };
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        box.AddThemeConstantOverride("separation", 10);
        center.AddChild(box);

        if (Icon("Translation") is { } icon) {
            box.AddChild(new TextureRect { Texture = icon, StretchMode = TextureRect.StretchModeEnum.KeepCentered, CustomMinimumSize = new Vector2(0, 32) });
        }
        var title = new Label { Text = "No translations open", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 18);
        box.AddChild(title);
        box.AddChild(new Label {
            Text = "Pick a catalog from the list above, open a .po or .pot file, or double-click a .po file in the FileSystem dock.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(1, 1, 1, 0.6f),
        });
        var open = new Button { Text = "Open…", Icon = Icon("Load"), SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        open.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.ShowOpenDialog));
        box.AddChild(open);
        return center;
    }

    Window BuildMessageDialog() {
        _messageDialog = new ConfirmationDialog { Name = "MessageDialog", Title = "Add text", OkButtonText = "Add", MinSize = new Vector2I(420, 0) };
        var grid = new GridContainer { Columns = 2 };
        _messageDialog.AddChild(grid);
        _newSource = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "The text as the code says it" };
        _newPlural = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "Only for a plural: {n} coins" };
        _newContext = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "Optional, tells same words apart" };
        grid.AddChild(new Label { Text = "Source" });
        grid.AddChild(_newSource);
        grid.AddChild(new Label { Text = "Plural" });
        grid.AddChild(_newPlural);
        grid.AddChild(new Label { Text = "Context" });
        grid.AddChild(_newContext);
        _messageDialog.RegisterTextEnter(_newSource);
        _messageDialog.Connect(AcceptDialog.SignalName.Confirmed, new Callable(this, MethodName.OnMessageConfirmed));
        return _messageDialog;
    }

    /// <summary>
    /// Asked after a rename when the old texts are used in the code: one row per use, ticked, under
    /// its file — and below, the strings with holes that may make an old text, to check by hand.
    /// </summary>
    Window BuildCodeDialog() {
        _codeDialog = new ConfirmationDialog {
            Name = "CodeDialog", Title = "Change the code too?", OkButtonText = "Change code", CancelButtonText = "Leave the code",
        };
        // The third way out: take the rename back, texts and all.
        _codeDialog.AddButton("Cancel the rename", right: false, action: CancelRenameAction);
        _codeDialog.Connect(AcceptDialog.SignalName.CustomAction, new Callable(this, MethodName.OnCodeCustomAction));
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _codeDialog.AddChild(box);
        // Wrapping text needs a width of its own: without one, the dialog grows as tall as the
        // text would be with one word per line.
        _codeSummary = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(600, 0) };
        box.AddChild(_codeSummary);
        _codeTree = new Tree { Columns = 2, HideRoot = true, ColumnTitlesVisible = true, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 220) };
        _codeTree.SetColumnTitle(0, "Use");
        _codeTree.SetColumnTitle(1, "Becomes");
        _codeTree.SetColumnExpandRatio(0, 3);
        _codeTree.Connect(Tree.SignalName.ItemEdited, new Callable(this, MethodName.OnCodeItemEdited));
        box.AddChild(_codeTree);
        _codeCheckTitle = new Label { Text = "Check by hand — strings with holes that may make an old text:" };
        box.AddChild(_codeCheckTitle);
        // Scrolls in a fixed height, however many there are.
        _codeCheck = new RichTextLabel { BbcodeEnabled = true, MetaUnderlined = false, TooltipText = "Open it", CustomMinimumSize = new Vector2(600, 72) };
        _codeCheck.Connect(RichTextLabel.SignalName.MetaClicked, new Callable(this, MethodName.OnCodeCheckClicked));
        box.AddChild(_codeCheck);
        _codeDialog.Connect(AcceptDialog.SignalName.Confirmed, new Callable(this, MethodName.OnCodeConfirmed));
        _codeDialog.Connect(AcceptDialog.SignalName.Canceled, new Callable(this, MethodName.OnCodeCanceled));
        return _codeDialog;
    }

    /// <summary>After a rename: when the old texts are used in the code, asks whether to change those uses too. Every time.</summary>
    void OfferCodeRename() {
        if (_set?.TakeRenames() is not { } taken) return;
        var changes = CodeRename.Plan(Usages, taken.Renames);
        var check = CodeRename.ToCheck(Usages, taken.Renames);
        if (changes.Count == 0) {
            if (check.Count > 0) SetStatus($"{check.Count} string{(check.Count == 1 ? "" : "s")} with holes may make the old text — see “Maybe used in”", warning: true);
            return;
        }
        _codeChanges = changes;
        _codeStep = taken.Step;

        var files = changes.Select(c => c.Usage.Path).Distinct().Count();
        _codeSummary.Text = $"{changes.Count} use{(changes.Count == 1 ? "" : "s")} of the old text{(taken.Renames.Count == 1 ? "" : "s")} in {files} file{(files == 1 ? "" : "s")}. "
                            + "The ticked ones are written anew; Undo puts the code back along with the texts.";
        _codeTree.Clear();
        var root = _codeTree.CreateItem();
        for (var i = 0; i < changes.Count; i++) {
            var change = changes[i];
            var parent = root.GetChildren().LastOrDefault(f => f.GetMetadata(0).AsString() == change.Usage.Path);
            if (parent == null) {
                parent = _codeTree.CreateItem(root);
                parent.SetCellMode(0, TreeItem.TreeCellMode.Check);
                parent.SetChecked(0, true);
                parent.SetEditable(0, true);
                parent.SetText(0, change.Usage.Path.TrimPrefix("res://"));
                parent.SetMetadata(0, change.Usage.Path);
                parent.SetMetadata(1, -1);
            }
            var item = _codeTree.CreateItem(parent);
            item.SetCellMode(0, TreeItem.TreeCellMode.Check);
            item.SetChecked(0, true);
            item.SetEditable(0, true);
            item.SetText(0, $"{change.Usage.Line}   {change.Usage.Code}");
            item.SetTooltipText(0, $"{change.Usage.Reference}\n{change.Usage.Code}");
            item.SetText(1, change.Rename.NewId);
            item.SetTooltipText(1, $"{change.Rename.OldId}\n→ {change.Rename.NewId}");
            item.SetMetadata(0, change.Usage.Path);
            item.SetMetadata(1, i);
        }

        _codeCheckTitle.Visible = _codeCheck.Visible = check.Count > 0;
        _codeCheck.Text = string.Join("\n", check.Select(u => $"[url={u.Reference}]{u.Path.TrimPrefix("res://")}:{u.Line}[/url]   {u.Code.Replace("[", "[lb]")}"));
        // A set size, scaled like the editor and never beyond the window.
        var scale = Engine.IsEditorHint() ? EditorInterface.Singleton.GetEditorScale() : 1f;
        _codeDialog.PopupCenteredClamped(new Vector2I((int) (780 * scale), (int) (480 * scale)), 0.85f);
    }

    static readonly StringName CancelRenameAction = "cancel_rename";

    void OnCodeCustomAction(StringName action) {
        if (action == CancelRenameAction) CancelRename();
    }

    /// <summary>Takes the rename back, texts and all, as Undo would; the code was never touched.</summary>
    internal void CancelRename() {
        _codeDialog.Hide();
        if (_codeChanges == null) return;
        var step = _codeStep;
        _codeChanges = null;
        _codeStep = null;
        if (_set == null || !_set.IsLastStep(step)) {
            SetStatus("The rename could not be taken back: something else was changed since", warning: true);
            return;
        }
        var name = _set.UndoName;
        _set.Undo();
        AfterReplay($"Cancelled: {name}");
    }

    /// <summary>Ticking a file ticks its uses; ticking a use keeps its file's tick in step.</summary>
    void OnCodeItemEdited() {
        var item = _codeTree.GetEdited();
        if (item == null || _codeChanges == null) return;
        var on = item.IsChecked(0);
        if (item.GetMetadata(1).AsInt32() < 0) {
            foreach (var child in item.GetChildren()) {
                child.SetChecked(0, on);
                _codeChanges[child.GetMetadata(1).AsInt32()].Wanted = on;
            }
            return;
        }
        _codeChanges[item.GetMetadata(1).AsInt32()].Wanted = on;
        var parent = item.GetParent();
        var all = parent.GetChildren().All(c => c.IsChecked(0));
        var none = parent.GetChildren().All(c => !c.IsChecked(0));
        parent.SetChecked(0, all);
        parent.SetIndeterminate(0, !all && !none);
    }

    void OnCodeCheckClicked(Variant meta) {
        _codeDialog.Hide();
        Details.OpenReference(meta.AsString());
    }

    /// <summary>The uses the dialog would change, as ticked; a test unticks and confirms through these.</summary>
    internal List<CodeRename.Change> PendingCodeChanges => _codeChanges;

    internal void ConfirmCodeRename() {
        _codeDialog.Hide();
        OnCodeConfirmed();
    }

    void OnCodeConfirmed() {
        if (_codeChanges == null) return;
        var problems = new List<string>();
        var done = CodeRename.Apply(_codeChanges, problems);
        _set?.AttachCode(_codeStep, done);
        var changed = _codeChanges.Count(c => c.Wanted);
        _codeChanges = null;
        _codeStep = null;
        Usages.Refresh();
        Table.Rebuild();
        ShowDetails();
        if (problems.Count == 0) {
            SetStatus($"Code changed: {changed} use{(changed == 1 ? "" : "s")} in {done.Count} file{(done.Count == 1 ? "" : "s")}");
            return;
        }
        foreach (var problem in problems) GD.PushWarning($"Misspell: {problem}");
        var more = problems.Count > 1 ? $" (and {problems.Count - 1} more, see Output)" : "";
        SetStatus(done.Count == 0 ? $"Code not changed: {problems[0]}{more}"
                                  : $"Code changed in {done.Count} file{(done.Count == 1 ? "" : "s")}, but not everywhere: {problems[0]}{more}", warning: true);
    }

    void OnCodeCanceled() {
        if (_codeChanges == null) return;
        SetStatus($"Code left as it was: {_codeChanges.Count} use{(_codeChanges.Count == 1 ? "" : "s")} still say{(_codeChanges.Count == 1 ? "s" : "")} the old text", warning: true);
        _codeChanges = null;
        _codeStep = null;
    }

    // ---- opening -----------------------------------------------------------------------------

    /// <summary>Opens the files that belong with the one at <paramref name="path"/>. Unsaved changes of the open set are saved first.</summary>
    public void Open(string path) {
        if (string.IsNullOrEmpty(path)) return;
        if (_set != null && _set.Files.Any(f => f.Path == path)) return;
        if (_set != null && _set.Files.Any(f => f.Dirty)) SaveAll();

        _openPath = path;
        _unsaved.Clear();
        _register.Clear();
        _set = CatalogSet.Open(path);
        HandOver();
        // The context column only when there are contexts to show.
        var contexts = _set.Rows.Any(r => r.Context != null);
        _contextToggle.SetPressedNoSignal(contexts);
        Table.ShowContext = contexts;
        ShowAsTree(LooksLikePaths(_set));
        Details.ShowRow(null, null);
        RefreshCatalogList();
        UpdateUi();
        SetStatus(Summary());
        EmitSignal(SignalName.CatalogOpened, path);
    }

    /// <summary>Opens a set that only exists in memory. For tests.</summary>
    public void OpenSet(CatalogSet set) {
        _set = set;
        _openPath = "";
        HandOver();
        Table.Rebuild();
        UpdateUi();
    }

    /// <summary>Test seam: drops the set as an assembly reload would, keeping what survives one.</summary>
    internal void ForgetSetAsAReloadWould() => _set = null;

    void HandOver() {
        Table.Catalogs = _set;
        Details.Catalogs = _set;
        Table.Usages = Details.Usages;
    }

    /// <summary>Where texts are used, shared by the table and the details; a test points it at a folder of its own.</summary>
    public KeyUsages Usages {
        get => Details.Usages;
        set {
            Details.Usages = Table.Usages = value;
            Table.Rebuild();
        }
    }

    /// <summary>After an assembly reload: the set again from disk, with the unsaved text laid over it.</summary>
    void Restore() {
        _set = CatalogSet.Open(_openPath);
        foreach (var (path, text) in _unsaved) {
            var file = _set.Files.FirstOrDefault(f => f.Path == path);
            if (file == null) {
                file = new CatalogFile { Path = path, Catalog = PoCatalog.Parse(text) };
                _set.Files.Add(file);
            } else {
                file.Catalog = PoCatalog.Parse(text);
            }
            file.Index();
            file.UpdateDirty();
        }
        _set.RebuildRows();
        HandOver();
        CallDeferred(MethodName.AfterRestore);
    }

    void AfterRestore() {
        Table.Rebuild();
        UpdateUi();
    }

    public override void _Process(double delta) {
        // Touching Catalogs brings it back after an assembly reload.
        if (_set == null && !string.IsNullOrEmpty(_openPath)) _ = Catalogs;
    }

    public override void _Notification(int what) {
        if (what == NotificationApplicationFocusIn) CheckDisk();
    }

    /// <summary>Files changed outside — a new template, a git pull — are read again; with unsaved changes the status line says so instead.</summary>
    void CheckDisk() {
        if (_set == null) return;
        var changed = _set.ChangedOnDisk();
        if (changed.Count == 0) return;
        if (_set.Files.Any(f => f.Dirty)) {
            SetStatus($"Changed on disk: {string.Join(", ", changed.Select(f => f.Path.GetFile()))}. Save to keep yours, or Reload to take theirs.", warning: true);
            return;
        }
        ReloadPressed();
        SetStatus($"Read again after changes on disk: {string.Join(", ", changed.Select(f => f.Path.GetFile()))}");
    }

    void ShowOpenDialog() => _openDialog.PopupCentered(new Vector2I(800, 500));

    /// <summary>Fills the toolbar's list with every set of catalogs in the project.</summary>
    public void RefreshCatalogList() {
        _catalogs.Clear();
        _catalogPaths.Clear();
        var found = CatalogSet.FindAll();
        var selected = -1;
        foreach (var (title, path) in found) {
            if (_set != null && _set.Files.Any(f => f.Path == path)) selected = _catalogPaths.Count;
            _catalogs.AddItem(title);
            _catalogPaths.Add(path);
        }
        if (_set != null && selected < 0) {
            selected = _catalogPaths.Count;
            _catalogs.AddItem(_set.Title);
            _catalogPaths.Add(_set.Files.FirstOrDefault()?.Path ?? "");
        }
        if (found.Count == 0 && _set == null) {
            _catalogs.AddItem("No .po/.pot files yet");
            _catalogs.SetItemDisabled(0, true);
            _catalogPaths.Add("");
        }
        _catalogs.Select(selected);
    }

    void OnCatalogPicked(long index) {
        if (index >= 0 && index < _catalogPaths.Count) Open(_catalogPaths[(int) index]);
    }

    // ---- saving ------------------------------------------------------------------------------

    public bool IsDirty => _set != null && _set.Files.Any(f => f.Dirty);

    public string[] UnsavedPaths() => _set == null ? [] : [.. _set.Files.Where(f => f.Dirty).Select(f => f.Path)];

    void SavePressed() => SaveAll();

    /// <summary>Writes every changed file, puts the new texts into the translations already loaded, and adds new languages to the project.</summary>
    public List<string> SaveAll() {
        if (Catalogs == null) return [];
        var saved = _set.SaveAll();
        if (saved.Count == 0) return saved;

        _unsaved.Clear();
        var toRegister = saved.Where(p => _register.Contains(p)).ToList();
        var registered = RegisterTranslations(toRegister);
        foreach (var path in saved) _register.Remove(path);
        ReloadTranslations(saved, toRegister);
        // So the FileSystem dock lists new files.
        if (Engine.IsEditorHint()) EditorInterface.Singleton.GetResourceFilesystem().Scan();

        SetStatus($"Saved {string.Join(", ", saved.Select(p => p.GetFile()))}{(registered > 0 ? " and added to the project's translations" : "")}.");
        RefreshCatalogList();
        Table.UpdateTitles();
        UpdateUi();
        return saved;
    }

    /// <summary>Adds .po files to <i>Project Settings → Localization → Translations</i>; returns how many were new there.</summary>
    static int RegisterTranslations(List<string> paths) {
        if (paths.Count == 0) return 0;
        const string setting = "internationalization/locale/translations";
        var current = ProjectSettings.HasSetting(setting) ? ProjectSettings.GetSetting(setting).AsStringArray().ToList() : [];
        var added = paths.Where(p => p.EndsWith(".po") && !current.Contains(p)).ToList();
        if (added.Count == 0) return 0;
        current.AddRange(added);
        ProjectSettings.SetSetting(setting, current.ToArray());
        ProjectSettings.Save();
        return added.Count;
    }

    /// <summary>
    /// A .po file is not imported: Godot loads it once and keeps the Translation in its cache, and
    /// the editor's translation preview uses that object until the editor restarts. Reloading it
    /// with <see cref="ResourceLoader.CacheMode.Replace"/> updates that very object, so everyone
    /// holding it sees the new texts. A language just added to the project is loaded for the first
    /// time. Then the open scene is told, so its labels translate again.
    /// </summary>
    public static void ReloadTranslations(IEnumerable<string> saved, IEnumerable<string> added) {
        var changed = false;
        foreach (var path in saved.Where(p => p.EndsWith(".po"))) {
            var isNew = added.Contains(path);
            if (!ResourceLoader.HasCached(path) && !isNew) continue;

            var translation = ResourceLoader.Load<Translation>(path, "", ResourceLoader.CacheMode.Replace);
            if (translation == null) continue;
            if (isNew && !TranslationServer.HasTranslation(translation)) TranslationServer.AddTranslation(translation);
            changed = true;
        }
        if (!changed) return;

        if (Engine.IsEditorHint()) EditorInterface.Singleton.GetEditedSceneRoot()?.PropagateNotification((int) NotificationTranslationChanged);
    }

    void ReloadPressed() {
        if (string.IsNullOrEmpty(_openPath)) return;
        var path = _openPath;
        _set = null;
        _openPath = "";
        Open(path);
    }

    // ---- editing -----------------------------------------------------------------------------

    void OnEdited() {
        AfterChange();
        ShowDetails();
        OfferCodeRename();
    }

    void OnDetailsEdited() {
        Table.RefreshRow(Table.SelectedRow);
        AfterChange();
    }

    /// <summary>After every change: keep the unsaved text where a reload finds it, and update what shows dirtiness.</summary>
    void AfterChange() {
        _unsaved.Clear();
        foreach (var file in _set.Files.Where(f => f.Dirty)) _unsaved[file.Path] = file.Catalog.Write();
        UpdateUi();
    }

    void OnRejected(string message) => SetStatus(message, warning: true);

    void OnCellSelected() => ShowDetails();

    void ShowDetails() {
        if (TranslationTable.GroupPathOf(Table.GetSelected()) is { } folder) Details.ShowFolder(folder);
        else Details.ShowRow(Table.SelectedRow, Table.SelectedFile);
    }

    /// <summary>
    /// Keys the table did not take come up here, before the editor's own shortcuts see them: so
    /// Ctrl+Z undoes the last edit of a translation, not of the open scene.
    /// </summary>
    public override void _GuiInput(InputEvent @event) {
        if (@event is not InputEventKey { Pressed: true } key || !key.IsCommandOrControlPressed() || Table.IsEditing) return;
        switch (key.Keycode) {
            case Key.Z when key.ShiftPressed:
            case Key.Y:
                Redo();
                break;
            case Key.Z:
                Undo();
                break;
            case Key.S:
                SaveAll();
                break;
            case Key.F:
                _search.GrabFocus();
                _search.SelectAll();
                break;
            default:
                return;
        }
        AcceptEvent();
    }

    public void Undo() {
        if (Catalogs == null || !_set.CanUndo) return;
        var name = _set.UndoName;
        _set.Undo();
        AfterReplay($"Undo: {name}");
    }

    /// <summary>After undo or redo: the table, and whether the code that went with the step could follow.</summary>
    void AfterReplay(string done) {
        // The step may have put code back.
        Usages.Refresh();
        Table.Rebuild();
        AfterChange();
        ShowDetails();
        if (_set.CodeProblems.Count == 0) {
            SetStatus(done);
            return;
        }
        foreach (var problem in _set.CodeProblems) GD.PushWarning($"Misspell: {problem}");
        SetStatus($"{done} — the code not everywhere: {_set.CodeProblems[0]}", warning: true);
    }

    public void Redo() {
        if (Catalogs == null || !_set.CanRedo) return;
        var name = _set.RedoName;
        _set.Redo();
        AfterReplay($"Redo: {name}");
    }

    void MergePressed() {
        if (Catalogs?.Template == null) return;
        var result = _set.MergeTemplate();
        Table.Rebuild();
        AfterChange();
        SetStatus($"Updated from {_set.Template.Path.GetFile()}: {result.Added} added, {result.Obsoleted} now obsolete, {result.Revived} back from obsolete, {result.Updated} with new references or plural.");
    }

    void AddLanguagePressed() => _languageDialog.AskNew();

    void OnLanguageMenu(string path) {
        var file = Catalogs?.Files.FirstOrDefault(f => f.Path == path);
        if (file != null) _languageDialog.AskFor(file);
    }

    void OnLanguageChosen(string path, string locale, string pluralForms, bool register) {
        if (Catalogs == null) return;
        if (string.IsNullOrEmpty(path)) {
            var file = _set.AddLanguage(locale, pluralForms);
            if (file == null) {
                SetStatus($"{locale} is already there.", warning: true);
                return;
            }
            if (register) _register.Add(file.Path);
            SetStatus($"Added {locale} as {file.Path}. It is written on the next save.");
        } else {
            var file = _set.Files.FirstOrDefault(f => f.Path == path);
            if (file == null) return;
            if (file.Locale != locale) _set.SetLanguage(file, locale, withRule: false);
            var error = string.IsNullOrEmpty(pluralForms) ? null : _set.SetPluralForms(file, pluralForms);
            if (error != null) SetStatus(error, warning: true);
        }
        Table.Rebuild();
        AfterChange();
        ShowDetails();
    }

    void AddMessagePressed() {
        if (Catalogs == null) return;
        // In the tree, a new key starts in the folder at hand.
        _newSource.Text = Table.SelectedFolder() ?? _search.Text;
        _newPlural.Text = "";
        _newContext.Text = "";
        _messageDialog.PopupCentered();
        _newSource.GrabFocus();
        _newSource.CaretColumn = _newSource.Text.Length;
    }

    void OnMessageConfirmed() {
        var plural = _newPlural.Text.Length > 0 ? _newPlural.Text : null;
        var error = _set.AddRow(_newContext.Text, _newSource.Text, plural);
        if (error != null) {
            SetStatus(error, warning: true);
            return;
        }
        _search.Text = "";
        Table.Search = "";
        Table.Rebuild();
        var row = _set.Rows.FirstOrDefault(r => r.Key == PoEntry.KeyOf(string.IsNullOrEmpty(_newContext.Text) ? null : _newContext.Text, _newSource.Text));
        var column = Table.ColumnOf(_set.Languages.FirstOrDefault());
        Table.SelectCell(row, 0, column >= 0 ? column : Table.ColumnOf(TranslationTable.ColumnKind.Source));
        Table.GrabFocus();
        AfterChange();
        ShowDetails();
    }

    void OnSearchChanged(string text) {
        Table.Search = text;
        Table.Rebuild();
    }

    void OnFilterPicked(long index) {
        Table.ShowOnly = (TranslationTable.Filter) _filter.GetItemId((int) index);
        Table.Rebuild();
    }

    /// <summary>Sources that are mostly keys laid out as paths are shown as a tree when opened; sentences rarely hold a slash.</summary>
    static bool LooksLikePaths(CatalogSet set) =>
        set.Rows.Count > 0 && set.Rows.Count(r => r.Id.Contains(TranslationTable.PathSeparator)) * 2 >= set.Rows.Count;

    public void ShowAsTree(bool on) {
        _treeToggle.SetPressedNoSignal(on);
        Table.TreeView = on;
        Table.Rebuild();
        UpdateUi();
    }

    /// <summary>A new folder in the selected one, or beside the selected text; its name opens for typing.</summary>
    void AddFolderPressed() => Table.NewFolder(Table.SelectedFolder()?.TrimEnd(TranslationTable.PathSeparator) ?? "");

    void OnTreeToggled(bool on) => ShowAsTree(on);

    void OnContextToggled(bool on) {
        Table.ShowContext = on;
        Table.Rebuild();
    }

    void OnDetailsToggled(bool on) => Details.Visible = on;

    // ---- status ------------------------------------------------------------------------------

    void UpdateUi() {
        var open = _set != null;
        var dirty = IsDirty;
        _save.Disabled = !dirty;
        _reload.Disabled = !open;
        // Updating from a template only means something with one; without, the button is not shown at all.
        _merge.Visible = open && _set.Template != null;
        _merge.Disabled = !open || !_set.Languages.Any();
        _addLanguage.Disabled = !open;
        _addMessage.Disabled = !open;
        _addFolder.Visible = open && Table.TreeView;
        _empty.Visible = !open;
        _split.Visible = open;
        if (open) Table.UpdateTitles();

        if (dirty != _wasDirty) {
            _wasDirty = dirty;
            EmitSignal(SignalName.DirtyStateChanged, dirty);
        }
        if (!open) SetStatus("Open a .po or .pot file — or pick a catalog above. Double-clicking a .po file in the FileSystem dock opens it here too.");
    }

    string Summary() {
        if (_set == null) return "";
        var parts = new List<string> { $"{_set.Rows.Count} texts" };
        foreach (var file in _set.Languages) {
            var stats = _set.StatsOf(file);
            var text = $"{file.Label} {stats.Translated}/{stats.Total}";
            if (stats.Fuzzy > 0) text += $" ({stats.Fuzzy} fuzzy)";
            if (file.Catalog.PluralFormsProblem is { } problem) text += $" ⚠ Plural-Forms: {problem}";
            parts.Add(text);
        }
        if (_set.Template == null) parts.Add("no template: the texts are what the languages have");
        var obsolete = _set.Languages.Sum(f => f.Catalog.Entries.Count(e => e.Obsolete));
        if (obsolete > 0) parts.Add($"{obsolete} obsolete kept in the files");
        return string.Join(" · ", parts);
    }

    public string StatusText => _status.Text;

    void SetStatus(string text, bool warning = false) {
        _status.Text = text;
        if (warning) _status.AddThemeColorOverride("font_color", new Color(1f, 0.7f, 0.3f));
        else _status.RemoveThemeColorOverride("font_color");
    }
}
#endif
