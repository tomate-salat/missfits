using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misspeak;

/// <summary>
/// Shows the dialogues of every <see cref="DialogueRunner"/> in the scene — put one in a level, and
/// NPCs that come later find it too. It listens to <see cref="DialogueEvents"/>, carries on with
/// <c>ui_accept</c> or a click on <see cref="ClickArea"/>, and hides itself while nothing is said.
/// <para>
/// It draws nothing itself. What is shown is up to the parts below it in the scene, found by their
/// interfaces: <see cref="IDialogueSpeakerView"/>, <see cref="IDialogueTextView"/>,
/// <see cref="IDialogueChoiceView"/> and <see cref="IDialogueContinueHint"/>. To change the look,
/// inherit <c>dialogue_box.tscn</c>, restyle and rearrange it, or swap a part for your own.
/// </para>
/// <para>
/// A dialogue started while another is on show takes the box over. When it ends, the box goes back
/// to the one before, if that is still under way.
/// </para>
/// </summary>
[GlobalClass]
public partial class DialogueBox : Control {
    /// <summary>Where a click carries on, like <c>ui_accept</c>. Left empty, clicks do nothing.</summary>
    [Export]
    public Control ClickArea { get; set; }

    /// <summary>The runner whose dialogue is on show, or null.</summary>
    public DialogueRunner Showing { get; private set; }

    readonly List<IDialogueSpeakerView> _speakers = [];
    readonly List<IDialogueTextView> _texts = [];
    readonly List<IDialogueChoiceView> _choiceViews = [];
    readonly List<IDialogueContinueHint> _hints = [];

    /// <summary>Choices that wait for the line above them to be written out.</summary>
    string[] _choices = [];

    /// <summary>The choices the parts are showing.</summary>
    string[] _offered = [];

    /// <summary>Whether a part is still writing out the line on show.</summary>
    public bool Writing => _texts.Any(view => view.Writing);

    public override void _Ready() {
        Collect(this);
        foreach (var view in _texts) view.WritingFinished += Refresh;
        foreach (var view in _choiceViews) view.Chosen += OnChosen;
        ClickArea?.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnClickAreaInput));
        Clear();
        Hide();
    }

    public override void _EnterTree() {
        var events = DialogueEvents.Of(this);
        if (events == null) return;

        events.DialogueStarted += OnStarted;
        events.LineShown += OnLineShown;
        events.ChoicesOffered += OnChoicesOffered;
        events.DialogueFinished += OnFinished;

        // A dialogue may already be under way: a runner that comes first in the scene starts before the box.
        CallDeferred(MethodName.ShowLatest);
    }

    public override void _ExitTree() {
        if (DialogueEvents.Instance is not { } events || !IsInstanceValid(events)) return;

        events.DialogueStarted -= OnStarted;
        events.LineShown -= OnLineShown;
        events.ChoicesOffered -= OnChoicesOffered;
        events.DialogueFinished -= OnFinished;
    }

    void Collect(Node node) {
        foreach (var child in node.GetChildren()) {
            if (child is IDialogueSpeakerView speaker) _speakers.Add(speaker);
            if (child is IDialogueTextView text) _texts.Add(text);
            if (child is IDialogueChoiceView choices) _choiceViews.Add(choices);
            if (child is IDialogueContinueHint hint) _hints.Add(hint);
            Collect(child);
        }
    }

    // ---- what the runners say -----------------------------------------------------------------

    void OnStarted(DialogueRunner runner) {
        Showing = runner;
        Clear();
        Show();
    }

    void OnLineShown(DialogueRunner runner, string speaker, string text) {
        if (runner != Showing) return;

        _choices = [];
        foreach (var view in _speakers) view.ShowSpeaker(speaker);
        foreach (var view in _texts) view.ShowText(text);
        Show();
        Refresh();
    }

    void OnChoicesOffered(DialogueRunner runner, string[] choices) {
        if (runner != Showing) return;

        // Choices that come without a line of their own leave the last one standing above them.
        _choices = choices;
        Show();
        Refresh();
    }

    void OnFinished(DialogueRunner runner) {
        if (runner != Showing) return;

        Showing = null;
        Clear();
        Hide();
        ShowLatest();
    }

    /// <summary>Takes up the dialogue started last that is still under way, as it stands now — if there is one.</summary>
    void ShowLatest() {
        if (Showing != null || DialogueEvents.Instance?.LatestActive() is not { } runner) return;

        Showing = runner;
        Clear();
        Show();
        if (runner.Text != "") OnLineShown(runner, runner.Speaker, runner.Text);
        if (runner.Choices.Length > 0) OnChoicesOffered(runner, runner.Choices);
    }

    void Clear() {
        _choices = [];
        _offered = [];
        foreach (var view in _speakers) view.ShowSpeaker("");
        foreach (var view in _texts) view.ShowText("");
        foreach (var view in _choiceViews) view.ShowChoices([]);
        foreach (var view in _hints) view.ShowHint(false);
    }

    /// <summary>Choices wait until the line is written out; the hint to carry on shows when there are none.</summary>
    void Refresh() {
        if (Showing == null) return;

        // Offered again only when they changed: that would take the focus off the one the player is on.
        var writing = Writing;
        var offered = writing ? [] : _choices;
        if (!offered.SequenceEqual(_offered)) {
            _offered = offered;
            foreach (var view in _choiceViews) view.ShowChoices(offered);
        }
        foreach (var view in _hints) view.ShowHint(!writing && _choices.Length == 0 && Showing.Waiting == DialogueWait.Advance);
    }

    void OnChosen(int index) => Showing?.Choose(index);

    // ---- carrying on --------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event) {
        if (!Visible || !@event.IsActionPressed("ui_accept")) return;
        if (Proceed()) GetViewport().SetInputAsHandled();
    }

    void OnClickAreaInput(InputEvent @event) {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Proceed();
    }

    /// <summary>
    /// What a press of the accept key or a click does: writes the line out if it is still being
    /// written, otherwise carries on. True when it did either.
    /// </summary>
    public bool Proceed() {
        if (Showing == null) return false;

        if (Writing) {
            foreach (var view in _texts) view.FinishWriting();
            return true;
        }
        return Showing.Advance();
    }
}
