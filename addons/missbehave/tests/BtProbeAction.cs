using Godot;
using Misscore;

namespace Missbehave.Tests;

/// <summary>Scriptable leaf used by the self test. Counts how often each hook was called.</summary>
[GlobalClass, Tool]
public partial class BtProbeAction : ActionNode {
    /// <summary>Status returned once <see cref="RunningTicks"/> ticks have gone by.</summary>
    [Export]
    public MissStatus Result { get; set; } = MissStatus.Success;

    /// <summary>Number of leading ticks that return Running.</summary>
    [Export]
    public int RunningTicks { get; set; }

    public int Ticks;
    public int BeforeRuns;
    public int AfterRuns;
    public int Interrupts;

    protected override MissStatus Run(MissContext ctx) {
        Ticks++;
        return Ticks <= RunningTicks ? MissStatus.Running : Result;
    }

    public override void BeforeRun(MissContext ctx) {
        BeforeRuns++;
        Ticks = 0;
    }

    public override void AfterRun(MissContext ctx) => AfterRuns++;

    public override void Interrupt(MissContext ctx) {
        Interrupts++;
        Ticks = 0;
    }

    protected override void OnCloned() {
        Ticks = 0;
        BeforeRuns = 0;
        AfterRuns = 0;
        Interrupts = 0;
    }
}
