using Godot;
using Misscore;

namespace Misstate.Tests;

/// <summary>Scriptable action used by the self test. Counts how often each hook was called.</summary>
[GlobalClass, Tool]
public partial class FsmProbeAction : ActionNode {
    /// <summary>Status returned once <see cref="RunningTicks"/> ticks have gone by.</summary>
    [Export]
    public MissStatus Result { get; set; } = MissStatus.Success;

    /// <summary>Number of leading ticks that return Running.</summary>
    [Export]
    public int RunningTicks { get; set; }

    /// <summary>Counted up by one on every tick, through the blackboard when linked.</summary>
    public BbParam<int> Counter { get; set; } = 0;

    /// <summary>What to do with the runner once this many ticks have gone by; 0 for never.</summary>
    [Export]
    public int StopRunnerAtTick { get; set; }

    /// <summary>A state to send the machine to on the first tick, by name; empty for none.</summary>
    [Export]
    public string GoToOnFirstTick { get; set; } = "";

    public int Ticks;
    public int TotalTicks;
    public int BeforeRuns;
    public int AfterRuns;
    public int Interrupts;

    protected override MissStatus Run(MissContext ctx) {
        Ticks++;
        TotalTicks++;
        Counter.Value += 1;
        if (StopRunnerAtTick > 0 && TotalTicks == StopRunnerAtTick) ctx.Runner?.Stop();
        if (GoToOnFirstTick != "" && Ticks == 1) ctx.GetRunner<FsmRunner>()?.GoTo(GoToOnFirstTick);
        return Ticks <= RunningTicks ? MissStatus.Running : Result;
    }

    public override void BeforeRun(MissContext ctx) {
        BeforeRuns++;
        Ticks = 0;
    }

    public override void AfterRun(MissContext ctx) => AfterRuns++;

    public override void Interrupt(MissContext ctx) => Interrupts++;

    protected override void OnCloned() {
        Ticks = 0;
        TotalTicks = 0;
        BeforeRuns = 0;
        AfterRuns = 0;
        Interrupts = 0;
    }
}