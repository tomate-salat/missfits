using Godot;
using Misscore;

namespace Misspeak.Tests;

/// <summary>Scriptable action used by the self test. Counts how often each hook was called.</summary>
[GlobalClass, Tool]
public partial class SpeakProbeAction : ActionNode {
    /// <summary>Status returned once <see cref="RunningTicks"/> ticks have gone by.</summary>
    [Export]
    public MissStatus Result { get; set; } = MissStatus.Success;

    /// <summary>Number of leading ticks that return Running.</summary>
    [Export]
    public int RunningTicks { get; set; }

    /// <summary>Counted up by one on every tick, through the blackboard when linked.</summary>
    public BbParam<int> Counter { get; set; } = 0;

    /// <summary>Cancels the dialogue of the runner that ticks it, on the first tick.</summary>
    [Export]
    public bool CancelOnFirstTick { get; set; }

    public int Ticks;
    public int BeforeRuns;
    public int AfterRuns;
    public int Interrupts;

    protected override MissStatus Run(MissContext ctx) {
        Ticks++;
        Counter.Value += 1;
        if (CancelOnFirstTick && Ticks == 1) ctx.GetRunner<DialogueRunner>()?.Cancel();
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
        BeforeRuns = 0;
        AfterRuns = 0;
        Interrupts = 0;
    }
}
