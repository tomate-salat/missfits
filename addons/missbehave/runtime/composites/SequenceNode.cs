using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Runs children front to back and fails at the first failure. Succeeds only when all children
/// succeed. A child that returns Running is resumed on the next tick, without re-checking the
/// children before it.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/sequence.svg")]
public partial class SequenceNode : ACompositeNode {
    protected override MissStatus Tick(MissContext ctx) {
        var start = RunningChild < 0 ? 0 : RunningChild;

        for (var i = start; i < Children.Count; i++) {
            var status = TickChild(i, ctx);

            if (status == MissStatus.Running) {
                RunningChild = i;
                return MissStatus.Running;
            }
            if (status == MissStatus.Failure) {
                RunningChild = -1;
                return MissStatus.Failure;
            }
        }

        RunningChild = -1;
        return MissStatus.Success;
    }
}
