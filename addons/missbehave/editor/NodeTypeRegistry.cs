#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;

namespace Missbehave.Editor;

/// <summary>One creatable node type as offered by the create dialog.</summary>
public sealed class BtNodeType {
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
    /// a tree resource, so the dialog flags it instead of failing silently on save.
    /// </summary>
    public bool IsGlobalClass { get; init; }

    public string Description { get; init; }

    public MissNode Create() {
        var node = (MissNode) Activator.CreateInstance(Type);
        node.EnsureId();
        return node;
    }
}

/// <summary>
/// Finds every creatable behavior node in the project.
/// <para>
/// Discovery is plain .NET reflection: Godot compiles the whole project — addons included — into
/// one assembly, and this plugin runs inside it, so every built-in and user-authored node type is
/// simply a subclass in <c>typeof(MissNode).Assembly</c>. The global class list is consulted
/// only for icons and for the missing-<c>[GlobalClass]</c> warning.
/// </para>
/// </summary>
public static class NodeTypeRegistry {
    public const string GroupComposite = "Composite";
    public const string GroupDecorator = "Decorator";
    public const string GroupAction = "Action";
    public const string GroupCondition = "Condition";
    public const string GroupOther = "Other";

    public static readonly string[] Groups = [
        GroupComposite, GroupDecorator, GroupAction, GroupCondition, GroupOther,
    ];

    static List<BtNodeType> _types;

    public static IReadOnlyList<BtNodeType> Types {
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
            .Select(type => new BtNodeType {
                Type = type,
                Name = NodeAttributes.NameOf(type),
                Group = GroupOf(type),
                SubGroup = NodeAttributes.GroupPathOf(type),
                IconPath = ResolveIcon(type, icons),
                IsGlobalClass = globalClassNames.Contains(type.Name),
                Description = DescriptionOf(type),
            })
            .OrderBy(t => Array.IndexOf(Groups, t.Group))
            // A list heads its group, so it is not lost among the conditions or actions it holds.
            .ThenBy(t => typeof(AListNode).IsAssignableFrom(t.Type) ? 0 : 1)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public static IEnumerable<BtNodeType> InGroup(string group)
        => Types.Where(t => t.Group == group);

    public static BtNodeType Find(Type type) => Types.FirstOrDefault(t => t.Type == type);

    /// <summary>Looks a type up by its full .NET name, as carried through signals.</summary>
    public static BtNodeType FindByName(string fullName)
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

    /// <summary>Namespace of the probe nodes the self tests build trees from.</summary>
    const string TestNamespace = "Missbehave.Tests";

    /// <summary>
    /// Whether the probe nodes of the self tests are offered. Off in the editor, where they would only
    /// clutter the picker; the editor self test switches it on to create them the usual way.
    /// </summary>
    public static bool IncludeTestTypes { get; set; }

    static bool IsCreatable(Type type)
        => (IncludeTestTypes || type.Namespace != TestNamespace)
           && !type.IsAbstract
           && typeof(MissNode).IsAssignableFrom(type)
           && type != typeof(MissNode)
           && type.GetConstructor(Type.EmptyTypes) != null;

    /// <summary>Composite, Decorator, Action, Condition or Other.</summary>
    public static string GroupOf(Type type) {
        // A list is filed with what it holds: a condition list is used like a condition.
        if (typeof(ConditionListNode).IsAssignableFrom(type)) return GroupCondition;
        if (typeof(ActionListNode).IsAssignableFrom(type)) return GroupAction;
        if (typeof(ACompositeNode).IsAssignableFrom(type)) return GroupComposite;
        if (typeof(ADecoratorNode).IsAssignableFrom(type)) return GroupDecorator;
        if (typeof(ConditionNode).IsAssignableFrom(type)) return GroupCondition;
        if (typeof(ActionNode).IsAssignableFrom(type)) return GroupAction;
        return GroupOther;
    }

    static string DescriptionOf(Type type) {
        // Namespace is a decent stand-in for "where does this come from": addon nodes live in
        // Missbehave, project nodes in whatever the game uses.
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
