using Godot;

namespace Missfits.user_tests.mediator;

[GlobalClass, Tool]
public partial class MedRes : Resource {
    string _text;

    [Signal] public delegate void TextChangedEventHandler();

    [Export]
    public string Text {
        get => _text;
        set {
            if (_text == value) return;
            
            _text = value;
            
            EmitSignalTextChanged();
        }
    }
}