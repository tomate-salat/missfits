using System;
using Godot;

namespace Misscore;

/// <summary>Untyped view of a <see cref="BbParam{T}"/>, for the editor and for serialization.</summary>
public interface IBbParam {
    Type ValueType { get; }

    /// <summary>The fixed value, used whenever the parameter is not linked to an entry.</summary>
    Variant Literal { get; set; }

    /// <summary>Id of the linked blackboard entry, or empty for a fixed value.</summary>
    string EntryId { get; set; }

    /// <summary>
    /// The linked entry's name when it was last seen by the editor. Display only — the link itself is
    /// <see cref="EntryId"/>, and renaming the entry updates this copy.
    /// </summary>
    string EntryName { get; set; }

    bool IsLinked { get; }

    /// <summary>Reads the current value and remembers the blackboard; called by the host before it runs and ticks.</summary>
    void Refresh(Blackboard blackboard);
}

/// <summary>
/// A parameter that is either a fixed value or a link to an entry on the blackboard — which one is
/// chosen in the Inspector, not in code. On a behavior tree node:
/// <code>
/// BbParam&lt;float&gt; Speed { get; set; } = 4f;
/// BbParam&lt;Node3D&gt; Target { get; set; }
///
/// protected override BehaviorStatus Run(BtContext ctx) {
///     Target.Value.GlobalPosition += Vector3.Forward * Speed.Value * (float) ctx.Delta;
/// </code>
/// <see cref="Value"/> is whatever the host last read through <see cref="IBbParam.Refresh"/> — a
/// behavior tree node does so before <c>BeforeRun</c> and before every tick, so it is the value as
/// of the start of that tick. <see cref="Get"/> reads the blackboard right now — only needed when
/// something earlier in the same tick may just have changed it.
/// No <c>[Export]</c>: Godot cannot export a generic type, so the host finds these members by their
/// type (<see cref="BbParams"/>) and stores them itself. Nor any <c>new()</c>: a parameter left
/// without an initializer is created by the host's constructor, and a plain value converts into one.
/// <para>
/// A class rather than a struct on purpose: members are properties, and a struct property hands out
/// copies — an unlinked <see cref="Set"/> would write into a copy and be lost.
/// </para>
/// </summary>
public sealed class BbParam<[MustBeVariant] T> : IBbParam {
    T _literal;

    /// <summary>What <see cref="Value"/> holds, and whether it has been read yet.</summary>
    T _value;
    bool _refreshed;
    Blackboard _blackboard;

    public BbParam() { }

    public BbParam(T literal) => _literal = literal;

    /// <summary>
    /// The value as of the host's last refresh. Assigning writes it through <see cref="Set"/> right
    /// away, so the blackboard and every later reader see it too. Before the host first runs — in
    /// the editor, say — it is the fixed value.
    /// </summary>
    public T Value {
        get => _refreshed ? _value : _literal;
        set => Set(_blackboard, value);
    }

    void IBbParam.Refresh(Blackboard blackboard) {
        _blackboard = blackboard;
        _value = Get(blackboard);
        _refreshed = true;
    }

    /// <summary>Lets a fixed starting value be written as just the value: <c>= 4f</c>.</summary>
    public static implicit operator BbParam<T>(T literal) => new(literal);

    /// <summary>The fixed value. Also what <see cref="Get"/> falls back on when a linked entry holds nothing.</summary>
    public T Literal {
        get => _literal;
        set => _literal = value;
    }

    public string EntryId { get; set; } = "";
    public string EntryName { get; set; } = "";
    public bool IsLinked => !string.IsNullOrEmpty(EntryId);

    public T Get(Blackboard blackboard) {
        if (IsLinked && blackboard != null && blackboard.TryGetById(EntryId, out var value)
            && BbTypes.TryConvert<T>(value, out var typed)) {
            return typed;
        }
        return _literal;
    }

    /// <summary>
    /// Writes to the linked entry. An unlinked parameter keeps the value itself instead, which is
    /// private to the instance — every instance runs its own copy of the host.
    /// </summary>
    public void Set(Blackboard blackboard, T value) {
        if (IsLinked && blackboard != null) blackboard.SetById(EntryId, Variant.From(value));
        else _literal = value;
        if (_refreshed) _value = value;
    }

    public override string ToString() => IsLinked ? EntryName : BbTypes.Format(Variant.From(_literal));

    Type IBbParam.ValueType => typeof(T);

    Variant IBbParam.Literal {
        get => Variant.From(_literal);
        set {
            if (BbTypes.TryConvert<T>(value, out var typed)) _literal = typed;
        }
    }
}
