using System.Collections.Generic;

namespace Misscore;

/// <summary>
/// A resource that declares blackboard entries and holds the objects whose parameters link to them —
/// a behavior tree with its nodes, for one. Implemented by a <c>Resource</c>: the blackboard editor
/// records its edits against it and emits its <c>changed</c> signal.
/// </summary>
public interface IBlackboardSource {
    Godot.Collections.Array<BlackboardEntry> Blackboard { get; set; }

    /// <summary>Every object whose <see cref="BbParam{T}"/> members may link to these entries.</summary>
    IEnumerable<IBbParamHost> ParamHosts();
}

/// <summary>
/// An object with <see cref="BbParam{T}"/> members, known within its <see cref="IBlackboardSource"/>
/// by a stable id. Implemented by a <c>GodotObject</c>.
/// </summary>
public interface IBbParamHost {
    string Id { get; }
}

public static class BlackboardSourceExtensions {
    public static BlackboardEntry FindEntry(this IBlackboardSource source, string id) {
        if (source?.Blackboard == null || string.IsNullOrEmpty(id)) return null;
        foreach (var entry in source.Blackboard) {
            if (entry?.Id == id) return entry;
        }
        return null;
    }

    public static BlackboardEntry FindEntryByName(this IBlackboardSource source, string name) {
        if (source?.Blackboard == null) return null;
        foreach (var entry in source.Blackboard) {
            if (entry != null && entry.Name == name) return entry;
        }
        return null;
    }
}
