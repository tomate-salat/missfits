using Godot;
using Misspeak;

/// <summary>
/// Demo of Misspeak: a talk with a smith, shown by the example dialogue box. Enter or a click carries
/// on, L switches between English and German mid-talk, and once the talk is over Enter starts it again.
/// </summary>
public partial class SpeakDemo : Node {
    [Export]
    public DialogueRunner Runner { get; set; }

    [Export]
    public Label Hint { get; set; }

    public override void _Ready() {
        // Normally listed under Project Settings → Localization; loaded here so the demo needs no setup.
        TranslationServer.AddTranslation(GD.Load<Translation>("res://misspeak demo/demo_dialogue.de.po"));
        TranslationServer.SetLocale("en");

        Runner.DialogueStarted += () => Hint.Hide();
        Runner.DialogueFinished += () => Hint.Show();
        Hint.Visible = !Runner.IsActive;
    }

    public override void _UnhandledInput(InputEvent @event) {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.L }) {
            TranslationServer.SetLocale(TranslationServer.GetLocale().StartsWith("de") ? "en" : "de");
        }
        else if (!Runner.IsActive && @event.IsActionPressed("ui_accept")) {
            Runner.Start();
        }
    }
}
