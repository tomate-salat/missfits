using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace Misscore;

/// <summary>
/// Files a node type in a sub-group of its category in the node picker:
/// <c>[NodeGroup("Enemies")]</c> on an action lists it under <i>Action/Enemies</i>. Separate levels
/// with a slash, e.g. <c>"Enemies/Ranged"</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NodeGroupAttribute(string path) : Attribute {
    public string Path { get; } = path ?? "";
}

/// <summary>
/// The name a node type goes by in the node picker and on its graph box, instead of its class name:
/// <c>[NodeName("Look at player")]</c>. A node's own <see cref="MissNode.DisplayName"/> still wins.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NodeNameAttribute(string name) : Attribute {
    public string Name { get; } = name ?? "";
}

/// <summary>Reads <see cref="NodeNameAttribute"/> and <see cref="NodeGroupAttribute"/>, once per type.</summary>
public static class NodeAttributes {
    static readonly ConcurrentDictionary<Type, string> Names = new();

    /// <summary>The declared name of a node type, or its class name.</summary>
    public static string NameOf(Type type)
        => Names.GetOrAdd(type, t => t.GetCustomAttribute<NodeNameAttribute>() is { Name.Length: > 0 } named
            ? named.Name.Trim()
            : t.Name);

    /// <summary>The declared sub-group path, split into its levels; empty when there is none.</summary>
    public static string[] GroupPathOf(Type type)
        => type.GetCustomAttribute<NodeGroupAttribute>()?.Path
               .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           ?? [];
}
