using Godot;
using Misscore;

namespace Missbehave;

/// <summary>Succeeds while a blackboard entry holds a value, i.e. it has not been erased.</summary>
[GlobalClass, Tool, Icon("res://addons/misscore/icons/blackboard.svg")]
public partial class BlackboardHasNode : ConditionNode {
    [BbEntryOnly]
    public BbParam<Variant> Entry { get; set; }

    public override string GetSummary() => Entry.IsLinked ? $"has {Entry}" : "";

    protected override bool Check(BtContext ctx)
        => Entry.IsLinked && ctx.Blackboard != null && ctx.Blackboard.HasId(Entry.EntryId);
}
