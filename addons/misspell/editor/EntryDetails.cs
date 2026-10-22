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
    bool _updating;

    public override void _Ready() {
        CustomMinimumSize = new Vector2(280, 0);
        HorizontalScrollMode = ScrollMode.Disabled;

        _box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _box.AddThemeConstantOverride("separation", 6);
        AddChild(_box);

        _context = Wrapped();
        _source = Wrapped();
        _plural = Wrapped();
        _box.AddChild(Heading("Source"));
        _box.AddChild(_context);
        _box.AddChild(_source);
        _box.AddChild(_plural);

        _notes = Wrapped();
        _box.AddChild(_notes);
        _references = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, TooltipText = "Open where this text comes from" };
        _references.Connect(RichTextLabel.SignalName.MetaClicked, new Callable(this, MethodName.OnReferenceClicked));
        _box.AddChild(_references);

        _problems = Wrapped();
        _problems.AddThemeColorOverride("font_color", new Color(1f, 0.7f, 0.3f));
        _box.AddChild(_problems);

        _box.AddChild(new HSeparator());
        _languageTitle = Heading("");
        _box.AddChild(_languageTitle);
        _fuzzy = new CheckBox { Text = "Fuzzy — a guess to check; the game does not use it" };
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

    static Label Heading(string text) {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 13);
        label.AddThemeColorOverride("font_color", new Color(0.65f, 0.75f, 1f));
        return label;
    }

    static Label Wrapped() => new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };

    void ShowNothing() {
        foreach (var child in _box.GetChildren().OfType<Control>()) child.Visible = false;
    }

    /// <summary>Shows a message, and the language of the selected column (or the first one).</summary>
    public void ShowRow(CatalogRow row, CatalogFile file) {
        _row = row;
        _file = file ?? Catalogs?.Languages.FirstOrDefault();
        Refresh();
    }

    public void Refresh() {
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

    /// <summary>One block per language: each form's name, the numbers it is for, and its text; the form for the tried number is marked.</summary>
    void FillForms() {
        foreach (var child in _forms.GetChildren()) child.QueueFree();
        var n = (long) _number.Value;

        foreach (var file in Catalogs.Languages) {
            var plurals = file.Catalog.Plurals;
            var entry = Catalogs.EntryOf(_row, file);
            var title = new Label { Text = $"{file.Label} · {plurals.Count} form{(plurals.Count == 1 ? "" : "s")}", TooltipText = plurals.Header };
            title.AddThemeColorOverride("font_color", new Color(0.65f, 0.75f, 1f));
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
    /// anything else is shown in the FileSystem dock.
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
        else editor.SelectFile(path);
    }
}
#endif
