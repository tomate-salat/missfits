using Godot;

namespace Misswire.Tests;

/// <summary>A source that runs in the editor: <see cref="Value"/> says when it changes, <see cref="Other"/> does not.</summary>
[Tool]
public partial class EditorProbeToolModel : Node {
    [Signal] public delegate void ValueChangedEventHandler();

    int _value;

    [Export]
    public int Value {
        get => _value;
        set {
            _value = value;
            EmitSignal(SignalName.ValueChanged);
        }
    }

    [Export] public int Other { get; set; }
}
