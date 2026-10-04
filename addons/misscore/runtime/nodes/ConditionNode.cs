using Godot;

namespace Misscore;

/// <summary>
/// Base class for leaves that only inspect the world. Override <see cref="Check"/> and return
/// whether the condition holds; a condition never returns Running.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/misscore/icons/condition.svg")]
public abstract partial class ConditionNode : ALeafNode {
    /// <summary>Inverts the result, so a condition can be reused without an extra decorator.</summary>
    [Export]
    public bool Negate { get; set; }

    public override string PickerGroup => NodeGroup.Condition;

    protected abstract bool Check(MissContext ctx);

    protected override MissStatus Tick(MissContext ctx) {
        var result = Check(ctx);
        if (Negate) result = !result;
        return result ? MissStatus.Success : MissStatus.Failure;
    }

    public override string GetSummary() => Negate ? "negated" : "";
}
