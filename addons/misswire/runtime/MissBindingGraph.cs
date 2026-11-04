using Godot;
using Misscore;

namespace Misswire;

/// <summary>
/// Everything one <see cref="MissMediator"/> wires up. It is meant to live inside the scene, embedded
/// in the mediator: its node paths are relative to that mediator and mean nothing elsewhere.
/// </summary>
[GlobalClass, Tool]
public partial class MissBindingGraph : MissResource {
    [Export]
    public Godot.Collections.Array<MissBinding> Bindings { get; set; } = [];

    /// <summary>The boxes of the graph editor that are not bindings. Only the editor reads this.</summary>
    [Export]
    public Godot.Collections.Array<MissGraphBoxInfo> Boxes { get; set; } = [];

    /// <summary>The fixed values steps can take through their value port.</summary>
    [Export]
    public Godot.Collections.Array<MissGraphConstant> Constants { get; set; } = [];

    /// <summary>The frames the graph editor groups boxes with. Only the editor reads this.</summary>
    [Export]
    public Godot.Collections.Array<MissGraphGroupInfo> Groups { get; set; } = [];
}
