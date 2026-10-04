using Godot;
using Misscore;

namespace Missbehave;

/// <summary>Always reports Success once the child finishes, whatever the child returned.</summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/succeeder.svg")]
public partial class SucceederNode : ADecoratorNode {
    protected override MissStatus Tick(MissContext ctx) {
        var status = TickChild(ctx);
        return status == MissStatus.Running ? MissStatus.Running : MissStatus.Success;
    }
}
