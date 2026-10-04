using Godot;
using Misscore;

namespace Missbehave.Tests;

/// <summary>
/// Test leaf with blackboard parameters: counts <see cref="Speed"/> up by one per tick, through
/// <see cref="BbParam{T}.Value"/>, and keeps running for <see cref="RunTicks"/> ticks.
/// </summary>
[GlobalClass, Tool]
public partial class BtProbeParamAction : ActionNode {
    public BbParam<float> Speed { get; set; } = 4f;

    /// <summary>Never touched by the tests, so it has to stay out of saved files.</summary>
    BbParam<float> Untouched { get; set; } = 1f;

    public BbParam<Node3D> Target { get; set; }

    /// <summary>Get-only and without initializer: the node has to create it through the backing field.</summary>
    public BbParam<int> Hits { get; }

    [Export]
    public int RunTicks { get; set; }

    public float SpeedAtBeforeRun { get; private set; }
    public float LastSpeed { get; private set; }
    public Node3D LastTarget { get; private set; }

    int _ticks;

    public override void BeforeRun(BtContext ctx) {
        SpeedAtBeforeRun = Speed.Value;
        _ticks = 0;
    }

    protected override BehaviorStatus Run(BtContext ctx) {
        LastSpeed = Speed.Value;
        LastTarget = Target.Value;
        Speed.Value = LastSpeed + 1;
        return ++_ticks < RunTicks ? BehaviorStatus.Running : BehaviorStatus.Success;
    }
}
