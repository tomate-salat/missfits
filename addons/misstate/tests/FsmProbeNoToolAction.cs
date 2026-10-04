using Godot;
using Misscore;

namespace Misstate.Tests;

/// <summary>
/// An action as someone would write it who forgot <c>[Tool]</c>. The editor must neither offer it
/// nor open a machine that uses it.
/// </summary>
[GlobalClass]
public partial class FsmProbeNoToolAction : ActionNode {
    protected override MissStatus Run(MissContext ctx) => MissStatus.Success;
}