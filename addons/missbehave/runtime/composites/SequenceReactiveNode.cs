using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Like <see cref="SequenceNode"/>, but re-checks every earlier child on each tick instead of
/// resuming. Use it when the preconditions in front of a long-running action must keep holding.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/sequence_reactive.svg")]
public partial class SequenceReactiveNode : ACompositeNode {
    protected override MissStatus Tick(MissContext ctx) {
        for (var i = 0; i < Children.Count; i++) {
            var status = TickChild(i, ctx);

            if (status == MissStatus.Running) {
                InterruptStaleRunning(ctx, i);
                RunningChild = i;
                return MissStatus.Running;
            }
            if (status == MissStatus.Failure) {
                InterruptStaleRunning(ctx, i);
                return MissStatus.Failure;
            }
        }

        InterruptStaleRunning(ctx, Children.Count - 1);
        return MissStatus.Success;
    }
}
