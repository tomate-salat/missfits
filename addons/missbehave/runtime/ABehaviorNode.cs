using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Base class of every behavior tree node.
/// <para>
/// A node resource lives in one of two roles. Nodes loaded from a <c>.tres</c> are
/// <b>definitions</b>: they are shared by every runner using that tree and must never be ticked.
/// Each <see cref="BehaviorTreeRunner"/> builds its own <b>runtime</b> copy via
/// <see cref="CloneRuntime"/>, and only those copies carry per-instance tick state.
/// </para>
/// </summary>
[GlobalClass, Tool]
public abstract partial class ABehaviorNode : Resource, ISerializationListener, IBbParamHost {
    /// <summary>Optional label shown in the graph instead of the class name.</summary>
    [Export]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Ordered children. Order is semantic — a sequence runs them front to back — and is authored
    /// by node position in the graph editor. The runtime never reorders this.
    /// </summary>
    [Export]
    public Godot.Collections.Array<ABehaviorNode> Children { get; set; } = [];

    /// <summary>Stable identity, assigned once on creation. Survives edits, moves and reorders.</summary>
    [Export]
    public string Id { get; set; } = "";

    /// <summary>Authored position in the graph editor. Storage only.</summary>
    [Export]
    public Vector2 GraphPosition { get; set; }

    /// <summary>Pre-order index within the flattened tree. Used as a compact debug wire key.</summary>
    public int RuntimeIndex { get; internal set; } = -1;

    /// <summary>On a runtime clone, the <see cref="Id"/> of the definition it came from.</summary>
    public string DefinitionId { get; internal set; } = "";

    /// <summary>True for authored resources, false for the per-runner clones that actually tick.</summary>
    public bool IsDefinition { get; internal set; } = true;

    public BehaviorStatus LastStatus { get; private set; }

    /// <summary>
    /// Creates every <see cref="BbParam{T}"/> member a subclass left without an initializer, so none
    /// needs a <c>new()</c>. Subclass initializers have already run by now — C# runs them before the
    /// base constructor — so a parameter given a starting value keeps it.
    /// </summary>
    protected ABehaviorNode() {
        foreach (var member in BbParams.Of(GetType())) member.On(this);
    }

    // ---- editor-facing metadata -------------------------------------------------------------

    public virtual int MinChildren => 0;
    public virtual int MaxChildren => 0;

    /// <summary>"Composite", "Decorator" or "Leaf" — drives graph slots and the create dialog.</summary>
    public virtual string Category => BtCategory.Leaf;

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
    protected abstract BehaviorStatus Tick(BtContext ctx);

    /// <summary>Called by the parent before a tick that does not resume a running node.</summary>
    public virtual void BeforeRun(BtContext ctx) { }

    /// <summary>Called by the parent after a tick that finished with Success or Failure.</summary>
    public virtual void AfterRun(BtContext ctx) { }

    /// <summary>Called when a branch is abandoned mid-run so it can drop whatever it started.</summary>
    public virtual void Interrupt(BtContext ctx) { }

    /// <summary>Hook for clones to reset per-instance state. Runs after the clone is populated.</summary>
    protected virtual void OnCloned() { }

    /// <summary>What parents call instead of <see cref="BeforeRun"/>: parameter values are current by then.</summary>
    internal void BeforeRunInternal(BtContext ctx) {
        RefreshParams(ctx);
        BeforeRun(ctx);
    }

    /// <summary>This node's parameters, collected on first use so ticking needs no reflection.</summary>
    IBbParam[] _paramsForTicking;

    /// <summary>
    /// Reads every parameter's current value into <see cref="BbParam{T}.Value"/> — before
    /// <see cref="BeforeRun"/> and before each tick, so a node running over many ticks still sees
    /// changes made by others in between.
    /// </summary>
    void RefreshParams(BtContext ctx) {
        _paramsForTicking ??= [.. BlackboardParams().Select(p => p.Param)];
        foreach (var param in _paramsForTicking) param.Refresh(ctx.Blackboard);
    }

    internal BehaviorStatus TickInternal(BtContext ctx) {
        if (IsDefinition) {
            GD.PushError($"missbehave: tried to tick a definition node ({GetType().Name} {Id}). " +
                         "Definitions are shared between runners — tick a BehaviorTreeInstance instead.");
            return BehaviorStatus.Failure;
        }

        RefreshParams(ctx);
        var status = Tick(ctx);
        LastStatus = status;
        ctx.Instance?.Report(RuntimeIndex, status);
        return status;
    }

    // ---- cloning ----------------------------------------------------------------------------

    /// <summary>
    /// Produces the per-runner runtime copy of this subtree. Exported scalars are copied, exported
    /// Resource references stay shared, and children are cloned recursively into a fresh array.
    /// </summary>
    public virtual ABehaviorNode CloneRuntime() {
        var clone = (ABehaviorNode) Duplicate(false);

        // A duplicated built-in subresource can inherit "res://tree.tres::Node_7" and then be
        // handed to the next runner straight out of the resource cache. Clearing the path is what
        // keeps two enemies from sharing one running-child index.
        clone.ResourcePath = "";
        clone.ResourceLocalToScene = false;

        clone.IsDefinition = false;
        clone.DefinitionId = Id;
        clone.RuntimeIndex = -1;

        var children = new Godot.Collections.Array<ABehaviorNode>();
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

    /// <summary>Holds the parameters while the assembly reloads, see <see cref="OnBeforeSerialize"/>.</summary>
    Godot.Collections.Dictionary _paramsAcrossReload;

    /// <summary>Every <see cref="BbParam{T}"/> member of this node, with its current parameter.</summary>
    public IEnumerable<(BbParamMember Member, IBbParam Param)> BlackboardParams() => BbParams.On(this);

    /// <summary>
    /// Lists each <see cref="BbParam{T}"/> as a stored property, which is all it takes for Godot to
    /// save it into the tree, load it back, show it in the Inspector and copy it into runtime clones.
    /// </summary>
    public override Godot.Collections.Array<Godot.Collections.Dictionary> _GetPropertyList() {
        var members = BbParams.Of(GetType());
        if (members.Count == 0) return null;

        var properties = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var member in members) {
            // A parameter still at its initial value is not stored: the resource saver has no notion of
            // a script-side default here and would write every one of them into every tree otherwise.
            // A clone loses nothing by skipping it either — its own initializer produced the same value.
            var usage = PropertyUsageFlags.Editor;
            var pristine = BbParams.DefaultStorage(GetType(), member);
            if (pristine == null || member.On(this) is not { } param
                                 || !BbTypes.SameValue(BbParams.ToStorage(param), pristine)) {
                usage |= PropertyUsageFlags.Storage;
            }

            properties.Add(new Godot.Collections.Dictionary {
                { "name", member.Name },
                { "type", (int) Variant.Type.Dictionary },
                { "hint", (int) PropertyHint.None },
                { "hint_string", BbParams.HintString },
                { "usage", (int) usage },
            });
        }
        return properties;
    }

    public override Variant _Get(StringName property) {
        var name = property.ToString();
        if (BbParams.Find(GetType(), name)?.On(this) is { } param) return BbParams.ToStorage(param);

        if (name.EndsWith(LiteralSuffix)
            && BbParams.Find(GetType(), name[..^LiteralSuffix.Length])?.On(this) is { } literalOf) {
            return literalOf.Literal;
        }
        return default;
    }

    public override bool _Set(StringName property, Variant value) {
        var name = property.ToString();
        if (BbParams.Find(GetType(), name)?.On(this) is { } param) {
            BbParams.FromStorage(param, value);
            return true;
        }

        if (name.EndsWith(LiteralSuffix)
            && BbParams.Find(GetType(), name[..^LiteralSuffix.Length])?.On(this) is { } literalOf) {
            literalOf.Literal = value;
            return true;
        }
        return false;
    }

    /// <summary>The Inspector compares against the revert value itself before showing the arrow.</summary>
    public override bool _PropertyCanRevert(StringName property)
        => BbParams.Find(GetType(), property.ToString()) is { } member
           && BbParams.DefaultStorage(GetType(), member) != null;

    public override Variant _PropertyGetRevert(StringName property)
        => BbParams.Find(GetType(), property.ToString()) is { } member
            ? BbParams.DefaultStorage(GetType(), member) ?? new Variant()
            : new Variant();

    /// <summary>
    /// A reload rebuilds the C# object and restores only Variant-compatible members, so the parameters
    /// — plain C# objects — would silently fall back to their initial values, and the next save would
    /// write those into the tree. They ride along in a Godot dictionary instead.
    /// </summary>
    public void OnBeforeSerialize() {
        _paramsAcrossReload = [];
        foreach (var (member, param) in BlackboardParams()) _paramsAcrossReload[member.Name] = BbParams.ToStorage(param);
    }

    public void OnAfterDeserialize() {
        if (_paramsAcrossReload == null) return;
        foreach (var (member, param) in BlackboardParams()) {
            if (_paramsAcrossReload.TryGetValue(member.Name, out var stored)) BbParams.FromStorage(param, stored);
        }
        _paramsAcrossReload = null;
    }
}

public static class BtCategory {
    public const string Composite = "Composite";
    public const string Decorator = "Decorator";
    public const string Leaf = "Leaf";
}
