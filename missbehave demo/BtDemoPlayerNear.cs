using Godot;
using Misscore;

namespace Missbehave.Demo;

/// <summary>Succeeds while the actor is within <see cref="Range"/> of the node in group "player".</summary>
[GlobalClass, Tool]
public partial class BtDemoPlayerNear : ConditionNode {
    [Export(PropertyHint.Range, "0,50,0.5,or_greater,suffix:m")]
    public float Range { get; set; } = 8f;

    public override string GetSummary() => $"player within {Range:0.#}m";

    protected override bool Check(MissContext ctx) {
        if (ctx.Actor is not Node3D actor) return false;
        var player = BtDemoWorld.Player(actor);
        if (player == null) return false;

        return actor.GlobalPosition.DistanceTo(player.GlobalPosition) <= Range;
    }
}
