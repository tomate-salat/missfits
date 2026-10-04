#if TOOLS
using Godot;

namespace Misscore.Editor;

/// <summary>
/// Reload-proof references to objects whose class is one of this assembly's own scripts.
/// <para>
/// Pressing play reloads the assembly. Godot then recreates the C# instance of every scripted
/// object and restores each one's fields — one object at a time. A field typed as, say,
/// a behavior tree node can be restored before the node it points at has its C# instance
/// back, at which point the only wrapper available is a bare <c>Godot.Resource</c> and the cast
/// in the generated restore code throws <c>InvalidCastException</c>.
/// </para>
/// <para>
/// So such references are stored as plain <see cref="GodotObject"/> fields, which restore without a
/// cast and still keep the object alive, and are resolved to their typed instance on access. A
/// stale wrapper is swapped for the live one the first time it is read after the reload.
/// </para>
/// </summary>
public static class ReloadSafe {
    /// <typeparam name="T">The script class, or an interface only script classes implement.</typeparam>
    public static T Get<T>(ref GodotObject field) where T : class {
        if (field is T typed) return typed;
        if (field == null || !GodotObject.IsInstanceValid(field)) return null;
        if (GodotObject.InstanceFromId(field.GetInstanceId()) is not T current) return null;

        field = (GodotObject) (object) current;
        return current;
    }
}
#endif
