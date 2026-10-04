using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Gives the child at most <see cref="MaxTicks"/> consecutive Running ticks before cutting it off
/// with a Failure. The counter resets whenever the child finishes on its own, and on Interrupt.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/limiter.svg")]
public partial class LimiterNode : ADecoratorNode {
    [Export(PropertyHint.Range, "1,600,1,or_greater")]
    public int MaxTicks { get; set; } = 1;

    int _count;

    public override string GetSummary() => $"max {MaxTicks} ticks";

    public override void BeforeRun(MissContext ctx) => _count = 0;

    protected override MissStatus Tick(MissContext ctx) {
        if (_count >= MaxTicks) {
            Interrupt(ctx);
            return MissStatus.Failure;
        }

        _count++;
        var status = TickChild(ctx);
        if (status != MissStatus.Running) _count = 0;
        return status;
    }

    public override void Interrupt(MissContext ctx) {
        _count = 0;
        base.Interrupt(ctx);
    }

    protected override void OnCloned() {
        base.OnCloned();
        _count = 0;
    }
}
