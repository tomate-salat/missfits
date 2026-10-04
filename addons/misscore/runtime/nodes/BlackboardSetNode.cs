using Godot;

namespace Misscore;

/// <summary>Writes a value — fixed, or read from another entry — into a blackboard entry and succeeds.</summary>
[GlobalClass, Tool, Icon("res://addons/misscore/icons/blackboard.svg")]
public partial class BlackboardSetNode : ActionNode {
    [BbEntryOnly]
    public BbParam<Variant> Target { get; set; }

    public BbParam<Variant> Value { get; set; }

    public override string GetSummary() => Target.IsLinked ? $"{Target} = {Value}" : "";

    protected override MissStatus Run(MissContext ctx) {
        if (!Target.IsLinked || ctx.Blackboard == null) return MissStatus.Failure;
        Target.Set(ctx, Value.Get(ctx));
        return MissStatus.Success;
    }
}
