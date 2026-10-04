#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Misscore;

namespace Missbehave.Editor;

/// <summary>
/// Puts a tree back to what its file holds, in place.
/// <para>
/// In place rather than by loading a new copy: the tree resource is what scenes, runners, the
/// Inspector and the undo history all point at, so its node and entry objects are kept and only
/// their stored values are overwritten. Nodes the file knows but the tree no longer has — deleted
/// since saving — come back as the objects that were loaded; nodes added since saving drop out.
/// </para>
/// </summary>
internal static class TreeRevert {
    /// <param name="knownNode">Looks up a node the editor still remembers, e.g. one deleted since saving.</param>
    public static Error Revert(BehaviorTree tree, Func<string, MissNode> knownNode = null) {
        if (tree == null || string.IsNullOrEmpty(tree.ResourcePath)) return Error.FileNotFound;

        // Ignore: a fresh copy of the file, not the cached (edited) tree; scripts and other
        // dependencies are still shared.
        var saved = ResourceLoader.Load<BehaviorTree>(tree.ResourcePath, "", ResourceLoader.CacheMode.Ignore);
        if (saved == null) return Error.FileCantRead;

        var nodes = new Dictionary<string, MissNode>();
        foreach (var node in tree.AllNodes()) {
            if (!string.IsNullOrEmpty(node?.Id)) nodes.TryAdd(node.Id, node);
        }
        var entries = new Dictionary<string, BlackboardEntry>();
        foreach (var entry in tree.Blackboard) {
            if (!string.IsNullOrEmpty(entry?.Id)) entries.TryAdd(entry.Id, entry);
        }

        // Loaded object → the object it is written into. Kept only when the type still matches: a
        // node replaced by another type since saving is a different object with a different script.
        var targets = new Dictionary<GodotObject, GodotObject>();
        foreach (var node in saved.AllNodes()) {
            var kept = nodes.GetValueOrDefault(node.Id) ?? knownNode?.Invoke(node.Id);
            targets[node] = kept != null && kept.GetType() == node.GetType() ? kept : node;
        }
        foreach (var entry in saved.Blackboard) {
            if (entry == null) continue;
            targets[entry] = entries.GetValueOrDefault(entry.Id) ?? entry;
        }

        foreach (var (from, to) in targets) CopyStored(from, to, targets);
        CopyStored(saved, tree, targets);
        return Error.Ok;
    }

    /// <summary>
    /// Copies every stored property — exactly what the file holds, parameters included — with
    /// references to loaded nodes and entries swapped for their targets.
    /// </summary>
    static void CopyStored(GodotObject from, GodotObject to, Dictionary<GodotObject, GodotObject> targets) {
        foreach (var property in from.GetPropertyList()) {
            var usage = (PropertyUsageFlags) property["usage"].AsInt64();
            if ((usage & PropertyUsageFlags.Storage) == 0) continue;

            var name = property["name"].AsStringName();
            if (name == "script") continue;
            to.Set(name, Retarget(from.Get(name), targets));
        }
    }

    static Variant Retarget(Variant value, Dictionary<GodotObject, GodotObject> targets) {
        switch (value.VariantType) {
            case Variant.Type.Object:
                return value.AsGodotObject() is { } obj && targets.TryGetValue(obj, out var target) ? Variant.From(target) : value;
            case Variant.Type.Array: {
                var source = value.AsGodotArray();
                // Duplicate keeps a typed array typed, which the Children and Orphans setters expect.
                var copy = source.Duplicate();
                for (var i = 0; i < source.Count; i++) copy[i] = Retarget(source[i], targets);
                return copy;
            }
            default:
                return value;
        }
    }
}
#endif
