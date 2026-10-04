using Godot;
using Misscore;

namespace Missbehave;

/// <summary>Base of nodes that wrap exactly one child and modify its result or its scheduling.</summary>
[GlobalClass, Tool]
public abstract partial class ADecoratorNode : MissNode {
    public override int MinChildren => 1;
    public override int MaxChildren => 1;
    public override string Category => NodeCategory.Decorator;

    protected bool ChildIsRunning { get; set; }

    protected MissNode Child => Children.Count > 0 ? Children[0] : null;

    protected override void OnCloned() => ChildIsRunning = false;

    /// <summary>Ticks the wrapped child, handling its BeforeRun/AfterRun bracketing.</summary>
    protected MissStatus TickChild(MissContext ctx) {
        var child = Child;
        if (child == null) return MissStatus.Failure;

        if (!ChildIsRunning) child.Begin(ctx);
        var status = child.Execute(ctx);

        if (status == MissStatus.Running) {
            ChildIsRunning = true;
        }
        else {
            ChildIsRunning = false;
            child.AfterRun(ctx);
        }
        return status;
    }

    public override void Interrupt(MissContext ctx) {
        if (ChildIsRunning) Child?.Interrupt(ctx);
        ChildIsRunning = false;
    }
}
