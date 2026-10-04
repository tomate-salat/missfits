using Godot;
using Misscore;

namespace Missbehave;

/// <summary>Compares two values, each either fixed or read from a blackboard entry.</summary>
[GlobalClass, Tool, Icon("res://addons/misscore/icons/blackboard.svg")]
public partial class BlackboardCompareNode : ConditionNode {
    public enum CompareOperator {
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
    }

    public BbParam<Variant> Left { get; set; }

    [Export]
    public CompareOperator Operator { get; set; } = CompareOperator.Equal;

    public BbParam<Variant> Right { get; set; }

    public override string GetSummary() => $"{Left} {Symbol()} {Right}";

    protected override bool Check(BtContext ctx) {
        // An erased entry compares as nothing at all rather than as its fallback value.
        if (Left.IsLinked && ctx.Blackboard?.HasId(Left.EntryId) != true) return false;
        if (Right.IsLinked && ctx.Blackboard?.HasId(Right.EntryId) != true) return false;

        var left = Left.Get(ctx);
        var right = Right.Get(ctx);

        return Operator switch {
            CompareOperator.Equal => Equal(left, right),
            CompareOperator.NotEqual => !Equal(left, right),
            CompareOperator.Less => Compare(left, right) < 0,
            CompareOperator.LessOrEqual => Compare(left, right) <= 0,
            CompareOperator.Greater => Compare(left, right) > 0,
            CompareOperator.GreaterOrEqual => Compare(left, right) >= 0,
            _ => false,
        };
    }

    /// <summary>3 and 3.0 are equal here, as they would be to anyone reading the graph.</summary>
    static bool Equal(Variant left, Variant right)
        => IsNumber(left) && IsNumber(right) ? left.AsDouble() == right.AsDouble() : BbTypes.SameValue(left, right);

    static bool IsNumber(Variant value) => value.VariantType is Variant.Type.Int or Variant.Type.Float;

    static int Compare(Variant left, Variant right) {
        if (left.VariantType == Variant.Type.String || right.VariantType == Variant.Type.String) {
            return string.CompareOrdinal(left.AsString(), right.AsString());
        }
        return left.AsDouble().CompareTo(right.AsDouble());
    }

    string Symbol() => Operator switch {
        CompareOperator.Equal => "==",
        CompareOperator.NotEqual => "!=",
        CompareOperator.Less => "<",
        CompareOperator.LessOrEqual => "<=",
        CompareOperator.Greater => ">",
        _ => ">=",
    };
}
