using System.Collections.Generic;
using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// An authored behavior tree, saved as a single <c>.tres</c> with every node stored as a built-in
/// sub-resource. This is a definition: it is never ticked directly, and it may be assigned to any
/// number of runners.
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/tree.svg")]
public partial class BehaviorTree : MissResource, IBlackboardSource {
    [Export]
    public MissNode Root { get; set; }

    /// <summary>
    /// Nodes that are authored but currently detached from the root. Without this they would be
    /// unreferenced and silently dropped on save, losing work every time a branch is unplugged.
    /// </summary>
    [Export]
    public Godot.Collections.Array<MissNode> Orphans { get; set; } = [];

    /// <summary>Position of the synthetic root entry in the graph editor.</summary>
    [Export]
    public Vector2 RootGraphPosition { get; set; }

    [Export(PropertyHint.MultilineText)]
    public string Description { get; set; } = "";

    /// <summary>
    /// The values this tree works with. Edited in the Missbehave panel's blackboard, which is why the
    /// Inspector does not show it; nodes link to entries by id through <see cref="BbParam{T}"/>.
    /// </summary>
    [Export]
    public Godot.Collections.Array<BlackboardEntry> Blackboard { get; set; } = [];

    public BlackboardEntry FindEntry(string id) {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var entry in Blackboard) {
            if (entry?.Id == id) return entry;
        }
        return null;
    }

    public BlackboardEntry FindEntryByName(string name) {
        foreach (var entry in Blackboard) {
            if (entry != null && entry.Name == name) return entry;
        }
        return null;
    }

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() == nameof(Blackboard)) property["usage"] = (int) PropertyUsageFlags.Storage;
    }

    /// <summary>Pre-order walk of the connected tree, assigning <see cref="MissNode.RuntimeIndex"/>.</summary>
    public MissNode[] Flatten() {
        var flat = new List<MissNode>();
        Flatten(Root, flat);
        return [.. flat];
    }

    internal static void Flatten(MissNode node, List<MissNode> into) {
        if (node == null) return;
        node.RuntimeIndex = into.Count;
        into.Add(node);
        foreach (var child in node.Children) Flatten(child, into);
    }

    IEnumerable<IBbParamHost> IBlackboardSource.ParamHosts() => AllNodes();

    /// <summary>Every node reachable from the root plus every orphan, for editor-side iteration.</summary>
    public IEnumerable<MissNode> AllNodes() {
        foreach (var node in Walk(Root)) yield return node;
        foreach (var orphan in Orphans) {
            foreach (var node in Walk(orphan)) yield return node;
        }
    }

    static IEnumerable<MissNode> Walk(MissNode node) {
        if (node == null) yield break;
        yield return node;
        foreach (var child in node.Children) {
            foreach (var descendant in Walk(child)) yield return descendant;
        }
    }

    public string[] Validate() {
        var problems = new List<string>();
        if (Root == null) {
            problems.Add("The tree has no root node.");
            return [.. problems];
        }
        foreach (var node in AllNodes()) {
            foreach (var warning in node.GetConfigurationWarnings()) {
                problems.Add($"{node.GetLabel()}: {warning}");
            }
        }
        foreach (var node in AllNodes()) {
            foreach (var problem in LinkProblems(node)) problems.Add($"{node.GetLabel()}: {problem}");
        }
        if (Orphans.Count > 0) {
            problems.Add($"{Orphans.Count} node(s) are not connected to the root and will never run.");
        }
        return [.. problems];
    }

    /// <summary>Parameters of <paramref name="node"/> linked to entries that are gone or of the wrong type.</summary>
    public IEnumerable<string> LinkProblems(MissNode node) {
        if (node == null) yield break;
        foreach (var (member, param) in node.BlackboardParams()) {
            if (!param.IsLinked) continue;

            var entry = FindEntry(param.EntryId);
            if (entry == null) {
                yield return $"{member.Name} is linked to a blackboard entry that no longer exists ({param.EntryName})";
            }
            else if (!BbTypes.Accepts(member.ValueType, entry)) {
                var expected = BbTypes.Describe(member.ValueType);
                yield return $"{member.Name} expects {BbTypes.Label(expected.VariantType, expected.ClassName)}, " +
                             $"but {entry.Name} is {entry.TypeLabel}";
            }
        }
    }
}
