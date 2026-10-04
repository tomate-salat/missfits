using Godot;

namespace Misscore;

/// <summary>
/// Base class for leaves that change the world. Subclass it, add <c>[Export]</c> parameters and
/// override <see cref="Run"/>:
/// <code>
/// [GlobalClass]
/// public partial class FollowTarget : ActionNode {
///     [Export] public float StopDistance { get; set; } = 1f;
///     protected override MissStatus Run(MissContext ctx) { ... }
/// }
/// </code>
/// Remember <c>[GlobalClass]</c> — without it the node cannot be saved into a tree resource.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/misscore/icons/action.svg")]
public abstract partial class ActionNode : ALeafNode {
    protected abstract MissStatus Run(MissContext ctx);

    protected override MissStatus Tick(MissContext ctx) => Run(ctx);
}
