using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Runs children front to back and succeeds at the first success. Fails only when every child
/// fails. A child that returns Running is resumed on the next tick.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/selector.svg")]
public partial class SelectorNode : ACompositeNode {
    protected override MissStatus Tick(MissContext ctx) {
        var start = RunningChild < 0 ? 0 : RunningChild;

        for (var i = start; i < Children.Count; i++) {
            var status = TickChild(i, ctx);

            if (status == MissStatus.Running) {
                RunningChild = i;
                return MissStatus.Running;
            }
            if (status == MissStatus.Success) {
                RunningChild = -1;
                return MissStatus.Success;
            }
        }

        RunningChild = -1;
        return MissStatus.Failure;
    }
}
