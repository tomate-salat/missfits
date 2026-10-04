using Godot;

namespace Misscore;

/// <summary>
/// Removes a blackboard entry's value, so that <see cref="BlackboardHasNode"/> fails and parameters
/// linked to it fall back to their fixed value. Succeeds whether or not there was a value.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/misscore/icons/blackboard.svg")]
public partial class BlackboardEraseNode : ActionNode {
    [BbEntryOnly]
    public BbParam<Variant> Entry { get; set; }

    public override string GetSummary() => Entry.IsLinked ? $"erase {Entry}" : "";

    protected override MissStatus Run(MissContext ctx) {
        if (!Entry.IsLinked || ctx.Blackboard == null) return MissStatus.Failure;
        ctx.Blackboard.EraseById(Entry.EntryId);
        return MissStatus.Success;
    }
}
