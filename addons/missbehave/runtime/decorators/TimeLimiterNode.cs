using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Gives the child <see cref="WaitTime"/> seconds to finish before interrupting it and reporting
/// Failure. Time is accumulated from the tick delta, so it behaves the same on either thread and
/// can be driven deterministically in tests.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/time_limiter.svg")]
public partial class TimeLimiterNode : ADecoratorNode {
    [Export(PropertyHint.Range, "0,60,0.05,or_greater,suffix:s")]
    public double WaitTime { get; set; }

    double _elapsed;

    public override string GetSummary() => $"within {WaitTime:0.##}s";

    public override void BeforeRun(MissContext ctx) => _elapsed = 0;

    protected override MissStatus Tick(MissContext ctx) {
        if (_elapsed >= WaitTime) {
            Interrupt(ctx);
            return MissStatus.Failure;
        }

        _elapsed += ctx.Delta;
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
