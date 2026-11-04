using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace Misswire;

/// <summary>
/// The default values of C# methods, which Godot does not know: it lists every parameter of a C#
/// method as needed, and a call by name has to pass them all. misswire is compiled into the same
/// assembly as the game, so it reads them from the C# class instead — for a placeholder too, by
/// the script's path.
/// </summary>
public static class MissDefaults {
    const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>The C# class behind an object: its own type, or for a placeholder the class its script names.</summary>
    public static Type ClassOf(GodotObject obj) {
        if (obj == null) return null;
        // A C# instance is its own class, script attribute or not; only a placeholder (or an engine
        // object) shows the engine's type and needs its script looked up.
        if (obj.GetType().Namespace != "Godot" || obj.GetType().GetCustomAttributes<ScriptPathAttribute>(false).Any()) return obj.GetType();
        return obj.GetScript().As<Script>() is { } script ? ClassOf(script) : null;
    }

    /// <summary>
    /// Script path to C# class, found once. Static, so a reload of the assembly empties it — which is
    /// right, the classes are new then.
    /// </summary>
    static Dictionary<string, Type> _classes;

    /// <summary>The C# class a script stands for, or null for a script in another language.</summary>
    public static Type ClassOf(Script script) {
        var path = script?.ResourcePath;
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return null;
        if (_classes == null || !_classes.ContainsKey(path)) _classes = FindClasses();
        return _classes.GetValueOrDefault(path);
    }

    static Dictionary<string, Type> FindClasses() {
        var classes = new Dictionary<string, Type>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            if (assembly.IsDynamic) continue;
            Type[] types;
            try {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e) {
                types = e.Types.Where(t => t != null).ToArray();
            }
            foreach (var type in types) {
                foreach (var attribute in type.GetCustomAttributes<ScriptPathAttribute>(false)) classes.TryAdd(attribute.Path, type);
            }
        }
        return classes;
    }

    /// <summary>The C# method a Godot method of this object is, with that many parameters, or null.</summary>
    public static MethodInfo MethodOf(GodotObject obj, string name, int parameters) => MethodOf(ClassOf(obj), name, parameters);

    public static MethodInfo MethodOf(Type type, string name, int parameters) =>
        type?.GetMethods(Members).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameters);

    /// <summary>The C# defaults of a method's parameters, in order; null where a parameter has none.</summary>
    public static List<Variant?> DefaultsOf(MethodInfo method) {
        var defaults = new List<Variant?>();
        if (method == null) return defaults;
        foreach (var parameter in method.GetParameters()) defaults.Add(parameter.HasDefaultValue ? ToVariant(parameter.DefaultValue) : null);
        return defaults;
    }

    /// <summary>
    /// How many parameters a method needs: Godot's count, unless it is a C# method whose last ones
    /// have defaults Godot does not know about.
    /// </summary>
    public static int Required(GodotObject obj, string name, int total, int godotRequired) {
        if (godotRequired < total || MethodOf(obj, name, total) is not { } method) return godotRequired;
        var defaults = DefaultsOf(method);
        var required = total;
        while (required > 0 && defaults[required - 1] != null) required--;
        return required;
    }

    /// <summary>
    /// Adds the C# defaults of the parameters a call leaves out, so a C# method called by name gets
    /// every argument. Leaves the arguments as they are for anything else.
    /// </summary>
    public static void Fill(GodotObject obj, string name, int total, Godot.Collections.Array arguments) {
        if (arguments.Count >= total || MethodOf(obj, name, total) is not { } method) return;
        var defaults = DefaultsOf(method);
        for (var i = arguments.Count; i < total && defaults[i] is { } value; i++) arguments.Add(value);
    }

    /// <summary>A C# default as a Variant: the types a parameter of a Godot method can have, and null.</summary>
    static Variant? ToVariant(object value) => value switch {
        null => default(Variant),
        bool b => b,
        int i => i,
        long l => l,
        float f => f,
        double d => d,
        string s => s,
        StringName n => n,
        NodePath p => p,
        Vector2 v => v,
        Vector3 v => v,
        Color c => c,
        Enum e => Convert.ToInt64(e),
        _ => (Variant?) null,
    };
}
