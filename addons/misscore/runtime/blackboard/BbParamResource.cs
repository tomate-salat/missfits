using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misscore;

/// <summary>
/// Base of every resource with <see cref="BbParam{T}"/> members — a behavior tree node, say. It
/// creates the parameters, lists them as stored properties so Godot saves, loads, shows and copies
/// them, and carries them across an assembly reload.
/// </summary>
[GlobalClass, Tool]
public abstract partial class BbParamResource : MissResource, ISerializationListener {
    /// <summary>
    /// Creates every <see cref="BbParam{T}"/> member a subclass left without an initializer, so none
    /// needs a <c>new()</c>. Subclass initializers have already run by now — C# runs them before the
    /// base constructor — so a parameter given a starting value keeps it.
    /// </summary>
    protected BbParamResource() {
        foreach (var member in BbParams.Of(GetType())) member.On(this);
    }

    /// <summary>Every <see cref="BbParam{T}"/> member of this resource, with its current parameter.</summary>
    public IEnumerable<(BbParamMember Member, IBbParam Param)> BlackboardParams() => BbParams.On(this);

    /// <summary>The parameters, collected on first use so refreshing needs no reflection.</summary>
    IBbParam[] _paramsForRefresh;

    /// <summary>
    /// Reads every parameter's current value into <see cref="BbParam{T}.Value"/>. Called by whatever
    /// runs this resource, before it runs — again each time, so something running for a while still
    /// sees changes made by others in between.
    /// </summary>
    public void RefreshParams(Blackboard blackboard) {
        _paramsForRefresh ??= [.. BlackboardParams().Select(p => p.Param)];
        foreach (var param in _paramsForRefresh) param.Refresh(blackboard);
    }

    // ---- inspector and storage ---------------------------------------------------------------

    /// <summary>Holds the parameters while the assembly reloads, see <see cref="OnBeforeSerialize"/>.</summary>
    Godot.Collections.Dictionary _paramsAcrossReload;

    /// <summary>
    /// Lists each <see cref="BbParam{T}"/> as a stored property, which is all it takes for Godot to
    /// save it into the resource, load it back, show it in the Inspector and copy it into duplicates.
    /// </summary>
    public override Godot.Collections.Array<Godot.Collections.Dictionary> _GetPropertyList() {
        var members = BbParams.Of(GetType());
        if (members.Count == 0) return null;

        var properties = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        foreach (var member in members) {
            // A parameter still at its initial value is not stored: the resource saver has no notion of
            // a script-side default here and would write every one of them into every file otherwise.
            // A duplicate loses nothing by skipping it either — its own initializer produced the same value.
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

        if (name.EndsWith(BbParams.LiteralSuffix)
            && BbParams.Find(GetType(), name[..^BbParams.LiteralSuffix.Length])?.On(this) is { } literalOf) {
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

        if (name.EndsWith(BbParams.LiteralSuffix)
            && BbParams.Find(GetType(), name[..^BbParams.LiteralSuffix.Length])?.On(this) is { } literalOf) {
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
    /// write those into the file. They ride along in a Godot dictionary instead.
    /// </summary>
    public virtual void OnBeforeSerialize() {
        _paramsAcrossReload = [];
        foreach (var (member, param) in BlackboardParams()) _paramsAcrossReload[member.Name] = BbParams.ToStorage(param);
    }

    public virtual void OnAfterDeserialize() {
        if (_paramsAcrossReload == null) return;
        foreach (var (member, param) in BlackboardParams()) {
            if (_paramsAcrossReload.TryGetValue(member.Name, out var stored)) BbParams.FromStorage(param, stored);
        }
        _paramsAcrossReload = null;
    }
}
