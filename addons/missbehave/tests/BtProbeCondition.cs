using Godot;
using Misscore;

namespace Missbehave.Tests;

/// <summary>Condition with a fixed answer, used by the self tests. Counts how often it was checked.</summary>
[GlobalClass, Tool]
public partial class BtProbeCondition : ConditionNode {
    [Export]
    public bool Holds { get; set; } = true;

    public int Checks;

    protected override bool Check(MissContext ctx) {
        Checks++;
        return Holds;
    }

    protected override void OnCloned() => Checks = 0;
}
