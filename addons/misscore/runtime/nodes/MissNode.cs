using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misscore;

/// <summary>
/// Base class of everything a Missfits addon ticks: the composites and decorators of a behavior
/// tree as much as the actions and conditions at its leaves, which a state machine runs just the
/// same.
/// <para>
/// A node resource lives in one of two roles. Nodes loaded from a file are <b>definitions</b>: they
/// are shared by everything using that file and must never be ticked. Whatever runs them builds its
/// own <b>runtime</b> copy via <see cref="CloneRuntime"/>, and only those copies carry per-instance
/// tick state.
/// </para>
/// <para>
/// Running a node means: <see cref="Begin"/> before a run that does not resume one left
/// <see cref="MissStatus.Running"/>, <see cref="Execute"/> once per tick, <see cref="AfterRun"/>
/// when it finished, <see cref="Interrupt"/> when it is abandoned mid-run.
/// </para>
/// </summary>
[GlobalClass, Tool]
public abstract partial class MissNode : BbParamResource, IBbParamHost {
    /// <summary>Optional label shown in the graph instead of the class name.</summary>
    [Export]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Ordered children. Order is semantic — a sequence runs them front to back — and is authored
    /// by node position in the graph editor. The runtime never reorders this.
    /// </summary>
    [Export]
    public Godot.Collections.Array<MissNode> Children { get; set; } = [];

    /// <summary>Stable identity, assigned once on creation. Survives edits, moves and reorders.</summary>
    [Export]
    public string Id { get; set; } = "";

    /// <summary>Authored position in the graph editor. Storage only.</summary>
    [Export]
    public Vector2 GraphPosition { get; set; }

    /// <summary>
    /// Index within the running instance, assigned by whatever runs the node — pre-order within a
    /// flattened tree. Used as a compact debug wire key.
    /// </summary>
    public int RuntimeIndex { get; set; } = -1;

    /// <summary>On a runtime clone, the <see cref="Id"/> of the definition it came from.</summary>
    public string DefinitionId { get; private set; } = "";

    /// <summary>True for authored resources, false for the per-runner clones that actually tick.</summary>
    public bool IsDefinition { get; private set; } = true;

    public MissStatus LastStatus { get; private set; }

    // ---- editor-facing metadata -------------------------------------------------------------

    public virtual int MinChildren => 0;
    public virtual int MaxChildren => 0;

    /// <summary>One of <see cref="NodeCategory"/> — drives graph slots and the create dialog.</summary>
    public virtual string Category => NodeCategory.Leaf;

    /// <summary>
    /// One of <see cref="NodeGroup"/>: where a node picker files this type. Follows the
    /// <see cref="Category"/> unless a type says otherwise — a list of conditions is a composite,
    /// but belongs with the conditions.
    /// </summary>
    public virtual string PickerGroup => Category switch {
        NodeCategory.Composite => NodeGroup.Composite,
        NodeCategory.Decorator => NodeGroup.Decorator,
        _ => NodeGroup.Other,
    };

    /// <summary>Short second line rendered on the graph node, e.g. a decorator's parameter.</summary>
    public virtual string GetSummary() => "";

    public string GetLabel() => string.IsNullOrWhiteSpace(DisplayName) ? NodeAttributes.NameOf(GetType()) : DisplayName;

    public virtual string[] GetConfigurationWarnings() {
        var warnings = new List<string>();
        var count = Children?.Count ?? 0;
        if (count < MinChildren) {
            warnings.Add(MinChildren == MaxChildren
                ? $"needs exactly {MinChildren} child(ren), has {count}"
                : $"needs at least {MinChildren} child(ren), has {count}");
        }
        if (count > MaxChildren) {
            warnings.Add($"accepts at most {MaxChildren} child(ren), has {count}");
        }
        for (var i = 0; i < count; i++) {
            if (Children[i] == null) warnings.Add($"child #{i + 1} is empty");
        }
        foreach (var (member, param) in BlackboardParams()) {
            if (member.EntryOnly && !param.IsLinked) warnings.Add($"{member.Name} is not linked to a blackboard entry");
        }
        return [.. warnings];
    }

    // ---- tick contract ----------------------------------------------------------------------

    /// <summary>Advance this node by one tick. Implemented by every concrete node.</summary>
    protected abstract MissStatus Tick(MissContext ctx);

    /// <summary>Called before a tick that does not resume a running node.</summary>
    public virtual void BeforeRun(MissContext ctx) { }

    /// <summary>Called after a tick that finished with Success or Failure.</summary>
    public virtual void AfterRun(MissContext ctx) { }

    /// <summary>Called when a branch is abandoned mid-run so it can drop whatever it started.</summary>
    public virtual void Interrupt(MissContext ctx) { }

    /// <summary>Hook for clones to reset per-instance state. Runs after the clone is populated.</summary>
    protected virtual void OnCloned() { }

    /// <summary>Starts a run. What callers use instead of <see cref="BeforeRun"/>: parameter values are current by then.</summary>
    public void Begin(MissContext ctx) {
        RefreshParams(ctx.Blackboard);
        BeforeRun(ctx);
    }

    /// <summary>Ticks the node once, with parameter values read fresh, and reports the status.</summary>
    public MissStatus Execute(MissContext ctx) {
        if (IsDefinition) {
            GD.PushError($"misscore: tried to tick a definition node ({GetType().Name} {Id}). " +
                         "Definitions are shared — tick a runtime clone instead.");
            return MissStatus.Failure;
        }

        // Before each tick too, so a node running over many ticks still sees what others wrote in between.
        RefreshParams(ctx.Blackboard);
        var status = Tick(ctx);
        LastStatus = status;
        ctx.Observer?.Report(RuntimeIndex, status);
        return status;
    }

    // ---- cloning ----------------------------------------------------------------------------

    /// <summary>
    /// Produces the per-runner runtime copy of this subtree. Exported scalars are copied, exported
    /// Resource references stay shared, and children are cloned recursively into a fresh array.
    /// </summary>
    public virtual MissNode CloneRuntime() {
        var clone = (MissNode) Duplicate(false);

        // A duplicated built-in subresource can inherit "res://tree.tres::Node_7" and then be
        // handed to the next runner straight out of the resource cache. Clearing the path is what
        // keeps two enemies from sharing one running-child index.
        clone.ResourcePath = "";
        clone.ResourceLocalToScene = false;

        clone.IsDefinition = false;
        clone.DefinitionId = Id;
        clone.RuntimeIndex = -1;

        var children = new Godot.Collections.Array<MissNode>();
        foreach (var child in Children) {
            if (child != null) children.Add(child.CloneRuntime());
        }
        clone.Children = children;

        clone.OnCloned();
        return clone;
    }

    public static string NewId() => Guid.NewGuid().ToString("N");

    public void EnsureId() {
        if (string.IsNullOrEmpty(Id)) Id = NewId();
    }

    // ---- inspector ---------------------------------------------------------------------------

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        var name = property["name"].AsString();
        if (name is nameof(Id) or nameof(GraphPosition) or nameof(Children)) {
            property["usage"] = (int) PropertyUsageFlags.Storage;
        }
    }

    // ---- blackboard parameters ---------------------------------------------------------------

    /// <inheritdoc cref="BbParams.LiteralSuffix"/>
    public const string LiteralSuffix = BbParams.LiteralSuffix;
}

/// <summary>The groups a node picker files node types in.</summary>
public static class NodeGroup {
    public const string Composite = "Composite";
    public const string Decorator = "Decorator";
    public const string Action = "Action";
    public const string Condition = "Condition";
    public const string Other = "Other";
}

public static class NodeCategory {
    public const string Composite = "Composite";
    public const string Decorator = "Decorator";
    public const string Leaf = "Leaf";
}
