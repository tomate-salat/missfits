using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misscore;

/// <summary>
/// Maps C# types onto what a blackboard entry records — a <see cref="Variant.Type"/> plus, for
/// objects, a class name — and decides which entries a parameter may link to.
/// </summary>
public static class BbTypes {
    static Dictionary<string, Type> _scriptClasses;

    /// <summary>The variant type and class name that stand for <paramref name="type"/> on the blackboard.</summary>
    public static (Variant.Type VariantType, string ClassName) Describe(Type type) {
        if (type == typeof(Variant)) return (Variant.Type.Nil, "");
        if (typeof(GodotObject).IsAssignableFrom(type)) return (Variant.Type.Object, type.Name);
        if (type.IsEnum) return (Variant.Type.Int, "");

        return (Type.GetTypeCode(type), type) switch {
            (TypeCode.Boolean, _) => (Variant.Type.Bool, ""),
            (TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32
                or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64, _) => (Variant.Type.Int, ""),
            (TypeCode.Single or TypeCode.Double, _) => (Variant.Type.Float, ""),
            (TypeCode.String, _) => (Variant.Type.String, ""),
            _ => (ByType.GetValueOrDefault(Generic(type), Variant.Type.Nil), ""),
        };
    }

    static Type Generic(Type type) => type.IsGenericType ? type.GetGenericTypeDefinition() : type;

    static readonly Dictionary<Type, Variant.Type> ByType = new() {
        [typeof(StringName)] = Variant.Type.StringName,
        [typeof(NodePath)] = Variant.Type.NodePath,
        [typeof(Vector2)] = Variant.Type.Vector2,
        [typeof(Vector2I)] = Variant.Type.Vector2I,
        [typeof(Vector3)] = Variant.Type.Vector3,
        [typeof(Vector3I)] = Variant.Type.Vector3I,
        [typeof(Vector4)] = Variant.Type.Vector4,
        [typeof(Vector4I)] = Variant.Type.Vector4I,
        [typeof(Rect2)] = Variant.Type.Rect2,
        [typeof(Rect2I)] = Variant.Type.Rect2I,
        [typeof(Color)] = Variant.Type.Color,
        [typeof(Quaternion)] = Variant.Type.Quaternion,
        [typeof(Basis)] = Variant.Type.Basis,
        [typeof(Transform2D)] = Variant.Type.Transform2D,
        [typeof(Transform3D)] = Variant.Type.Transform3D,
        [typeof(Projection)] = Variant.Type.Projection,
        [typeof(Plane)] = Variant.Type.Plane,
        [typeof(Aabb)] = Variant.Type.Aabb,
        [typeof(Rid)] = Variant.Type.Rid,
        [typeof(Callable)] = Variant.Type.Callable,
        [typeof(Signal)] = Variant.Type.Signal,
        [typeof(Godot.Collections.Array)] = Variant.Type.Array,
        [typeof(Godot.Collections.Array<>)] = Variant.Type.Array,
        [typeof(Godot.Collections.Dictionary)] = Variant.Type.Dictionary,
        [typeof(Godot.Collections.Dictionary<,>)] = Variant.Type.Dictionary,
        [typeof(byte[])] = Variant.Type.PackedByteArray,
        [typeof(int[])] = Variant.Type.PackedInt32Array,
        [typeof(long[])] = Variant.Type.PackedInt64Array,
        [typeof(float[])] = Variant.Type.PackedFloat32Array,
        [typeof(double[])] = Variant.Type.PackedFloat64Array,
        [typeof(string[])] = Variant.Type.PackedStringArray,
        [typeof(Vector2[])] = Variant.Type.PackedVector2Array,
        [typeof(Vector3[])] = Variant.Type.PackedVector3Array,
        [typeof(Vector4[])] = Variant.Type.PackedVector4Array,
        [typeof(Color[])] = Variant.Type.PackedColorArray,
    };

    /// <summary>Short type name for the editor: <c>float</c>, <c>Vector3</c>, <c>EnemyNavAgent</c>, <c>Variant</c>.</summary>
    public static string Label(Variant.Type type, string className) => type switch {
        Variant.Type.Nil => "Variant",
        Variant.Type.Bool => "bool",
        Variant.Type.Int => "int",
        Variant.Type.Float => "float",
        Variant.Type.Object => string.IsNullOrEmpty(className) ? "Object" : className,
        _ => type.ToString(),
    };

    /// <summary>
    /// Whether a parameter of <paramref name="parameterType"/> can link to <paramref name="entry"/>.
    /// Either side being Variant accepts anything; objects may differ as long as one class derives from
    /// the other, since reading and writing each need one direction.
    /// </summary>
    public static bool Accepts(Type parameterType, BlackboardEntry entry) {
        if (entry == null) return false;
        var (type, className) = Describe(parameterType);
        return Compatible(type, className, entry.VariantType, entry.ClassName);
    }

    public static bool Compatible(Variant.Type a, string aClass, Variant.Type b, string bClass) {
        if (a == Variant.Type.Nil || b == Variant.Type.Nil) return true;
        if (a != b) return false;
        if (a != Variant.Type.Object) return true;
        return IsA(aClass, bClass) || IsA(bClass, aClass);
    }

    /// <summary>True when <paramref name="className"/> is <paramref name="baseName"/> or derives from it.</summary>
    public static bool IsA(string className, string baseName) {
        if (string.IsNullOrEmpty(baseName) || baseName == "Object" || className == baseName) return true;
        if (string.IsNullOrEmpty(className)) return false;

        var type = ResolveClass(className);
        var baseType = ResolveClass(baseName);
        if (type != null && baseType != null) return baseType.IsAssignableFrom(type);

        return ClassDB.ClassExists(className) && ClassDB.IsParentClass(className, baseName);
    }

    public static bool IsNodeClass(string className) => IsA(className, "Node");

    /// <summary>
    /// The class name for a class name or a script path, e.g. <c>EnemyNavAgent</c> for
    /// <c>res://…/EnemyNavAgent.cs</c>. A path that is no registered global class stays as it is.
    /// </summary>
    public static string ClassNameOf(string classOrPath) {
        if (string.IsNullOrEmpty(classOrPath) || !classOrPath.StartsWith("res://")) return classOrPath ?? "";

        foreach (var entry in ProjectSettings.GetGlobalClassList()) {
            if (entry["path"].AsString() == classOrPath) return entry["class"].AsString();
        }
        return classOrPath;
    }

    /// <summary>The C# type behind an engine class or a script class, or null when there is none.</summary>
    public static Type ResolveClass(string className) {
        if (string.IsNullOrEmpty(className)) return null;

        var engine = typeof(GodotObject).Assembly.GetType($"Godot.{className}");
        if (engine != null) return engine;

        _scriptClasses ??= typeof(BbTypes).Assembly.GetTypes()
            .Where(t => typeof(GodotObject).IsAssignableFrom(t) && !t.IsGenericTypeDefinition && !t.IsNested)
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.First());
        return _scriptClasses.GetValueOrDefault(className);
    }

    /// <summary>The zero value of a type: <c>0.0</c>, <c>""</c>, <c>Vector3.Zero</c>, or null for objects.</summary>
    public static Variant DefaultOf(Variant.Type type) => type switch {
        Variant.Type.Bool => false,
        Variant.Type.Int => 0L,
        Variant.Type.Float => 0.0,
        Variant.Type.String => "",
        Variant.Type.StringName => new StringName(),
        Variant.Type.NodePath => new NodePath(),
        Variant.Type.Vector2 => Vector2.Zero,
        Variant.Type.Vector2I => Vector2I.Zero,
        Variant.Type.Vector3 => Vector3.Zero,
        Variant.Type.Vector3I => Vector3I.Zero,
        Variant.Type.Vector4 => Vector4.Zero,
        Variant.Type.Vector4I => Vector4I.Zero,
        Variant.Type.Color => Colors.White,
        Variant.Type.Rect2 => new Rect2(),
        Variant.Type.Rect2I => new Rect2I(),
        Variant.Type.Quaternion => Quaternion.Identity,
        Variant.Type.Basis => Basis.Identity,
        Variant.Type.Transform2D => Transform2D.Identity,
        Variant.Type.Transform3D => Transform3D.Identity,
        Variant.Type.Projection => Projection.Identity,
        Variant.Type.Plane => new Plane(),
        Variant.Type.Aabb => new Aabb(),
        Variant.Type.Array => new Godot.Collections.Array(),
        Variant.Type.Dictionary => new Godot.Collections.Dictionary(),
        Variant.Type.PackedByteArray => System.Array.Empty<byte>(),
        Variant.Type.PackedInt32Array => System.Array.Empty<int>(),
        Variant.Type.PackedInt64Array => System.Array.Empty<long>(),
        Variant.Type.PackedFloat32Array => System.Array.Empty<float>(),
        Variant.Type.PackedFloat64Array => System.Array.Empty<double>(),
        Variant.Type.PackedStringArray => System.Array.Empty<string>(),
        Variant.Type.PackedVector2Array => System.Array.Empty<Vector2>(),
        Variant.Type.PackedVector3Array => System.Array.Empty<Vector3>(),
        Variant.Type.PackedVector4Array => System.Array.Empty<Vector4>(),
        Variant.Type.PackedColorArray => System.Array.Empty<Color>(),
        _ => new Variant(),
    };

    /// <summary>
    /// Reads a blackboard value as <typeparamref name="T"/>. Fails rather than throwing when the value
    /// has the wrong type, and treats null as "no value" for anything that cannot be null.
    /// </summary>
    public static bool TryConvert<[MustBeVariant] T>(Variant value, out T result) {
        result = default;
        if (TypeOf<T>.IsVariant) {
            result = (T) (object) value;
            return true;
        }

        var actual = value.VariantType;
        if (TypeOf<T>.IsObject) {
            if (actual == Variant.Type.Nil) return true;
            if (actual != Variant.Type.Object || value.AsGodotObject() is not T typed) return false;
            result = typed;
            return true;
        }

        if (actual == Variant.Type.Nil) return false;

        // Variant would happily turn 7.0 into "7" — a value of the wrong type is a mistake, not a string.
        var expected = TypeOf<T>.VariantType;
        if (expected != Variant.Type.Nil && actual != expected
                                         && !(IsNumber(expected) && IsNumber(actual))
                                         && !(IsText(expected) && IsText(actual))) {
            return false;
        }
        result = value.As<T>();
        return true;
    }

    /// <summary>What <see cref="Describe"/> says about <typeparamref name="T"/>, worked out once per type.</summary>
    static class TypeOf<T> {
        public static readonly bool IsVariant = typeof(T) == typeof(Variant);
        public static readonly bool IsObject = typeof(GodotObject).IsAssignableFrom(typeof(T));
        public static readonly Variant.Type VariantType = Describe(typeof(T)).VariantType;
    }

    static bool IsNumber(Variant.Type type) => type is Variant.Type.Int or Variant.Type.Float;

    static bool IsText(Variant.Type type) => type is Variant.Type.String or Variant.Type.StringName;

    /// <summary>
    /// Value equality for two variants: same type and same value, objects by identity. (Variant has
    /// no equality of its own in C#; <c>Equals</c> compares the raw handles.)
    /// </summary>
    public static bool SameValue(Variant a, Variant b) {
        if (a.VariantType != b.VariantType) return false;
        return a.VariantType switch {
            Variant.Type.Nil => true,
            Variant.Type.Object => ReferenceEquals(a.AsGodotObject(), b.AsGodotObject()),
            _ => GD.VarToStr(a) == GD.VarToStr(b),
        };
    }

    /// <summary>A value as it reads in a node summary.</summary>
    public static string Format(Variant value) => value.VariantType switch {
        Variant.Type.Nil => "null",
        Variant.Type.Object => value.AsGodotObject() is Node node ? node.Name : value.AsGodotObject()?.GetClass() ?? "null",
        Variant.Type.String or Variant.Type.StringName => $"\"{value}\"",
        _ => value.ToString(),
    };
}
