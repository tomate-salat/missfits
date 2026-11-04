using Godot;

namespace Misswire.Tests;

/// <summary>A game node without <c>[Tool]</c>: a placeholder in the editor.</summary>
public partial class EditorProbeModel : Node {
    [Signal] public delegate void OutfitChangedEventHandler();
    [Signal] public delegate void HitEventHandler(int amount);

    EditorProbeOutfit _outfit;

    [Export]
    public EditorProbeOutfit Outfit {
        get => _outfit;
        set {
            if (_outfit == value) return;
            _outfit = value;
            EmitSignal(SignalName.OutfitChanged);
        }
    }

    [Export] public int Health { get; set; } = 100;
    [Export] public EditorProbeNoToolResource Extra { get; set; }
}
