using Godot;

namespace Misswire.Tests;

/// <summary>Like the outfit in Gladiator: <c>[Tool]</c>, and says when its inside changes.</summary>
[Tool]
public partial class EditorProbeOutfit : Resource {
    string _hair = "";

    [Export]
    public string Hair {
        get => _hair;
        set {
            _hair = value;
            EmitChanged();
        }
    }

    /// <summary>What a step last handed the outfit; a method to call on a resource.</summary>
    public Variant LastTag { get; set; }

    public void Tag(Variant value) => LastTag = value;
}
