using Godot;
using Misscore;

namespace Missbehave;

/// <summary>Writes a value — fixed, or read from another entry — into a blackboard entry and succeeds.</summary>
[GlobalClass, Tool, Icon("res://addons/misscore/icons/blackboard.svg")]
public partial class BlackboardSetNode : ActionNode {
    [BbEntryOnly]
    public BbParam<Variant> Target { get; set; }

    public BbParam<Variant> Value { get; set; }

    public override string GetSummary() => Target.IsLinked ? $"{Target} = {Value}" : "";

    protected override BehaviorStatus Run(BtContext ctx) {
        if (!Target.IsLinked || ctx.Blackboard == null) return BehaviorStatus.Failure;
        Target.Set(ctx, Value.Get(ctx));
        return BehaviorStatus.Success;
    }
}
