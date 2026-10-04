using System;
using System.Collections.Generic;
using Godot;
using Misscore;

namespace Missbehave;


/// <summary>
/// Several leaves of one kind folded into a single box: the graph draws the entries as rows inside
/// the list instead of as boxes of their own. Underneath they are ordinary children, so they tick,
/// clone, save and report to the debugger exactly like the children of a sequence or selector.
/// </summary>
[GlobalClass, Tool]
public abstract partial class AListNode : ACompositeNode {
    [Export]
    public ListMode Mode { get; set; } = ListMode.Sequence;

    /// <summary>The kind of leaf this list holds.</summary>
    public abstract Type EntryType { get; }

    public bool Accepts(MissNode node) => node != null && EntryType.IsInstanceOfType(node);

    protected override MissStatus Tick(MissContext ctx) {
        // The entry that ends the run: a failure for a sequence, a success for a selector.
        var decisive = Mode == ListMode.Sequence ? MissStatus.Failure : MissStatus.Success;
        var start = RunningChild < 0 ? 0 : RunningChild;

        for (var i = start; i < Children.Count; i++) {
            var status = TickChild(i, ctx);

            if (status == MissStatus.Running) {
                RunningChild = i;
                return MissStatus.Running;
            }
            if (status == decisive) {
                RunningChild = -1;
                return decisive;
            }
        }

        RunningChild = -1;
        return Mode == ListMode.Sequence ? MissStatus.Success : MissStatus.Failure;
    }

    public override string[] GetConfigurationWarnings() {
        var warnings = new List<string>(base.GetConfigurationWarnings());
        for (var i = 0; i < Children.Count; i++) {
            if (Children[i] != null && !Accepts(Children[i])) {
                warnings.Add($"entry #{i + 1} ({Children[i].GetLabel()}) is not a {EntryType.Name}");
            }
        }
        return [.. warnings];
    }
}
