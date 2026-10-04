using Godot;
using Misscore;

namespace Missbehave.Tests;

/// <summary>Action filed in a nested sub-group under a name of its own, for the editor self test.</summary>
[GlobalClass, Tool]
[NodeGroup("Probes/Nested")]
[NodeName("Grouped probe")]
public partial class BtProbeGroupedAction : ActionNode {
    protected override MissStatus Run(MissContext ctx) => MissStatus.Success;
}
