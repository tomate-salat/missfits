using Godot;
using Misscore;

namespace Missfits.misstate_demo;

[GlobalClass, Tool]
public partial class StateLogAction : ActionNode {
    BbParam<string> LogMessage { get; set; }
    
    protected override MissStatus Run(MissContext ctx) {
        GD.Print(LogMessage.Value);
        
        return MissStatus.Success;
    }
}