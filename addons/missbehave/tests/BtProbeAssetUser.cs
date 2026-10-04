using Godot;
using Misscore;

namespace Missbehave.Tests;

/// <summary>Leaf holding a reference to a shared asset, to prove cloning does not deep-copy it.</summary>
[GlobalClass, Tool]
public partial class BtProbeAssetUser : ActionNode {
    [Export]
    public BtProbeAsset Asset { get; set; }

    protected override MissStatus Run(MissContext ctx) => MissStatus.Success;
}
