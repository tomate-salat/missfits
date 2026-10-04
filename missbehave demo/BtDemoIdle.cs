using Godot;
using Misscore;

namespace Missbehave.Demo;

/// <summary>Spins the actor in place and never finishes, so it is obvious which branch is active.</summary>
[GlobalClass, Tool]
public partial class BtDemoIdle : ActionNode {
    [Export(PropertyHint.Range, "0,360,1,or_greater,suffix:°/s")]
    public float SpinSpeed { get; set; } = 45f;

    public override string GetSummary() => "idle, never finishes";

    protected override MissStatus Run(MissContext ctx) {
        if (ctx.Actor is Node3D actor) {
            actor.RotateY(Mathf.DegToRad(SpinSpeed) * (float) ctx.Delta);
        }
        return MissStatus.Running;
    }
}
