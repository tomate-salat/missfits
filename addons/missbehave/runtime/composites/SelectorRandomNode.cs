using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// A selector that tries its children in a shuffled order, so an actor with several equally valid
/// options does not always pick the same one.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/selector_random.svg")]
public partial class SelectorRandomNode : ARandomizedCompositeNode {
    protected override MissStatus Tick(MissContext ctx) {
        EnsureOrder();

        for (var s = Slot; s < Order.Length; s++) {
            var i = Order[s];
            var status = TickChild(i, ctx);

            if (status == MissStatus.Running) {
                RunningChild = i;
                Slot = s;
                return MissStatus.Running;
            }
            RunningChild = -1;
            if (status == MissStatus.Success) {
                Slot = 0;
                return MissStatus.Success;
            }
        }

        Slot = 0;
        return MissStatus.Failure;
    }
}
