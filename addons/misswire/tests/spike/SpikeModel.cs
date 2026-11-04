using Godot;

namespace Misswire.Tests;

/// <summary>A game node as the addon expects it: no <c>[Tool]</c>, so in the editor it is a placeholder.</summary>
public partial class SpikeModel : Node {
    [Signal] public delegate void OutfitChangedEventHandler();

    [Export] public SpikeOutfit Outfit { get; set; }
    [Export] public int Health { get; set; } = 100;
    [Export] public Node Partner { get; set; }
    [Export] public SpikeNoToolResource Extra { get; set; }

    public void Heal(int amount) => Health += amount;
}
