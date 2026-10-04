using Godot;
using Misscore;

namespace Missfits.misstate_demo;

[GlobalClass, Tool]
public partial class StateClickCondition : ConditionNode {
    
    
    protected override bool Check(MissContext ctx) {
        return Input.IsActionJustPressed("ui_accept");
    }
    
}