using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misspeak;

/// <summary>
/// What every <see cref="DialogueRunner"/> does, in one place: a dialogue box, the HUD or anything
/// else listens here instead of to each runner — including runners that come into the scene later.
/// <para>
/// The plugin makes it an autoload named <c>DialogueEvents</c>. Without that autoload it is made the
/// first time a runner or a listener asks for it (see <see cref="Of"/>), so nothing depends on it.
/// </para>
/// <para>
/// No [GlobalClass]: Godot refuses an autoload named like a global class, and nobody is meant to
/// put one in a scene by hand.
/// </para>
/// </summary>
public partial class DialogueEvents : Node {
    /// <summary>The name the plugin gives the autoload.</summary>
    public const string AutoloadName = "DialogueEvents";

    /// <summary>The one in the scene tree, or null before anything asked for it.</summary>
    public static DialogueEvents Instance { get; private set; }

    /// <summary>The hub of the tree <paramref name="from"/> is in — made and added to it if there is none yet.</summary>
    public static DialogueEvents Of(Node from) {
        if (Instance != null && IsInstanceValid(Instance)) return Instance;
        if (from == null || !from.IsInsideTree()) return null;

        // Deferred: the root may be busy setting up its children when the first runner asks.
        Instance = new DialogueEvents { Name = AutoloadName };
        from.GetTree().Root.CallDeferred(Node.MethodName.AddChild, Instance);
        return Instance;
    }

    [Signal]
    public delegate void DialogueStartedEventHandler(DialogueRunner runner);

    /// <summary>A line is to be shown, translated and with its placeholders filled in.</summary>
    [Signal]
    public delegate void LineShownEventHandler(DialogueRunner runner, string speaker, string text);

    /// <summary>The player has to pick one of these; answer with the runner's <see cref="DialogueRunner.Choose"/>.</summary>
    [Signal]
    public delegate void ChoicesOfferedEventHandler(DialogueRunner runner, string[] choices);

    /// <summary>A dialogue ended — said all there was, was cancelled, or its runner left the tree.</summary>
    [Signal]
    public delegate void DialogueFinishedEventHandler(DialogueRunner runner);

    /// <summary>
    /// The first dialogue started while none was under way, or the last one under way ended. Two at
    /// once count as one: the HUD comes back once neither runs any more.
    /// </summary>
    [Signal]
    public delegate void AnyActiveChangedEventHandler(bool active);

    readonly List<DialogueRunner> _active = [];

    /// <summary>Whether any dialogue is under way.</summary>
    public bool IsAnyActive => _active.Count > 0;

    /// <summary>The runners with a dialogue under way, the one started last at the end.</summary>
    public IReadOnlyList<DialogueRunner> ActiveRunners => _active;

    public override void _EnterTree() {
        // The autoload, or one made by Of: whichever came first is the one.
        if (Instance == null || !IsInstanceValid(Instance)) Instance = this;
    }

    public override void _ExitTree() {
        if (Instance == this) Instance = null;
    }

    internal void ReportStarted(DialogueRunner runner) {
        var wasActive = IsAnyActive;
        _active.Remove(runner);
        _active.Add(runner);
        EmitSignalDialogueStarted(runner);
        if (!wasActive) EmitSignalAnyActiveChanged(true);
    }

    internal void ReportLine(DialogueRunner runner, string speaker, string text) => EmitSignalLineShown(runner, speaker, text);

    internal void ReportChoices(DialogueRunner runner, string[] choices) => EmitSignalChoicesOffered(runner, choices);

    internal void ReportFinished(DialogueRunner runner) {
        if (!_active.Remove(runner)) return;
        EmitSignalDialogueFinished(runner);
        if (!IsAnyActive) EmitSignalAnyActiveChanged(false);
    }

    /// <summary>The runner started last that is still under way, or null.</summary>
    public DialogueRunner LatestActive() => _active.LastOrDefault(runner => IsInstanceValid(runner) && runner.IsActive);
}
