#if TOOLS
using Godot;
using Misscore.Editor;

namespace Misstate.Editor;

/// <summary>
/// One line inside a state's box: an action the state runs, a transition out of it, or — indented
/// below its transition — a condition that transition checks. A transition row's slot carries the
/// output port on the right, so every transition has a wire of its own. What a row looks like and
/// how it reports clicks is <see cref="GraphRow"/>'s; what they mean is left to <see cref="FsmGraphEdit"/>.
/// </summary>
[Tool]
public partial class FsmRow : GraphRow {
    /// <summary><see cref="GraphRow.Kind"/> of a row showing one of the state's actions.</summary>
    public const string Action = "action";

    /// <summary><see cref="GraphRow.Kind"/> of a row showing one of the state's transitions.</summary>
    public const string Transition = "transition";

    /// <summary><see cref="GraphRow.Kind"/> of a row showing one condition of the transition above it.</summary>
    public const string Condition = "condition";

    /// <summary>How far a condition is set in from its transition.</summary>
    public const int ConditionIndent = 18;
}
#endif
