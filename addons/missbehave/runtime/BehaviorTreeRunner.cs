using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Drives one <see cref="BehaviorTree"/> for one actor. Add it as a child of the actor and assign a
/// tree resource; the same resource can be shared by any number of runners, because each builds its
/// own runtime clone.
/// <para>
/// The Inspector lists the tree's blackboard entries under <i>Blackboard</i>, so each runner can
/// give them its own values — including scene nodes, which a tree resource cannot hold.
/// </para>
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/runner.svg")]
public partial class BehaviorTreeRunner : MissRunner {
    /// <summary>
    /// The tree this runner plays. Shared, not consumed: it is cloned on <see cref="MissRunner.Rebuild"/>,
    /// so the same resource can drive any number of runners without them interfering with each other.
    /// </summary>
    [Export]
    public BehaviorTree Tree {
        get => SourceAs<BehaviorTree>();
        set => SetSource(value);
    }

    public BehaviorTreeInstance Instance { get; private set; }

    protected override bool HasInstance => Instance != null;

    protected override INodeObserver Observer => Instance;

    public override void _ExitTree() {
        if (Engine.IsEditorHint()) return;
        MissbehaveDebug.Unregister(this);
    }

    protected override void BuildInstance() {
        if (Instance != null) MissbehaveDebug.Unregister(this);

        Instance = BehaviorTreeInstance.Create(Tree);

        if (Instance == null) {
            if (Tree != null) GD.PushWarning($"missbehave: {Name} has a tree without a root node.");
            return;
        }
        MissbehaveDebug.Register(this);
    }

    protected override MissStatus TickInstance(MissContext ctx) {
        Instance.BeginFrame();

        var root = Instance.Root;
        if (Status != MissStatus.Running) root.Begin(ctx);
        var status = root.Execute(ctx);
        if (status != MissStatus.Running) root.AfterRun(ctx);
        return status;
    }

    protected override void AfterTick() => MissbehaveDebug.SendFrame(this);

    protected override void InterruptInstance(MissContext ctx) => Instance.Root.Interrupt(ctx);
}
