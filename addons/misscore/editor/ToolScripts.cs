#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Misscore.Editor;

/// <summary>
/// Finds node classes that lack <c>[Tool]</c>.
/// <para>
/// The editor only runs scripts marked as tools. A resource whose script is not one is loaded as a
/// placeholder there: Godot keeps its stored values, but no C# object stands behind it. An action
/// like that looks fine while it is being created — the editor made the object itself — and turns
/// into a placeholder the next time its file is loaded, at which point nothing can ask it for its
/// name, its parameters or anything else. So such a file is not opened, and the classes are named.
/// </para>
/// </summary>
public static class ToolScripts {
    public static bool IsTool(Type type) => type.IsDefined(typeof(ToolAttribute), inherit: false);

    /// <summary>Names of the node classes a resource file uses that are not tool scripts.</summary>
    public static List<string> MissingIn(string resourcePath) {
        var missing = new List<string>();
        if (string.IsNullOrEmpty(resourcePath) || !ResourceLoader.Exists(resourcePath)) return missing;

        foreach (var dependency in ResourceLoader.GetDependencies(resourcePath)) {
            var path = PathOf(dependency);
            if (!path.EndsWith(".cs")) continue;

            // A C# script class is named after its file.
            var name = Path.GetFileNameWithoutExtension(path);
            var type = typeof(MissNode).Assembly.GetTypes()
                .FirstOrDefault(t => t.Name == name && typeof(MissNode).IsAssignableFrom(t));
            if (type != null && !IsTool(type) && !missing.Contains(type.Name)) missing.Add(type.Name);
        }
        return missing;
    }

    /// <summary>What to tell the user when <see cref="MissingIn"/> found something, or null when it did not.</summary>
    public static string Explain(string resourcePath) {
        var missing = MissingIn(resourcePath);
        if (missing.Count == 0) return null;
        return $"{resourcePath} cannot be opened: {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} missing [Tool]. "
               + "Write [GlobalClass, Tool] above the class, build, and restart the editor.";
    }

    /// <summary>A dependency as Godot lists it is "uid::type::path"; older files carry the bare path.</summary>
    static string PathOf(string dependency) {
        var cut = dependency.LastIndexOf("::", StringComparison.Ordinal);
        var path = cut >= 0 ? dependency[(cut + 2)..] : dependency;
        if (path.StartsWith("res://")) return path;

        var uid = cut >= 0 ? dependency[..dependency.IndexOf("::", StringComparison.Ordinal)] : dependency;
        return uid.StartsWith("uid://") ? ResourceUid.GetIdPath(ResourceUid.TextToId(uid)) : path;
    }
}
#endif