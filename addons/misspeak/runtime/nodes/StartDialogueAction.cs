using Godot;
using Misscore;

namespace Misspeak;

/// <summary>
/// Starts a dialogue from a behavior tree, a state machine — or from another dialogue. By default it
/// keeps running until that dialogue is over, so whatever comes after it waits for the talk to end.
/// Interrupted, it cancels the dialogue it started.
/// </summary>
[GlobalClass, Tool, NodeName("Start dialogue"), NodeGroup("Dialogue")]
public partial class StartDialogueAction : ActionNode {
    /// <summary>The dialogue to play. Left empty, the runner plays the one it has.</summary>
    [Export]
    public Dialogue Dialogue { get; set; }

    /// <summary>Name of the section to start at. Left empty, it is the dialogue's start section.</summary>
    [Export]
    public string Section { get; set; } = "";

    /// <summary>
    /// The <see cref="DialogueRunner"/> to play it on, from the actor. Left empty, it is the first
    /// one among the actor's children, else the first one in the scene.
    /// </summary>
    [Export]
    public NodePath Runner { get; set; } = new();

    /// <summary>On, the action runs until the dialogue is over. Off, it succeeds as soon as the dialogue has started.</summary>
    [Export]
    public bool WaitUntilFinished { get; set; } = true;

    DialogueRunner _runner;
    bool _started;

    public override string GetSummary() => Dialogue == null ? "" : Dialogue.ResourcePath.GetFile().GetBaseName();

    public override void BeforeRun(MissContext ctx) {
        _runner = DialogueRunner.Find(ctx.Actor, Runner);
        _started = _runner != null && _runner.Start(Dialogue, Section);
    }

    protected override MissStatus Run(MissContext ctx) {
        if (!_started) return MissStatus.Failure;
        if (!WaitUntilFinished || !IsInstanceValid(_runner)) return MissStatus.Success;
        return _runner.IsActive ? MissStatus.Running : MissStatus.Success;
    }

    public override void Interrupt(MissContext ctx) {
        if (_started && IsInstanceValid(_runner)) _runner.Cancel();
        _started = false;
    }

    protected override void OnCloned() {
        _runner = null;
        _started = false;
    }
}
