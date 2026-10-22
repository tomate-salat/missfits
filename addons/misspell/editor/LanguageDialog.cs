#if TOOLS
using System.Linq;
using Godot;

namespace Misspell.Editor;

/// <summary>
/// The language of a file and its plural rule — for a new language, or one already there. The rule
/// is checked as it is typed: each form with the numbers it is for, so a wrong rule shows before
/// it is saved.
/// </summary>
[Tool]
public partial class LanguageDialog : ConfirmationDialog {
    /// <summary>OK was pressed: the locale, the <c>Plural-Forms</c> header, and for a new file whether to add it to the project's translations.</summary>
    [Signal]
    public delegate void ChosenEventHandler(string path, string locale, string pluralForms, bool register);

    LineEdit _locale;
    Label _localeName;
    OptionButton _known;
    LineEdit _rule;
    Label _error;
    GridContainer _preview;
    CheckBox _register;
    string _path = "";
    bool _updating;

    public override void _Ready() {
        MinSize = new Vector2I(520, 0);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);

        var grid = new GridContainer { Columns = 2 };
        box.AddChild(grid);

        grid.AddChild(new Label { Text = "Language" });
        var localeRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _locale = new LineEdit { PlaceholderText = "de, pt_BR, pl …", CustomMinimumSize = new Vector2(120, 0) };
        _locale.Connect(LineEdit.SignalName.TextChanged, new Callable(this, MethodName.OnLocaleChanged));
        localeRow.AddChild(_locale);
        _localeName = new Label { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        localeRow.AddChild(_localeName);
        grid.AddChild(localeRow);

        grid.AddChild(new Label { Text = "Plural rule" });
        _known = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _known.AddItem("Custom", 0);
        for (var i = 0; i < PluralRules.All.Length; i++) {
            var rule = PluralRules.All[i];
            var languages = string.Join(", ", rule.Languages.Take(6)) + (rule.Languages.Length > 6 ? ", …" : "");
            _known.AddItem($"{rule.Names.Length} — {string.Join("/", rule.Names)} ({languages})", i + 1);
        }
        _known.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnKnownSelected));
        grid.AddChild(_known);

        grid.AddChild(new Label { Text = "Plural-Forms" });
        _rule = new LineEdit { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, PlaceholderText = "nplurals=2; plural=(n != 1);" };
        _rule.Connect(LineEdit.SignalName.TextChanged, new Callable(this, MethodName.OnRuleChanged));
        grid.AddChild(_rule);

        _error = new Label();
        _error.AddThemeColorOverride("font_color", new Color(1f, 0.45f, 0.4f));
        box.AddChild(_error);

        _preview = new GridContainer { Columns = 2 };
        _preview.AddThemeConstantOverride("h_separation", 12);
        box.AddChild(_preview);

        _register = new CheckBox { Text = "Add to Project Settings → Localization → Translations", ButtonPressed = true };
        box.AddChild(_register);

        Connect(AcceptDialog.SignalName.Confirmed, new Callable(this, MethodName.OnConfirmed));
    }

    /// <summary>Asks for a new language.</summary>
    public void AskNew() {
        Title = "Add language";
        OkButtonText = "Add";
        _path = "";
        _register.Visible = true;
        _locale.Editable = true;
        _locale.Text = "";
        _rule.Text = "";
        Update(fromLocale: true);
        PopupCentered();
        _locale.GrabFocus();
    }

    /// <summary>Shows the language and plural rule of a file, to change them.</summary>
    public void AskFor(CatalogFile file) {
        Title = $"Language of {file.Path.GetFile()}";
        OkButtonText = "Apply";
        _path = file.Path;
        _register.Visible = false;
        _locale.Text = file.Locale;
        _rule.Text = file.Catalog.GetHeader(PoCatalog.PluralFormsField) ?? "";
        Update(fromLocale: _rule.Text.Length == 0);
        PopupCentered();
    }

    void OnLocaleChanged(string _) => Update(fromLocale: true);

    void OnRuleChanged(string _) => Update(fromLocale: false);

    void OnKnownSelected(long index) {
        if (index <= 0) return;
        _rule.Text = PluralRules.All[index - 1].Header;
        Update(fromLocale: false);
    }

    /// <summary>
    /// With <paramref name="fromLocale"/>, a language with a known rule gets that rule — unless one
    /// was typed by hand that differs, which is kept and shown as differing.
    /// </summary>
    void Update(bool fromLocale) {
        if (_updating) return;
        _updating = true;

        var locale = _locale.Text.Trim();
        var known = PluralRules.For(locale);
        _localeName.Text = locale.Length == 0 ? "" : TranslationServer.GetLocaleName(PluralRules.Normalize(locale)) is { Length: > 0 } name ? name : "unknown to Godot";

        if (fromLocale && known != null && (_rule.Text.Trim().Length == 0 || PluralRules.All.Any(r => r.Header == _rule.Text.Trim()))) _rule.Text = known.Header;

        var forms = PluralForms.TryParse(_rule.Text, out var error);
        var index = forms == null ? -1 : System.Array.FindIndex(PluralRules.All, r => r.Forms.SameAs(forms));
        _known.Select(index + 1);

        foreach (var child in _preview.GetChildren()) child.QueueFree();
        if (forms == null) {
            _error.Text = _rule.Text.Trim().Length == 0 ? "No rule: the language counts as having one and other, as English." : error;
        } else {
            _error.Text = forms.UnreachableForms().Any() ? $"No number takes form {string.Join(", ", forms.UnreachableForms())} — check the rule." : "";
            if (known != null && !known.Forms.SameAs(forms)) _error.Text += (_error.Text.Length > 0 ? "\n" : "") + $"Differs from the usual rule for {locale}.";
            for (var form = 0; form < forms.Count; form++) {
                _preview.AddChild(new Label { Text = $"{form} · {forms.Names[form]}" });
                _preview.AddChild(new Label { Text = $"n = {forms.SampleText(form, 6)}" });
            }
        }

        GetOkButton().Disabled = locale.Length == 0 || (_rule.Text.Trim().Length > 0 && forms == null);
        _updating = false;
    }

    void OnConfirmed() {
        var rule = PluralForms.TryParse(_rule.Text, out _)?.Header ?? "";
        EmitSignal(SignalName.Chosen, _path, PluralRules.Normalize(_locale.Text), rule, _register.Visible && _register.ButtonPressed);
    }
}
#endif
