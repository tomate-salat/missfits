using Godot;
using Misscore;

namespace Missbehave.Tests;

/// <summary>Test leaf that stops its own runner mid-tick and remembers which runner it saw.</summary>
[GlobalClass, Tool]
public partial class BtProbeStopAction : ActionNode {
    public BehaviorTreeRunner SeenRunner { get; private set; }

    protected override MissStatus Run(MissContext ctx) {
        SeenRunner = ctx.GetRunner<BehaviorTreeRunner>();
        ctx.Runner?.Stop();
        return MissStatus.Success;
    }
}
