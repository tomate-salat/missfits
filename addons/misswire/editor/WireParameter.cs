#if TOOLS
using Godot;

namespace Misswire.Editor;

/// <summary>
/// A parameter of what a step does: its name and type, and the value it has by default if the
/// method declares one.
/// </summary>
public readonly record struct WireParameter(string Name, Variant.Type Type, string ClassName, Variant? Default) {
    public string TypeName => WireMembers.TypeName(Type, ClassName);

    /// <summary>Whether a fixed value of this type can be typed into the graph; anything else is set in the Inspector.</summary>
    public bool Typable => Type is Variant.Type.Nil or Variant.Type.Bool or Variant.Type.Int or Variant.Type.Float
        or Variant.Type.String or Variant.Type.StringName or Variant.Type.NodePath;

    /// <summary>The value a fixed argument starts with: the method's default, or the type's empty value.</summary>
    public Variant StartValue => Default ?? Type switch {
        Variant.Type.Bool => false,
        Variant.Type.Int => 0,
        Variant.Type.Float => 0.0,
        Variant.Type.String => "",
        Variant.Type.StringName => new StringName(),
        Variant.Type.NodePath => new NodePath(),
        Variant.Type.Vector2 => Vector2.Zero,
        Variant.Type.Vector3 => Vector3.Zero,
        Variant.Type.Color => Colors.White,
        _ => default,
    };
}
#endif
