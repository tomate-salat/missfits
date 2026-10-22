using Godot;

/// <summary>
/// Demo of Misspell's plural support in the game: the same two sentences for a number you pick, in
/// English, German, Polish (three forms), Arabic (six) and Japanese (one). Up and Down change the
/// number, L goes to the next language. Open <c>messages.pot</c> in the Misspell screen to see the
/// translations as a table.
/// </summary>
public partial class SpellDemo : Control {
    static readonly string[] Locales = ["en", "de", "pl", "ar", "ja"];

    [Export]
    public Label Title { get; set; }

    [Export]
    public Label Coins { get; set; }

    [Export]
    public Label Enemies { get; set; }

    [Export]
    public Label Words { get; set; }

    [Export]
    public Label Language { get; set; }

    int _n = 1;
    int _locale;

    public override void _Ready() {
        // Normally listed under Project Settings → Localization; loaded here so the demo needs no setup.
        foreach (var locale in Locales[1..]) TranslationServer.AddTranslation(GD.Load<Translation>($"res://misspell demo/{locale}.po"));
        TranslationServer.SetLocale("en");
        Show();
    }

    public override void _UnhandledInput(InputEvent @event) {
        if (@event is not InputEventKey { Pressed: true } key) return;
        switch (key.Keycode) {
            case Key.Up:
                _n++;
                break;
            case Key.Down:
                _n = Mathf.Max(0, _n - 1);
                break;
            case Key.L when !key.Echo:
                _locale = (_locale + 1) % Locales.Length;
                TranslationServer.SetLocale(Locales[_locale]);
                break;
            default:
                return;
        }
        Show();
    }

    new void Show() {
        var n = _n.ToString();
        Title.Text = Tr("Misspell demo — Up/Down: change the number · L: next language");
        // TrN picks the form by the language's Plural-Forms rule; the number goes in afterwards.
        Coins.Text = TrN("You have {n} coin.", "You have {n} coins.", _n).Replace("{n}", n);
        Enemies.Text = TrN("{n} enemy remains.", "{n} enemies remain.", _n).Replace("{n}", n);
        // The same word, told apart by its context.
        Words.Text = $"{Tr("Close", "menu")}  ·  {Tr("Close", "door")}";
        Language.Text = $"{Locales[_locale]} · n = {_n}";
    }
}
