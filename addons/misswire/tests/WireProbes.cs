using System.Collections.Generic;
using Godot;

namespace Misswire.Tests;

/// <summary>A source as a game would have it: a value with a change signal, and an event with arguments.</summary>
public partial class WireProbeModel : Node {
    [Signal] public delegate void OutfitChangedEventHandler();
    [Signal] public delegate void HitEventHandler(int amount, string by);
    [Signal] public delegate void CrowdedEventHandler(int a, int b, int c, int d, int e, int f, int g);
    [Signal] public delegate void CapeSwappedEventHandler();

    WireProbeOutfit _cape;

    /// <summary>Says when it gets another resource, but not by the name misswire looks for.</summary>
    [Export]
    public WireProbeOutfit Cape {
        get => _cape;
        set {
            _cape = value;
            EmitSignal(SignalName.CapeSwapped);
        }
    }

    /// <summary>Says nothing when it gets another resource.</summary>
    [Export] public WireProbeOutfit Hat { get; set; }

    WireProbeOutfit _outfit;

    /// <summary>Returns early for the same object, as game code likes to; inner changes need <c>changed</c>.</summary>
    [Export]
    public WireProbeOutfit Outfit {
        get => _outfit;
        set {
            if (_outfit == value) return;
            _outfit = value;
            EmitSignal(SignalName.OutfitChanged);
        }
    }
}

public partial class WireProbeOutfit : Resource {
    [Export] public string Hair { get; set; } = "";

    public void Change(string hair) {
        Hair = hair;
        EmitChanged();
    }
}

/// <summary>A target that writes down everything done to it.</summary>
public partial class WireProbeController : Node {
    public readonly List<string> Log = [];

    WireProbeOutfit _outfit;

    [Export]
    public WireProbeOutfit Outfit {
        get => _outfit;
        set {
            _outfit = value;
            Log.Add($"set {value?.Hair}");
        }
    }

    public void ApplyOutfit() => Log.Add($"apply {Outfit?.Hair}");
    public void Damage(int amount, string by) => Log.Add($"damage {amount} by {by}");
    public void Note(Variant first, Variant second) => Log.Add($"note {first} {second}");
    public void Seen(Variant value) => Log.Add($"seen {value}");
    public void Greet(string name, int times = 1) => Log.Add($"greet {name} x{times}");
}
