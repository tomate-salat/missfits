using Godot;
using Misscore;

namespace Missbehave;

/// <summary>Swaps Success and Failure. Running passes through unchanged.</summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/inverter.svg")]
public partial class InverterNode : ADecoratorNode {
    protected override MissStatus Tick(MissContext ctx) => TickChild(ctx) switch {
        MissStatus.Success => MissStatus.Failure,
        MissStatus.Failure => MissStatus.Success,
        _ => MissStatus.Running,
    };
}
