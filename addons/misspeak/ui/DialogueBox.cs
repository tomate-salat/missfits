using Godot;

namespace Misspeak;

/// <summary>
/// A plain dialogue box, as an example and a starting point: it shows what a
/// <see cref="DialogueRunner"/> says along the bottom of the screen, one button per choice, and
/// carries on with <c>ui_accept</c> or a click. It hides itself while nothing is said.
/// <para>
/// Nothing in the addon depends on it. Copy it and make it yours, or write your own against the
/// runner's signals — this file is all there is to it.
/// </para>
/// </summary>
[GlobalClass]
public partial class DialogueBox : Control {
    /// <summary>The runner to show. Left empty, it is the first one in the scene.</summary>
    [Export]
    public DialogueRunner Runner { get; set; }

    /// <summary>How fast a line is written out, in characters per second. 0 shows it at once.</summary>
    [Export(PropertyHint.Range, "0,200,1,or_greater")]
    public float CharactersPerSecond { get; set; } = 45f;

    PanelContainer _panel;
    Label _speaker;
    RichTextLabel _text;
    VBoxContainer _choices;
    Label _hint;
    double _written;

    /// <summary>Whether the line on show is still being written out.</summary>
    public bool Writing => _text != null && _text.VisibleCharacters >= 0 && _text.VisibleCharacters < _text.GetTotalCharacterCount();

    public override void _Ready() {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Build();

        Runner ??= GetTree().GetFirstNodeInGroup(DialogueRunner.Group) as DialogueRunner;
        if (Runner == null) {
            GD.PushWarning("misspeak: the dialogue box found no DialogueRunner to show.");
            Hide();
            return;
        }

        Runner.DialogueStarted += Show;
        Runner.LineShown += OnLineShown;
        Runner.ChoicesOffered += OnChoicesOffered;
        Runner.DialogueFinished += OnFinished;

        // A dialogue may already be under way: the runner starts before the box when it comes first in the scene.
        Visible = Runner.IsActive;
        if (Runner.Text != "") OnLineShown(Runner.Speaker, Runner.Text);
        if (Runner.Choices.Length > 0) OnChoicesOffered(Runner.Choices);
    }

    public override void _ExitTree() {
        if (Runner == null || !IsInstanceValid(Runner)) return;

        Runner.DialogueStarted -= Show;
        Runner.LineShown -= OnLineShown;
        Runner.ChoicesOffered -= OnChoicesOffered;
        Runner.DialogueFinished -= OnFinished;
    }

    void Build() {
        _panel = new PanelContainer { Name = "Panel", MouseFilter = MouseFilterEnum.Stop };
        _panel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        _panel.GrowVertical = GrowDirection.Begin;
        _panel.OffsetLeft = 32;
        _panel.OffsetRight = -32;
        _panel.OffsetBottom = -24;
        _panel.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnPanelInput));
        AddChild(_panel);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        foreach (var side in new[] { "margin_left", "margin_right" }) margin.AddThemeConstantOverride(side, 18);
        foreach (var side in new[] { "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(side, 12);
        _panel.AddChild(margin);

        var rows = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        rows.AddThemeConstantOverride("separation", 8);
        margin.AddChild(rows);

        _speaker = new Label { Name = "Speaker", MouseFilter = MouseFilterEnum.Ignore };
        _speaker.AddThemeColorOverride("font_color", new Color("#e3b341"));
        rows.AddChild(_speaker);

        _text = new RichTextLabel {
            Name = "Text",
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(0, 52),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        rows.AddChild(_text);

        _choices = new VBoxContainer { Name = "Choices" };
        _choices.AddThemeConstantOverride("separation", 4);
        rows.AddChild(_choices);

        _hint = new Label { Name = "Hint", Text = "▼", HorizontalAlignment = HorizontalAlignment.Right, MouseFilter = MouseFilterEnum.Ignore };
        _hint.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.5f));
        rows.AddChild(_hint);
    }

    void OnLineShown(string speaker, string text) {
        Show();
        _speaker.Text = speaker;
        _speaker.Visible = speaker != "";
        _text.Text = text;
        _text.Visible = true;
        _written = 0;
        _text.VisibleCharacters = CharactersPerSecond > 0 ? 0 : -1;
        ClearChoices();
        Refresh();
    }

    void OnChoicesOffered(string[] choices) {
        Show();
        ClearChoices();

        // Choices that come without a line of their own leave the last one standing above them.
        for (var i = 0; i < choices.Length; i++) {
            var button = new Button { Text = choices[i], Alignment = HorizontalAlignment.Left };
            button.Pressed += Pick(i);
            _choices.AddChild(button);
        }
        Refresh();
    }

    System.Action Pick(int index) => () => Runner.Choose(index);

    void OnFinished() {
        ClearChoices();
        _text.Text = "";
        Hide();
    }

    void ClearChoices() {
        foreach (var child in _choices.GetChildren()) {
            _choices.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>Choices wait until the line is written out; the hint to carry on shows when there are none.</summary>
    void Refresh() {
        var offered = _choices.GetChildCount() > 0;
        _choices.Visible = offered && !Writing;
        _hint.Visible = !offered && !Writing && Runner?.Waiting == DialogueWait.Advance;
        if (!_choices.Visible) return;

        // So that the keyboard alone gets through a dialogue.
        var focused = GetViewport().GuiGetFocusOwner();
        if (focused == null || !_choices.IsAncestorOf(focused)) (_choices.GetChild(0) as Button)?.GrabFocus();
    }

    public override void _Process(double delta) {
        if (!Visible || !Writing) return;

        _written += delta * CharactersPerSecond;
        _text.VisibleCharacters = Mathf.Min((int) _written, _text.GetTotalCharacterCount());
        if (!Writing) {
            _text.VisibleCharacters = -1;
            Refresh();
        }
    }

    public override void _UnhandledInput(InputEvent @event) {
        if (!Visible || !@event.IsActionPressed("ui_accept")) return;
        if (Proceed()) GetViewport().SetInputAsHandled();
    }

    void OnPanelInput(InputEvent @event) {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Proceed();
    }

    /// <summary>
    /// What a press of the accept key or a click on the box does: writes the line out if it is still
    /// being written, otherwise carries on. True when it did either.
    /// </summary>
    public bool Proceed() {
        if (Runner == null) return false;

        if (Writing) {
            _text.VisibleCharacters = -1;
            Refresh();
            return true;
        }
        return Runner.Advance();
    }
}
