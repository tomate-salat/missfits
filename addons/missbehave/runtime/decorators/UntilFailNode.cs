using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Keeps the child going: reports Running while the child succeeds or runs, and Success once the
/// child finally fails. Use it to loop a branch until something breaks it.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/until_fail.svg")]
public partial class UntilFailNode : ADecoratorNode {
    protected override MissStatus Tick(MissContext ctx) => TickChild(ctx) switch {
        MissStatus.Failure => MissStatus.Success,
        _ => MissStatus.Running,
    };
}
