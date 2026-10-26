#if TOOLS
using System.Linq;
using Godot;

namespace Misspell.Editor;

/// <summary>
/// Beside the table, everything about the selected message: its source in full, where it comes
/// from, the translator's comment and fuzzy flag for the selected language — and for a plural
/// message every language's forms with the numbers each is for, and a number to try them with.
/// </summary>
[Tool]
public partial class EntryDetails : ScrollContainer {
    /// <summary>A comment or flag was changed here.</summary>
    [Signal]
    public delegate void EditedEventHandler();

    /// <summary>Plain C#, handed over by the panel again after an assembly reload.</summary>
    public CatalogSet Catalogs;
    /// <summary>Where texts are used in the project; plain C# as well, so made again after a reload.</summary>
    public KeyUsages Usages {
        get => _usageIndex ??= new KeyUsages();
        set => _usageIndex = value;
    }
    KeyUsages _usageIndex;
    const int ShownUsages = 50;
    CatalogRow _row;
    CatalogFile _file;

    VBoxContainer _box;
    Label _source;
    Label _plural;
    Label _context;
    Label _notes;
    RichTextLabel _references;
    Label _languageTitle;
    CheckBox _fuzzy;
    TextEdit _comment;
    VBoxContainer _pluralBox;
    SpinBox _number;
    VBoxContainer _forms;
    Label _problems;
    VBoxContainer _usageBox;
    RichTextLabel _usages;
    VBoxContainer _guessBox;
    RichTextLabel _guesses;
    Label _sourceHeading;
    /// <summary>The folder shown instead of a message, in the tree; null while a message is.</summary>
    string _folder;
    bool _updating;

    public override void _Ready() {
        CustomMinimumSize = new Vector2(280, 0);
        HorizontalScrollMode = ScrollMode.Disabled;

        var margin = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride($"margin_{side}", 10);
        AddChild(margin);
        _box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _box.AddThemeConstantOverride("separation", 8);
        margin.AddChild(_box);

        _context = Wrapped();
        _context.AddThemeColorOverride("font_color", Accent);
        _source = Wrapped();
        _source.AddThemeFontSizeOverride("font_size", 16);
        _plural = Wrapped();
        _plural.Modulate = new Color(1, 1, 1, 0.7f);
        _sourceHeading = Heading("Source");
        _box.AddChild(_sourceHeading);
        _box.AddChild(_context);
        _box.AddChild(_source);
        _box.AddChild(_plural);

        _notes = Wrapped();
        _notes.Modulate = new Color(1, 1, 1, 0.7f);
        _box.AddChild(_notes);
        _references = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, TooltipText = "Open where this text comes from" };
        _references.Connect(RichTextLabel.SignalName.MetaClicked, new Callable(this, MethodName.OnReferenceClicked));
        _box.AddChild(_references);

        _problems = Wrapped();
        _problems.AddThemeColorOverride("font_color", new Color(1f, 0.7f, 0.3f));
        _box.AddChild(_problems);

        _usageBox = new VBoxContainer();
        _usageBox.AddThemeConstantOverride("separation", 8);
        _box.AddChild(_usageBox);
        _usageBox.AddChild(new HSeparator());
        _usageBox.AddChild(Heading("Used in"));
        _usages = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, MetaUnderlined = false, TooltipText = "Open where this text is used" };
        _usages.Connect(RichTextLabel.SignalName.MetaClicked, new Callable(this, MethodName.OnReferenceClicked));
        _usageBox.AddChild(_usages);

        _guessBox = new VBoxContainer();
        _guessBox.AddThemeConstantOverride("separation", 8);
        _box.AddChild(_guessBox);
        _guessBox.AddChild(new HSeparator());
        _guessBox.AddChild(Heading("Maybe used in"));
        _guesses = new RichTextLabel {
            BbcodeEnabled = true, FitContent = true, ScrollActive = false, MetaUnderlined = false,
            TooltipText = "Strings with holes that could make this text — what fills the holes is not known",
        };
        _guesses.Connect(RichTextLabel.SignalName.MetaClicked, new Callable(this, MethodName.OnReferenceClicked));
        _guessBox.AddChild(_guesses);

        _box.AddChild(new HSeparator());
        _languageTitle = Heading("");
        _box.AddChild(_languageTitle);
        _fuzzy = new CheckBox { Text = "Fuzzy", TooltipText = "A guess to check. The game does not use a fuzzy translation; editing the text clears the mark." };
        _fuzzy.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.OnFuzzyToggled));
        _box.AddChild(_fuzzy);
        _comment = new TextEdit {
            PlaceholderText = "Comment for translators of this language",
            CustomMinimumSize = new Vector2(0, 60),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            ScrollFitContentHeight = true,
        };
        _comment.Connect(Control.SignalName.FocusExited, new Callable(this, MethodName.OnCommentDone));
        _box.AddChild(_comment);

        _pluralBox = new VBoxContainer();
        _box.AddChild(_pluralBox);
        _pluralBox.AddChild(new HSeparator());
        _pluralBox.AddChild(Heading("Plural forms"));
        var tryRow = new HBoxContainer();
        tryRow.AddChild(new Label { Text = "Try n =" });
        _number = new SpinBox { MinValue = 0, MaxValue = 1_000_000, Value = 1, Rounded = true, TooltipText = "Which form each language takes for this number" };
        _number.Connect(Godot.Range.SignalName.ValueChanged, new Callable(this, MethodName.OnNumberChanged));
        tryRow.AddChild(_number);
        _pluralBox.AddChild(tryRow);
        _forms = new VBoxContainer();
        _pluralBox.AddChild(_forms);

        ShowNothing();
    }

    /// <summary>A section's title: small, in capitals and the editor's accent colour, with room above it.</summary>
    Label Heading(string text) {
        var label = new Label { Text = text, Uppercase = true };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", Accent);
        if (HasThemeFont("bold", "EditorFonts")) label.AddThemeFontOverride("font", GetThemeFont("bold", "EditorFonts"));
        return label;
    }

    Color Accent => HasThemeColor("accent_color", "Editor") ? GetThemeColor("accent_color", "Editor") : new Color(0.55f, 0.7f, 1f);

    static Label Wrapped() => new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };

    void ShowNothing() {
        foreach (var child in _box.GetChildren().OfType<Control>()) child.Visible = false;
    }

    /// <summary>Shows a message, and the language of the selected column (or the first one).</summary>
    public void ShowRow(CatalogRow row, CatalogFile file) {
        _row = row;
        _folder = null;
        _file = file ?? Catalogs?.Languages.FirstOrDefault();
        Refresh();
    }

    /// <summary>Shows a folder of the tree: where the texts in it are used, and the strings with holes that may make them.</summary>
    public void ShowFolder(string path) {
        _row = null;
        _folder = path;
        Refresh();
    }

    public void Refresh() {
        if (_folder != null) {
            RefreshFolder();
            return;
        }
        _sourceHeading.Text = "Source";
        if (Catalogs == null || _row == null || !Catalogs.Rows.Contains(_row)) {
            // After an undo the rows are new; find this one again by what identifies it.
            _row = _row == null ? null : Catalogs?.Rows.FirstOrDefault(r => r.Key == _row.Key);
            if (_row == null) {
                ShowNothing();
                return;
            }
        }
        if (_file != null && !Catalogs.Files.Contains(_file)) _file = Catalogs.Languages.FirstOrDefault();

        _updating = true;
        foreach (var child in _box.GetChildren().OfType<Control>()) child.Visible = true;
        var source = Catalogs.SourceOf(_row);

        _context.Text = _row.Context != null ? $"Context: {_row.Context}" : "";
        _context.Visible = _row.Context != null;
        _source.Text = _row.Id;
        _plural.Text = _row.IsPlural ? $"Plural: {_row.IdPlural}" : "";
        _plural.Visible = _row.IsPlural;

        var notes = source?.ExtractedComments ?? [];
        _notes.Text = string.Join("\n", notes);
        _notes.Visible = notes.Count > 0;

        var references = source?.References.SelectMany(r => r.Split(' ', System.StringSplitOptions.RemoveEmptyEntries)).ToList() ?? [];
        _references.Text = string.Join("\n", references.Select(r => $"[url={r}]{r}[/url]"));
        _references.Visible = references.Count > 0;

        var problems = Catalogs.Languages.SelectMany(f => Catalogs.ProblemsOf(_row, f).Select(p => $"⚠ {f.Label}: {p}")).ToList();
        _problems.Text = string.Join("\n", problems);
        _problems.Visible = problems.Count > 0;

        FillUsages();

        var entry = _file == null ? null : Catalogs.EntryOf(_row, _file);
        _languageTitle.Text = _file == null ? "No language yet" : $"{_file.Label} — {LocaleName(_file.Locale)}";
        _fuzzy.Visible = _comment.Visible = _file != null;
        _fuzzy.Disabled = entry == null;
        _fuzzy.SetPressedNoSignal(entry?.Fuzzy == true);
        var comment = string.Join("\n", entry?.TranslatorComments ?? []);
        if (!_comment.HasFocus() && _comment.Text != comment) _comment.Text = comment;

        _pluralBox.Visible = _row.IsPlural;
        if (_row.IsPlural) FillForms();
        _updating = false;
    }

    /// <summary>Every place in the scenes, resources and scripts with the source as a string — in a translation call, with this context — each a link with its line of code below.</summary>
    void FillUsages() {
        var none = Usages.ProbablyUnused(_row.Id, _row.Context) ? TranslationTable.UnusedTip : NotFound;
        _usages.Text = UsageList(Usages.Of(_row.Id, _row.Context).Select(u => (u, (string) null)).ToList(), none);
        var guesses = Usages.GuessesOf(_row.Id);
        _guesses.Text = UsageList(guesses.Select(u => (u, (string) null)).ToList(), "");
        _guessBox.Visible = guesses.Count > 0;
    }

    static string NotFound => $"Not found as a string in any {string.Join(", ", KeyUsages.Extensions.Select(e => "." + e))} file.";

    /// <summary>
    /// For a folder: its path and how many texts are in it, the uses of each of them — named by the
    /// rest of its key — and the strings with holes that may make one.
    /// </summary>
    void RefreshFolder() {
        ShowNothing();
        if (Catalogs == null) return;
        var inside = _folder + TranslationTable.PathSeparator;
        var rows = Catalogs.Rows.Where(r => r.Id.StartsWith(inside, System.StringComparison.Ordinal)).ToList();

        _sourceHeading.Text = "Folder";
        _source.Text = inside;
        _notes.Text = rows.Count == 1 ? "1 text" : $"{rows.Count} texts";
        foreach (var control in new Control[] { _sourceHeading, _source, _notes, _usageBox }) control.Visible = true;

        var usages = rows.SelectMany(r => Usages.Of(r.Id, r.Context).Select(u => (u, r.Id[inside.Length..]))).ToList();
        var none = rows.Count == 0 ? "No texts in it yet."
                 : rows.All(r => Usages.ProbablyUnused(r.Id, r.Context)) ? "Probably unused: not even a part of the keys inside is found in the project."
                 : "None of its texts found as a string.";
        _usages.Text = UsageList(usages, none);
        var guesses = Usages.GuessesIn(_folder, rows.Select(r => r.Id).ToList());
        _guesses.Text = UsageList(guesses.Select(u => (u, (string) null)).ToList(), "");
        _guessBox.Visible = guesses.Count > 0;
    }

    /// <summary>
    /// The uses grouped by file: the file's name (a link to it) with its folder dimmed after it, then
    /// one row per use — the line number and the line of code, the string in it marked, both
    /// opening the file at that line. When given, the text a use is of stands above its code.
    /// </summary>
    string UsageList(System.Collections.Generic.List<(KeyUsages.Usage Usage, string Text)> usages, string none) {
        var dim = Html(DimColor);
        var text = new System.Text.StringBuilder();
        if (usages.Count == 0) text.Append($"[color={dim}]{Escape(none)}[/color]");

        var shown = usages.Take(ShownUsages).ToList();
        foreach (var file in shown.GroupBy(u => u.Usage.Path)) {
            if (text.Length > 0) text.Append("\n[font_size=6] [/font_size]\n");
            var path = file.Key.TrimPrefix("res://");
            var folder = path.GetBaseDir();
            text.Append($"[url={file.Key}][b]{Escape(path.GetFile())}[/b][/url]");
            if (folder.Length > 0) text.Append($"  [font_size=11][color={dim}]{Escape(folder)}/[/color][/font_size]");
            text.Append("\n[table=2]");
            foreach (var (usage, of) in file.OrderBy(u => u.Usage.Line)) {
                var link = $"[url={usage.Reference}]";
                text.Append($"[cell padding=0,2,8,2]{link}[color={Html(Accent)}]{usage.Line}[/color][/url][/cell][cell expand=1 padding=0,2,0,2]");
                if (of != null) text.Append($"[font_size=13][b]{Escape(of)}[/b][/font_size]\n");
                text.Append($"{link}[font_size=12][code]{Marked(usage)}[/code][/font_size][/url][/cell]");
            }
            text.Append("[/table]");
        }
        if (usages.Count > ShownUsages) text.Append($"\n[color={dim}]… and {usages.Count - ShownUsages} more[/color]");
        return text.ToString();
    }

    const int ExcerptLength = 60;

    /// <summary>The use's line of code, cut around its string when long, with the string in the accent colour.</summary>
    string Marked(KeyUsages.Usage usage) {
        var code = usage.Code;
        var literal = usage.Literal.IsPattern ? "" : usage.Literal.Text;
        var at = literal.Length > 0 ? code.IndexOf(literal, System.StringComparison.Ordinal) : -1;
        if (code.Length > ExcerptLength) {
            var start = System.Math.Clamp(at < 0 ? 0 : at - 16, 0, code.Length - ExcerptLength);
            var excerpt = code.Substring(start, ExcerptLength);
            code = (start > 0 ? "…" : "") + excerpt + (start + ExcerptLength < code.Length ? "…" : "");
            at = at < 0 ? -1 : code.IndexOf(literal, System.StringComparison.Ordinal);
        }
        var dim = Html(DimColor);
        if (at < 0) return $"[color={dim}]{Escape(code)}[/color]";
        var end = System.Math.Min(code.Length, at + literal.Length);
        return $"[color={dim}]{Escape(code[..at])}[/color][color={Html(Accent)}]{Escape(code[at..end])}[/color][color={dim}]{Escape(code[end..])}[/color]";
    }

    Color DimColor => (HasThemeColor("font_color", "Label") ? GetThemeColor("font_color", "Label") : Colors.White) with { A = 0.6f };

    static string Html(Color color) => "#" + color.ToHtml();

    static string Escape(string text) => text.Replace("[", "[lb]");

    /// <summary>One block per language: each form's name, the numbers it is for, and its text; the form for the tried number is marked.</summary>
    void FillForms() {
        foreach (var child in _forms.GetChildren()) child.QueueFree();
        var n = (long) _number.Value;

        foreach (var file in Catalogs.Languages) {
            var plurals = file.Catalog.Plurals;
            var entry = Catalogs.EntryOf(_row, file);
            var title = new Label { Text = $"{file.Label} · {plurals.Count} form{(plurals.Count == 1 ? "" : "s")}", TooltipText = plurals.Header };
            title.AddThemeColorOverride("font_color", Accent);
            _forms.AddChild(title);

            // Each form as two lines — what it is for, then its text indented below — so long
            // texts wrap instead of being cut off in the narrow panel.
            var chosen = plurals.FormFor(n);
            for (var form = 0; form < plurals.Count; form++) {
                var mark = form == chosen ? "▶ " : "   ";
                _forms.AddChild(new Label {
                    Text = $"{mark}{plurals.Names[form]} · n = {plurals.SampleText(form, 3)}",
                    TooltipText = string.Join(", ", plurals.Samples(form).Take(12)),
                });
                var text = entry?.StringAt(form) ?? "";
                var shown = form == chosen && text.Length > 0 ? Fill(text, n) : text;
                var value = new Label {
                    Text = shown.Length > 0 ? shown : "—",
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                if (form == chosen) value.AddThemeColorOverride("font_color", new Color(0.55f, 0.95f, 0.6f));
                var indent = new MarginContainer();
                indent.AddThemeConstantOverride("margin_left", 22);
                indent.AddChild(value);
                _forms.AddChild(indent);
            }
        }
    }

    /// <summary>The number put into the usual placeholders, so the tried form reads as it would in the game.</summary>
    static string Fill(string text, long n) => text.Replace("{n}", n.ToString()).Replace("%d", n.ToString()).Replace("{count}", n.ToString());

    static string LocaleName(string locale) {
        if (string.IsNullOrEmpty(locale)) return "language unknown";
        var name = TranslationServer.GetLocaleName(locale);
        return string.IsNullOrEmpty(name) ? locale : name;
    }

    void OnNumberChanged(double _) {
        if (_row?.IsPlural == true) FillForms();
    }

    void OnFuzzyToggled(bool on) {
        if (_updating || _file == null) return;
        Catalogs.SetFuzzy(_row, _file, on);
        EmitSignal(SignalName.Edited);
    }

    void OnCommentDone() {
        if (_updating || _file == null || _row == null) return;
        Catalogs.SetComment(_row, _file, _comment.Text);
        EmitSignal(SignalName.Edited);
    }

    /// <summary>
    /// <c>res://ui/shop.tscn</c> opens the scene, <c>res://shop.gd:12</c> the script at that line;
    /// a resource opens in the inspector; anything else is shown in the FileSystem dock.
    /// </summary>
    void OnReferenceClicked(Variant meta) => OpenReference(meta.AsString());

    void OpenReference(string reference) {
        if (!Engine.IsEditorHint()) return;
        var path = reference;
        var line = -1;
        var colon = reference.LastIndexOf(':');
        if (colon > "res://".Length && int.TryParse(reference[(colon + 1)..], out var parsed)) {
            path = reference[..colon];
            line = parsed;
        }
        if (!path.StartsWith("res://")) path = "res://" + path.TrimStart('/');
        if (!ResourceLoader.Exists(path) && !FileAccess.FileExists(path)) return;

        var editor = EditorInterface.Singleton;
        if (path.EndsWith(".tscn") || path.EndsWith(".scn")) editor.OpenSceneFromPath(path);
        else if (ResourceLoader.Load(path) is Script script) editor.EditScript(script, line);
        else if (path.EndsWith(".tres") || path.EndsWith(".res")) editor.EditResource(ResourceLoader.Load(path));
        else editor.SelectFile(path);
    }
}
#endif
