using System;
using Godot;

namespace Misscore;

/// <summary>
/// One value an <see cref="IBlackboardSource"/> declares on its blackboard: a type, a default and a
/// name to show.
/// <para>
/// Everything refers to an entry by <see cref="Id"/>, never by <see cref="Name"/>, so an entry can be
/// renamed at any time without breaking a single link. The name only matters to people, and to code
/// outside that looks a value up by name.
/// </para>
/// </summary>
[GlobalClass, Tool]
public partial class BlackboardEntry : Resource {
    [Export]
    public string Id { get; set; } = NewId();

    public static string NewId() => Guid.NewGuid().ToString("N");

    [Export]
    public string Name { get; set; } = "";

    /// <summary>Nil means the entry accepts any value.</summary>
    [Export]
    public Variant.Type VariantType { get; set; } = Variant.Type.Nil;

    /// <summary>
    /// For object entries, the class the value has to be — e.g. <c>Node3D</c> or a script class. A
    /// script path is turned into its class name: the editor's class picker hands out paths for script
    /// classes, and nothing else here understands them.
    /// </summary>
    [Export]
    public string ClassName {
        get => _className;
        set => _className = BbTypes.ClassNameOf(value);
    }

    string _className = "";

    /// <summary>
    /// The value every instance starts with, unless it is overridden there. Scene nodes cannot live in
    /// a resource, so node entries have no default and are always filled in per instance.
    /// </summary>
    [Export]
    public Variant Default { get; set; }

    public bool IsNode => VariantType == Variant.Type.Object && BbTypes.IsNodeClass(ClassName);

    public string TypeLabel => BbTypes.Label(VariantType, ClassName);

    public override string ToString() => $"{Name} ({TypeLabel})";
}
