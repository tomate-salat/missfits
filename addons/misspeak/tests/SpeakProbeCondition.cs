using Godot;
using Misscore;

namespace Misspeak.Tests;

/// <summary>Condition used by the self test: holds once <see cref="Value"/> has reached <see cref="AtLeast"/>.</summary>
[GlobalClass, Tool]
public partial class SpeakProbeCondition : ConditionNode {
    public BbParam<int> Value { get; set; } = 0;

    [Export]
    public int AtLeast { get; set; }

    protected override bool Check(MissContext ctx) => Value.Value >= AtLeast;
}
