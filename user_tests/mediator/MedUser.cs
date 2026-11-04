using Godot;

namespace Missfits.user_tests.mediator;

[GlobalClass, Tool]
public partial class MedUser : Node {
    [Export] MedRes Res { get; set; }

    public void Apply() {
        GD.Print($"Apply: {Res.Text}");
    }

    public void Greet(string name) {
        GD.Print($"Greet: {name}");
    }
}