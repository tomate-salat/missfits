#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misswire.Editor;

/// <summary>A wire drawn dashed: something that holds without having been wired by hand.</summary>
public readonly record struct DashedWire(string From, int FromPort, string To, int ToPort);

/// <summary>
/// Draws the wires GraphEdit cannot: dashed ones, for what misswire finds by itself — the change
/// signal a property binding follows by its name. They run where a plain wire between the same
/// ports would, beneath the boxes, and are redrawn every frame while there are any, because
/// scrolling, zooming or moving a box moves them. Kept in Godot-friendly arrays for a reload.
/// </summary>
[Tool]
public partial class WireDashOverlay : Control {
    string[] _from = [];
    int[] _fromPort = [];
    string[] _to = [];
    int[] _toPort = [];

    int Count => Mathf.Min(Mathf.Min(_from.Length, _fromPort.Length), Mathf.Min(_to.Length, _toPort.Length));

    public IReadOnlyList<DashedWire> Wires => [.. Enumerable.Range(0, Count).Select(i => new DashedWire(_from[i], _fromPort[i], _to[i], _toPort[i]))];

    public override void _Ready() {
        Name = "DashOverlay";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        SetProcess(false);
    }

    public void ShowWires(IEnumerable<DashedWire> wires) {
        var list = wires.ToList();
        _from = [.. list.Select(w => w.From)];
        _fromPort = [.. list.Select(w => w.FromPort)];
        _to = [.. list.Select(w => w.To)];
        _toPort = [.. list.Select(w => w.ToPort)];
        SetProcess(Count > 0);
        QueueRedraw();
    }

    public override void _Process(double delta) {
        if (IsVisibleInTree()) QueueRedraw();
    }

    public override void _Draw() {
        if (GetParent() is not GraphEdit graph) return;
        var zoom = graph.Zoom;
        var width = Mathf.Max(1f, graph.ConnectionLinesThickness * zoom * 0.75f);
        var color = new Color(WireBox.SignalColor, 0.75f);

        foreach (var wire in Wires) {
            var from = graph.GetNodeOrNull<GraphNode>(wire.From);
            var to = graph.GetNodeOrNull<GraphNode>(wire.To);
            if (from == null || to == null || wire.FromPort >= from.GetOutputPortCount() || wire.ToPort >= to.GetInputPortCount()) continue;

            var line = graph.GetConnectionLine(from.Position + from.GetOutputPortPosition(wire.FromPort) * zoom,
                to.Position + to.GetInputPortPosition(wire.ToPort) * zoom);
            for (var i = 1; i < line.Length; i++) DrawDashedLine(line[i - 1], line[i], color, width, 6f * zoom, antialiased: true);
        }
    }
}
#endif
