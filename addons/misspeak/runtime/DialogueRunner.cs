using Godot;
using Misscore;

namespace Misspeak;

/// <summary>
/// Plays <see cref="Dialogue"/>s and tells whoever shows them what to show. It draws nothing itself:
/// connect a dialogue box to <see cref="LineShown"/>, <see cref="ChoicesOffered"/> and
/// <see cref="DialogueFinished"/>, and have it call <see cref="Advance"/> and <see cref="Choose"/>.
/// <para>
/// Nothing runs until <see cref="Start"/> is called — by your code, by <see cref="Autostart"/>, or by
/// a <see cref="StartDialogueAction"/> in a behavior tree or a state machine. Stopping the runner
/// pauses the dialogue where it is; <see cref="Cancel"/> ends it.
/// </para>
/// </summary>
[GlobalClass, Tool]
public partial class DialogueRunner : MissRunner {
    /// <summary>Every runner in the scene tree is in this group, which is how actions find one.</summary>
    public const string Group = "misspeak_runners";

    /// <summary>The dialogue <see cref="Start"/> plays when it is given none. Shared, not consumed.</summary>
    [Export]
    public Dialogue Dialogue {
        get => SourceAs<Dialogue>();
        set => SetSource(value);
    }

    /// <summary>Starts <see cref="Dialogue"/> as soon as the runner is ready.</summary>
    [Export]
    public bool Autostart { get; set; }

    [Signal]
    public delegate void DialogueStartedEventHandler();

    /// <summary>A line is to be shown, translated and with its placeholders filled in.</summary>
    [Signal]
    public delegate void LineShownEventHandler(string speaker, string text);

    /// <summary>The player has to pick one of these; answer with <see cref="Choose"/> and its index.</summary>
    [Signal]
    public delegate void ChoicesOfferedEventHandler(string[] choices);

    [Signal]
    public delegate void DialogueFinishedEventHandler();

    public DialogueInstance Instance { get; private set; }

    /// <summary>Whether a dialogue is under way.</summary>
    public bool IsActive => Instance?.Active == true;

    public DialogueWait Waiting => Instance?.Waiting ?? DialogueWait.Nothing;

    /// <summary>Speaker of the line being shown, or empty.</summary>
    public string Speaker => Instance?.Speaker ?? "";

    /// <summary>Text of the line being shown, or empty.</summary>
    public string Text => Instance?.Text ?? "";

    /// <summary>The choices being offered, or none.</summary>
    public string[] Choices => Instance == null ? [] : [.. Instance.Choices];

    protected override bool HasInstance => Instance != null;

    public override void _Ready() {
        base._Ready();
        if (Engine.IsEditorHint()) return;

        AddToGroup(Group);
        if (Autostart) Start();
    }

    public override void _ExitTree() {
        if (Engine.IsEditorHint()) return;
        MisspeakDebug.Unregister(this);
    }

    protected override void BuildInstance() {
        if (Instance != null) MisspeakDebug.Unregister(this);
        Instance?.Cancel(Context(0));

        Instance = DialogueInstance.Create(Dialogue);
        if (Instance == null) {
            if (Dialogue != null) GD.PushWarning($"misspeak: {Name} has a dialogue without sections.");
            return;
        }
        MisspeakDebug.Register(this);

        Instance.Translate = (text, context) => Tr(text, context);
        Instance.Started += () => EmitSignalDialogueStarted();
        Instance.LineShown += _ => EmitSignalLineShown(Instance.Speaker, Instance.Text);
        Instance.ChoicesOffered += choices => EmitSignalChoicesOffered([.. choices]);
        Instance.Finished += () => EmitSignalDialogueFinished();
    }

    /// <summary>Running while a dialogue is under way, Success once there is nothing left to say.</summary>
    protected override MissStatus TickInstance(MissContext ctx) {
        Instance.Tick(ctx);
        return Instance.Active ? MissStatus.Running : MissStatus.Success;
    }

    protected override void InterruptInstance(MissContext ctx) => Instance.Interrupt(ctx);

    protected override void AfterTick() => MisspeakDebug.SendState(this);

    /// <summary>
    /// Starts a dialogue: the given one, which then becomes <see cref="Dialogue"/>, or the one the
    /// runner already has. One that is under way is cancelled first. False when there is nothing to
    /// start.
    /// </summary>
    /// <param name="section">Name of the section to start at, instead of the dialogue's start section.</param>
    public bool Start(Dialogue dialogue = null, string section = "") {
        if (dialogue != null && !ReferenceEquals(dialogue, Dialogue)) {
            Dialogue = dialogue;
            Blackboard.Declare(dialogue.Blackboard);
            Rebuild();
        }
        if (Instance == null) return false;
        if (string.IsNullOrEmpty(section)) return Instance.Start(Context(0));
        return Dialogue.FindSectionByName(section) is { } named && Instance.Start(Context(0), named.Id);
    }

    /// <summary>The language changed: what is on show is said again, in the new one.</summary>
    public override void _Notification(int what) {
        if (what == NotificationTranslationChanged && !Engine.IsEditorHint()) Instance?.Reshow(Context(0));
    }

    /// <summary>Carries on after a line that offered no choice. False unless the dialogue is waiting for that.</summary>
    public bool Advance() => Instance?.Advance(Context(0)) == true;

    /// <summary>Picks one of the choices offered, by its index. False when there is no such choice to pick.</summary>
    public bool Choose(int index) => Instance?.Choose(index, Context(0)) == true;

    /// <summary>Ends the dialogue where it is.</summary>
    public void Cancel() => Instance?.Cancel(Context(0));

    /// <summary>
    /// The runner an action means: the one at <paramref name="path"/>, taken from the actor — or,
    /// with no path, the first one among the actor's children, else the first one in the scene.
    /// </summary>
    public static DialogueRunner Find(Node actor, NodePath path) {
        if (actor == null || !IsInstanceValid(actor)) return null;
        if (path != null && !path.IsEmpty) return actor.GetNodeOrNull<DialogueRunner>(path);

        foreach (var child in actor.GetChildren()) {
            if (child is DialogueRunner own) return own;
        }
        return actor.IsInsideTree() ? actor.GetTree().GetFirstNodeInGroup(Group) as DialogueRunner : null;
    }
}
