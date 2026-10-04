using Godot;
using Misscore;

namespace Missbehave;

/// <summary>Always reports Failure once the child finishes, whatever the child returned.</summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/failer.svg")]
public partial class FailerNode : ADecoratorNode {
    protected override MissStatus Tick(MissContext ctx) {
        var status = TickChild(ctx);
        return status == MissStatus.Running ? MissStatus.Running : MissStatus.Failure;
    }
}
