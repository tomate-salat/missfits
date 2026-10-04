#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misstate.Editor;

/// <summary>One wire to highlight while a game runs: from an output port of one box to the input of another.</summary>
/// <param name="Taken">True for the way the machine came into its state, false for a way out it is watching.</param>
public readonly record struct LiveWire(string From, int Port, string To, bool Taken);

/// <summary>
/// Paints over the wires of a running machine: the transition it took into the state it is in, in
/// the colour of success, and the transitions out of that state — the ones being checked on every
/// tick, none of which has fired yet — in the colour of running, with dots travelling along them,
/// as the Missbehave graph shows a wire into a running node.
/// <para>
/// Sits in the GraphEdit right behind its connection layer, so it is drawn over the plain wires but
/// beneath the boxes. It draws in the graph's coordinates, ignores the mouse, and redraws every
/// frame while it shows anything, because scrolling, zooming or dragging a box moves the wires. The
/// wires are kept in Variant-compatible arrays so that an assembly reload does not lose them and
/// leave the last picture standing.
/// </para>
/// </summary>
[Tool]
public partial class FsmWireOverlay : Control {
    /// <summary>Travel speed of the dots, in graph units per second.</summary>
    const float Speed = 48f;

    /// <summary>Distance between two dots, in graph units.</summary>
    const float Spacing = 36f;

    // One entry per wire across the four arrays.
    string[] _from = [];
    int[] _port = [];
    string[] _to = [];
    bool[] _taken = [];
    float _phase;

    int Count => Mathf.Min(Mathf.Min(_from.Length, _port.Length), Mathf.Min(_to.Length, _taken.Length));

    public IReadOnlyList<LiveWire> Wires {
        get {
            var wires = new List<LiveWire>(Count);
            for (var i = 0; i < Count; i++) wires.Add(new LiveWire(_from[i], _port[i], _to[i], _taken[i]));
            return wires;
        }
    }

    public override void _Ready() {
        Name = "WireOverlay";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        SetProcess(false);
    }

    public void ShowWires(IEnumerable<LiveWire> wires) {
        var list = wires.ToList();
        _from = [.. list.Select(w => w.From)];
        _port = [.. list.Select(w => w.Port)];
        _to = [.. list.Select(w => w.To)];
        _taken = [.. list.Select(w => w.Taken)];
        SetProcess(true);
        QueueRedraw();
    }

    /// <summary>Removes every wire. Always redraws, so nothing drawn earlier can be left standing.</summary>
    public void ClearWires() {
        _from = [];
        _port = [];
        _to = [];
        _taken = [];
        QueueRedraw();
    }

    public override void _Process(double delta) {
        if (Count == 0) {
            // This last redraw paints nothing and wipes whatever is still on the canvas.
            QueueRedraw();
            SetProcess(false);
            return;
        }
        _phase = (_phase + (float) delta * Speed) % (Spacing * 1000f);
        if (IsVisibleInTree()) QueueRedraw();
    }

    public override void _Draw() {
        if (Count == 0 || GetParent() is not GraphEdit graph) return;

        var zoom = graph.Zoom;
        var thickness = Mathf.Max(1f, graph.ConnectionLinesThickness * zoom) + 1f;

        // The way in goes on top: where it shares a wire with a way out, it is the one that happened.
        foreach (var wire in Wires.OrderBy(w => w.Taken)) {
            var from = graph.GetNodeOrNull<GraphNode>(wire.From);
            var to = graph.GetNodeOrNull<GraphNode>(wire.To);
            if (from == null || to == null || wire.Port >= from.GetOutputPortCount() || to.GetInputPortCount() == 0) continue;

            // As GraphEdit places a wire that is being dragged: from where the boxes are in the view.
            var line = graph.GetConnectionLine(from.Position + from.GetOutputPortPosition(wire.Port) * zoom,
                to.Position + to.GetInputPortPosition(0) * zoom);
            if (line.Length < 2) continue;

            var color = wire.Taken ? FsmRow.Success : FsmRow.Running;
            DrawPolyline(line, color, thickness, antialiased: true);
            if (!wire.Taken) DrawDots(line, color.Lightened(0.6f), zoom, thickness);
        }
    }

    void DrawDots(Vector2[] line, Color color, float zoom, float thickness) {
        var spacing = Spacing * zoom;
        var next = _phase * zoom % spacing;
        var radius = thickness * 0.8f;
        var travelled = 0f;

        for (var i = 1; i < line.Length; i++) {
            var segment = line[i - 1].DistanceTo(line[i]);
            while (next <= travelled + segment) {
                var t = segment > 0f ? (next - travelled) / segment : 0f;
                DrawCircle(line[i - 1].Lerp(line[i], t), radius, color, antialiased: true);
                next += spacing;
            }
            travelled += segment;
        }
    }
}
#endif
