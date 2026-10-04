using Godot;
using Misscore;

namespace Misspeak;

/// <summary>
/// Holds while a dialogue is under way. Negated, it holds once the talking is over — e.g. on a state
/// machine's transition out of a "talking" state.
/// </summary>
[GlobalClass, Tool, NodeName("Dialogue active"), NodeGroup("Dialogue")]
public partial class DialogueActiveCondition : ConditionNode {
    /// <summary>
    /// The <see cref="DialogueRunner"/> to ask, from the actor. Left empty, it is the first one among
    /// the actor's children, else the first one in the scene.
    /// </summary>
    [Export]
    public NodePath Runner { get; set; } = new();

    protected override bool Check(MissContext ctx) => DialogueRunner.Find(ctx.Actor, Runner)?.IsActive == true;
}
