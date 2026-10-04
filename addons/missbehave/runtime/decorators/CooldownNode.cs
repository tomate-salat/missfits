using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// After the child finishes, reports Failure for <see cref="WaitTime"/> seconds before letting it
/// run again. The cooldown also ticks down while the branch is not being visited, so it measures
/// wall time within the runner rather than time spent in this branch.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/cooldown.svg")]
public partial class CooldownNode : ADecoratorNode {
    [Export(PropertyHint.Range, "0,60,0.05,or_greater,suffix:s")]
    public double WaitTime { get; set; }

    double _remaining;

    public override string GetSummary() => $"every {WaitTime:0.##}s";

    protected override MissStatus Tick(MissContext ctx) {
        if (_remaining > 0) {
            _remaining -= ctx.Delta;
            return MissStatus.Failure;
        }

        var status = TickChild(ctx);
        if (status != MissStatus.Running) _remaining = WaitTime;
        return status;
    }

    public override void Interrupt(MissContext ctx) {
        _remaining = 0;
        base.Interrupt(ctx);
    }

    protected override void OnCloned() {
        base.OnCloned();
        _remaining = 0;
    }
}
