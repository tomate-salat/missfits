using Godot;
using Misscore;

namespace Missbehave.Tests;

/// <summary>Stand-in for a real game asset referenced by a leaf, e.g. weapon stats.</summary>
[GlobalClass, Tool]
public partial class BtProbeAsset : MissResource {
    [Export]
    public int Value { get; set; }
}
