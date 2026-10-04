using Godot;

namespace Misscore;

/// <summary>
/// Base class for leaves that change the world. Subclass it, add <c>[Export]</c> parameters and
/// override <see cref="Run"/>:
/// <code>
/// [GlobalClass, Tool]
/// public partial class FollowTarget : ActionNode {
///     [Export] public float StopDistance { get; set; } = 1f;
///     protected override MissStatus Run(MissContext ctx) { ... }
/// }
/// </code>
/// Remember both attributes: without <c>[GlobalClass]</c> the node cannot be saved into a resource,
/// and without <c>[Tool]</c> the editor cannot work with it once the resource is loaded again.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/misscore/icons/action.svg")]
public abstract partial class ActionNode : ALeafNode {
    public override string PickerGroup => NodeGroup.Action;

    protected abstract MissStatus Run(MissContext ctx);

    protected override MissStatus Tick(MissContext ctx) => Run(ctx);
}
