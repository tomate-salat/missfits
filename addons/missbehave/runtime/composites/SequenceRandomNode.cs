using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// A sequence that runs all of its children in a shuffled order. Still fails at the first failure;
/// only the order in which the children are visited is randomised.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/sequence_random.svg")]
public partial class SequenceRandomNode : ARandomizedCompositeNode {
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
            if (status == MissStatus.Failure) {
                Slot = 0;
                return MissStatus.Failure;
            }
        }

        Slot = 0;
        return MissStatus.Success;
    }
}
