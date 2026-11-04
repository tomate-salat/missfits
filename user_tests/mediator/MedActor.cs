using Godot;

namespace Missfits.user_tests.mediator;

[GlobalClass]
public partial class MedActor : Node  {
    MedRes _res;

    [Signal] public delegate void ResChangedEventHandler();

    [Export]
    public MedRes Res {
        get => _res;
        set {
            if (_res == value) return;
            
            _res = value;
            
            EmitSignalResChanged();
        }
    }
}