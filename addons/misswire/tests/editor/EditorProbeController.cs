using System.Collections.Generic;
using Godot;

namespace Misswire.Tests;

/// <summary>A target that acts in the editor and counts what is done to it.</summary>
[Tool]
public partial class EditorProbeController : Node {
    EditorProbeOutfit _outfit;

    [Export]
    public EditorProbeOutfit Outfit {
        get => _outfit;
        set {
            _outfit = value;
            Sets++;
        }
    }

    public int Sets { get; set; }
    public int Applies { get; set; }
    public int Notes { get; set; }
    public Variant LastNote { get; set; }
    public readonly Dictionary<string, int> Marks = [];

    public void ApplyOutfit() => Applies++;

    public string Greeting { get; set; } = "";

    /// <summary>One value and a C# default, which Godot does not know but misswire reads.</summary>
    public void Greet(string name, int times = 1) => Greeting = $"{name} x{times}";

    /// <summary>Two values needed: a mediator does not offer it.</summary>
    public void Pair(string first, string second) => Greeting = first + second;

    /// <summary>A method with one typed parameter.</summary>
    public void Welcome(string name) => Greeting = name;

    public void Note(Variant value) {
        Notes++;
        LastNote = value;
    }

    /// <summary>Called with a fixed tag per binding, so each binding can be counted on its own.</summary>
    public void Mark(string tag) => Marks[tag] = MarkCount(tag) + 1;

    public int MarkCount(string tag) => Marks.GetValueOrDefault(tag);

    public void Reset() {
        Sets = Applies = Notes = 0;
        Marks.Clear();
    }
}
