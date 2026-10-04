using Godot;
using Misscore;

namespace Missbehave.Demo;

/// <summary>
/// Walks the actor toward the player, reporting Running until it is close enough. A long-running
/// action like this is what makes instance isolation visible: two enemies sharing one tree resource
/// must be able to be at different points of the same chase.
/// </summary>
[GlobalClass, Tool]
public partial class BtDemoChase : ActionNode {
    [Export(PropertyHint.Range, "0.1,20,0.1,or_greater")]
    public float Speed { get; set; } = 4f;

    [Export(PropertyHint.Range, "0,20,0.1,or_greater,suffix:m")]
    public float StopDistance { get; set; } = 1.5f;

    public override string GetSummary() => $"{Speed:0.#} m/s, stop at {StopDistance:0.#}m";

    protected override MissStatus Run(MissContext ctx) {
        if (ctx.Actor is not Node3D actor) return MissStatus.Failure;
        var player = BtDemoWorld.Player(actor);
        if (player == null) return MissStatus.Failure;

        var toPlayer = player.GlobalPosition - actor.GlobalPosition;
        if (toPlayer.Length() <= StopDistance) return MissStatus.Success;

        actor.GlobalPosition += toPlayer.Normalized() * Speed * (float) ctx.Delta;
        return MissStatus.Running;
    }
}
