using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// A sequence that remembers its progress across failures. Children that already succeeded are not
/// run again; a failing child fails the node but stays the resume point, so the next tick retries
/// exactly there. Progress resets once the whole sequence succeeds, or on Interrupt.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/sequence_star.svg")]
public partial class SequenceStarNode : ACompositeNode {
    int _resumeIndex;

    protected override MissStatus Tick(MissContext ctx) {
        for (var i = _resumeIndex; i < Children.Count; i++) {
            var status = TickChild(i, ctx);

            if (status == MissStatus.Running) {
                RunningChild = i;
                _resumeIndex = i;
                return MissStatus.Running;
            }

            RunningChild = -1;
            if (status == MissStatus.Failure) {
                _resumeIndex = i;
                return MissStatus.Failure;
            }

            _resumeIndex = i + 1;
        }

        _resumeIndex = 0;
        return MissStatus.Success;
    }

    public override void Interrupt(MissContext ctx) {
        _resumeIndex = 0;
        base.Interrupt(ctx);
    }

    protected override void OnCloned() {
        base.OnCloned();
        _resumeIndex = 0;
    }
}
