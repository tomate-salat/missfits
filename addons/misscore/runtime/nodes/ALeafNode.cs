using Godot;

namespace Misscore;

/// <summary>Base of every node that does actual work and has no children.</summary>
[GlobalClass, Tool]
public abstract partial class ALeafNode : MissNode {
    public override int MinChildren => 0;
    public override int MaxChildren => 0;
    public override string Category => NodeCategory.Leaf;
}
