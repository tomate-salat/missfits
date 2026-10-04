#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misscore.Editor;

/// <summary>One creatable node type as offered by the create dialog.</summary>
public sealed class NodeTypeInfo {
    public Type Type { get; init; }

    /// <summary>Shown in the dialog: the <see cref="NodeNameAttribute"/> name, or the class name.</summary>
    public string Name { get; init; }

    /// <summary>Group shown in the dialog: Composite, Decorator, Action, Condition or Other.</summary>
    public string Group { get; init; }

    /// <summary>Levels below <see cref="Group"/> from <see cref="NodeGroupAttribute"/>; empty for none.</summary>
    public string[] SubGroup { get; init; } = [];

    public string IconPath { get; init; }

    /// <summary>
    /// False when the class is missing <c>[GlobalClass]</c>. Such a node cannot round-trip through
    /// a resource file, so the dialog flags it instead of failing silently on save.
    /// </summary>
    public bool IsGlobalClass { get; init; }

    /// <summary>
    /// False when the class is missing <c>[Tool]</c>. The editor cannot work with such a node once
    /// its file is loaded again — see <see cref="ToolScripts"/> — so the dialog does not offer it.
    /// </summary>
    public bool IsTool { get; init; }

    public string Description { get; init; }

    public MissNode Create() {
        var node = (MissNode) Activator.CreateInstance(Type);
        node.EnsureId();
        return node;
    }
}

/// <summary>
/// Finds every creatable node type in the project, for the node pickers of the Missfits editors.
/// <para>
/// Discovery is plain .NET reflection: Godot compiles the whole project — addons included — into
/// one assembly, and the editors run inside it, so every built-in and user-authored node type is
/// simply a subclass in <c>typeof(MissNode).Assembly</c>. The global class list is consulted
/// only for icons and for the missing-<c>[GlobalClass]</c> warning.
/// </para>
/// </summary>
public static class NodeTypeRegistry {
    public const string GroupComposite = NodeGroup.Composite;
    public const string GroupDecorator = NodeGroup.Decorator;
    public const string GroupAction = NodeGroup.Action;
    public const string GroupCondition = NodeGroup.Condition;
    public const string GroupOther = NodeGroup.Other;

    public static readonly string[] Groups = [
        GroupComposite, GroupDecorator, GroupAction, GroupCondition, GroupOther,
    ];

    static List<NodeTypeInfo> _types;

    public static IReadOnlyList<NodeTypeInfo> Types {
        get {
            if (_types == null) Refresh();
            return _types;
        }
    }

    public static void Refresh() {
        var icons = ReadGlobalClassIcons(out var globalClassNames);

        _types = [.. typeof(MissNode).Assembly
            .GetTypes()
            .Where(IsCreatable)
            .Select(type => new NodeTypeInfo {
                Type = type,
                Name = NodeAttributes.NameOf(type),
                Group = GroupOf(type),
                SubGroup = NodeAttributes.GroupPathOf(type),
                IconPath = ResolveIcon(type, icons),
                IsGlobalClass = globalClassNames.Contains(type.Name),
                IsTool = ToolScripts.IsTool(type),
                Description = DescriptionOf(type),
            })
            .OrderBy(t => Array.IndexOf(Groups, t.Group))
            // A list heads its group, so it is not lost among the conditions or actions it holds.
            .ThenBy(t => Pristine(t.Type)?.Category == NodeCategory.Leaf ? 1 : 0)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public static IEnumerable<NodeTypeInfo> InGroup(string group)
        => Types.Where(t => t.Group == group);

    public static NodeTypeInfo Find(Type type) => Types.FirstOrDefault(t => t.Type == type);

    /// <summary>A new node of that type, with an id.</summary>
    public static MissNode Create(Type type) {
        var node = (MissNode) Activator.CreateInstance(type);
        node.EnsureId();
        return node;
    }

    /// <summary>Looks a type up by its full .NET name, as carried through signals.</summary>
    public static NodeTypeInfo FindByName(string fullName)
        => string.IsNullOrEmpty(fullName) ? null : Types.FirstOrDefault(t => t.Type.FullName == fullName);

    public static string IconFor(MissNode node)
        => node == null ? "" : Find(node.GetType())?.IconPath ?? "";
    /// <summary>
    /// Walks up the inheritance chain for an icon, so a user's <c>FollowTarget : ActionNode</c>
    /// picks up the generic action icon without having to declare one.
    /// </summary>
    static string ResolveIcon(Type type, Dictionary<string, string> icons) {
        // The class's own declaration first: Godot's class list lags behind until the editor rescans.
        if (type.GetCustomAttributes(typeof(IconAttribute), false).FirstOrDefault() is IconAttribute own) return own.Path;
        for (var current = type; current != null; current = current.BaseType) {
            if (icons.TryGetValue(current.Name, out var path) && !string.IsNullOrEmpty(path)) return path;
        }
        // A class Godot has not registered yet — just built, not scanned — still declares its icon.
        for (var current = type; current != null; current = current.BaseType) {
            if (current.GetCustomAttributes(typeof(IconAttribute), false).FirstOrDefault() is IconAttribute icon) return icon.Path;
        }
        return "";
    }

    /// <summary>
    /// Whether the probe nodes of the self tests are offered. Off in the editor, where they would only
    /// clutter the picker; the editor self test switches it on to create them the usual way.
    /// </summary>
    public static bool IncludeTestTypes { get; set; }

    static bool IsCreatable(Type type)
        => (IncludeTestTypes || !IsTestType(type))
           && !type.IsAbstract
           && typeof(MissNode).IsAssignableFrom(type)
           && type != typeof(MissNode)
           && type.GetConstructor(Type.EmptyTypes) != null;

    /// <summary>
    /// Whether a type is one of the probe nodes the Missfits self tests build with — any addon's,
    /// since actions and conditions are shared.
    /// </summary>
    public static bool IsTestType(Type type)
        => type.Namespace is { } ns && ns.StartsWith("Miss") && ns.EndsWith(".Tests");

    /// <summary>One untouched node per type, to ask what only an instance can say.</summary>
    static readonly Dictionary<Type, MissNode> Pristines = [];

    static MissNode Pristine(Type type) {
        if (Pristines.TryGetValue(type, out var known)) return known;
        var node = type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null ? null : (MissNode) Activator.CreateInstance(type);
        Pristines[type] = node;
        return node;
    }

    /// <summary>Composite, Decorator, Action, Condition or Other — see <see cref="MissNode.PickerGroup"/>.</summary>
    public static string GroupOf(Type type) => Pristine(type)?.PickerGroup ?? GroupOther;

    static string DescriptionOf(Type type) {
        // Namespace is a decent stand-in for "where does this come from": addon nodes live in
        // the addons' own, project nodes in whatever the game uses.
        return type.Namespace ?? "";
    }

    static Dictionary<string, string> ReadGlobalClassIcons(out HashSet<string> names) {
        var icons = new Dictionary<string, string>();
        names = [];

        foreach (var entry in ProjectSettings.GetGlobalClassList()) {
            var name = entry["class"].AsString();
            if (string.IsNullOrEmpty(name)) continue;

            names.Add(name);
            var icon = entry.TryGetValue("icon", out var iconValue) ? iconValue.AsString() : "";
            if (!string.IsNullOrEmpty(icon)) icons[name] = icon;
        }

        return icons;
    }
}
#endif
