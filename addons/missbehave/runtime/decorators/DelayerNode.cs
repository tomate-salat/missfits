using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Reports Running for <see cref="WaitTime"/> seconds before letting the child run at all. The
/// timer resets once the child finishes, and on Interrupt.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/delayer.svg")]
public partial class DelayerNode : ADecoratorNode {
    [Export(PropertyHint.Range, "0,60,0.05,or_greater,suffix:s")]
    public double WaitTime { get; set; }

    double _elapsed;

    public override string GetSummary() => $"after {WaitTime:0.##}s";

    protected override MissStatus Tick(MissContext ctx) {
        if (_elapsed < WaitTime) {
            _elapsed += ctx.Delta;
            return MissStatus.Running;
        }

        var status = TickChild(ctx);
        if (status != MissStatus.Running) _elapsed = 0;
        return status;
    }

    public override void Interrupt(MissContext ctx) {
        _elapsed = 0;
        base.Interrupt(ctx);
    }

    protected override void OnCloned() {
        base.OnCloned();
        _elapsed = 0;
    }
}
