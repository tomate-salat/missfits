using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misscore;

/// <summary>
/// A waypoint for wires in a graph editor: wires may lead to it instead of to what they are really
/// after, and it leads on — to that, or to another reroute. It does nothing at runtime: whatever
/// leads to a reroute goes straight to what stands at the end of the chain (<see cref="Resolve"/>).
/// </summary>
[GlobalClass, Tool]
public partial class MissReroute : MissResource {
    /// <summary>Stable identity, which is what wires and other reroutes refer to.</summary>
    [Export]
    public string Id { get; set; } = BlackboardEntry.NewId();

    /// <summary>Where it leads on, by id: the real target, or another reroute.</summary>
    [Export]
    public string TargetId { get; set; } = "";

    /// <summary>Authored position in a graph editor.</summary>
    [Export]
    public Vector2 GraphPosition { get; set; }

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(Id) or nameof(TargetId) or nameof(GraphPosition)) {
            property["usage"] = (int) PropertyUsageFlags.Storage;
        }
    }

    public static MissReroute Find(IEnumerable<MissReroute> reroutes, string id)
        => string.IsNullOrEmpty(id) ? null : reroutes?.FirstOrDefault(reroute => reroute?.Id == id);

    /// <summary>
    /// The id a target really stands for: itself, if it names no reroute, else whatever the chain of
    /// reroutes starting there ends at. Empty when the chain ends nowhere or runs in a circle.
    /// </summary>
    public static string Resolve(IEnumerable<MissReroute> reroutes, string targetId) {
        var all = reroutes?.Where(reroute => reroute != null).ToList() ?? [];
        for (var hops = 0; hops <= all.Count; hops++) {
            if (Find(all, targetId) is not { } reroute) return targetId ?? "";
            targetId = reroute.TargetId;
        }
        return "";
    }

    /// <summary>Whether leading <paramref name="reroute"/> on to that target would send its wires round in a circle.</summary>
    public static bool WouldLoop(IEnumerable<MissReroute> reroutes, MissReroute reroute, string targetId) {
        var all = reroutes?.Where(candidate => candidate != null).ToList() ?? [];
        for (var hops = 0; hops <= all.Count; hops++) {
            if (Find(all, targetId) is not { } next) return false;
            if (ReferenceEquals(next, reroute)) return true;
            targetId = next.TargetId;
        }
        return true;
    }
}
