#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;

namespace Missbehave.Editor;

/// <summary>One wire the running tree went through on its last tick, from parent box to child box.</summary>
public readonly record struct LiveFlow(string From, string To, MissStatus Status);

/// <summary>
/// Paints the wires the running tree went through on its last tick in the colour of the box they
/// lead to, and lets dots travel from parent to child on the wires into running nodes — so the path
/// a tick takes, and where it is still busy, can be followed at a glance.
/// <para>
/// Sits in the GraphEdit right behind its connection layer, so it is drawn over the plain wires but
/// beneath the boxes (see <see cref="BehaviorTreeGraphEdit"/>). It draws in the graph's coordinates and ignores
/// the mouse, so editing is unaffected. It redraws every frame while live statuses are shown.
/// </para>
/// <para>
/// What it draws is whatever the canvas last received, and that stays on screen until the next redraw.
/// So redrawing is only ever switched off right after a redraw that painted nothing — otherwise the
/// last picture would freeze in place and no longer follow zooming or scrolling. The flows are kept in
/// Variant-compatible arrays for the same reason: an assembly reload keeps those, whereas a plain C#
/// list would come back empty and leave the old picture behind.
/// </para>
/// </summary>
[Tool]
public partial class ConnectionFlowOverlay : Control {
    /// <summary>Travel speed of the dots, in graph units per second.</summary>
    const float Speed = 48f;

    /// <summary>Distance between two dots, in graph units.</summary>
    const float Spacing = 22f;

    // One entry per flow across the three arrays.
    string[] _from = [];
    string[] _to = [];
    int[] _status = [];
    float _phase;

    int FlowCount => Mathf.Min(_from.Length, Mathf.Min(_to.Length, _status.Length));

    public IReadOnlyList<LiveFlow> Flows {
        get {
            var flows = new List<LiveFlow>(FlowCount);
            for (var i = 0; i < FlowCount; i++) flows.Add(new LiveFlow(_from[i], _to[i], (MissStatus) _status[i]));
            return flows;
        }
    }

    /// <summary>How far the dots have travelled; exposed so a test can see the animation advance.</summary>
    public float Phase => _phase;

    public override void _Ready() {
        Name = "ConnectionFlowOverlay";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        SetProcess(false);
    }

    /// <summary>True while at least one wire leads to a running node, i.e. while dots are moving.</summary>
    public bool Animating {
        get {
            for (var i = 0; i < FlowCount; i++) {
                if (_status[i] == (int) MissStatus.Running) return true;
            }
            return false;
        }
    }

    public void ShowFlows(IEnumerable<LiveFlow> flows) {
        var list = flows.ToList();
        _from = [.. list.Select(f => f.From)];
        _to = [.. list.Select(f => f.To)];
        _status = [.. list.Select(f => (int) f.Status)];
        SetProcess(true);
        QueueRedraw();
    }

    /// <summary>Removes every flow. Always redraws, so nothing drawn earlier can be left standing.</summary>
    public void ClearFlows() {
        _from = [];
        _to = [];
        _status = [];
        QueueRedraw();
    }

    /// <summary>
    /// Redraws every frame while anything is shown, not only while dots move: zooming, scrolling or
    /// dragging a box moves the wires, and a still wire drawn once would be left behind.
    /// </summary>
    public override void _Process(double delta) {
        if (FlowCount == 0) {
            // This last redraw paints nothing and wipes whatever is still on the canvas.
            QueueRedraw();
            SetProcess(false);
            return;
        }
        if (Animating) _phase = (_phase + (float) delta * Speed) % (Spacing * 1000f);
        if (IsVisibleInTree()) QueueRedraw();
    }

    public override void _Draw() {
        if (FlowCount == 0 || Graph() is not { } graph) return;

        // Wires are computed in the graph's space; normally that is this overlay's space too.
        var toLocal = GetGlobalTransform().AffineInverse() * graph.GetGlobalTransform();
        var zoom = graph.Zoom;
        var thickness = Mathf.Max(1f, graph.ConnectionLinesThickness * zoom);

        foreach (var flow in Flows) {
            var from = graph.GetNodeOrNull<BehaviorTreeGraphNode>(flow.From);
            var to = graph.GetNodeOrNull<BehaviorTreeGraphNode>(flow.To);
            if (from == null || to == null) continue;

            // Anchors are unscaled and relative to their box; the box's transform carries the graph's
            // zoom and scroll, which lands them in the graph's — and so this overlay's — space.
            var start = from.GetTransform() * from.OutputAnchor;
            var end = to.GetTransform() * to.InputAnchor;
            var line = graph.GetConnectionLine(start, end);
            if (line.Length < 2) continue;
            for (var i = 0; i < line.Length; i++) line[i] = toLocal * line[i];

            // The wire takes the target box's colour outright, a little wider than the plain wire so
            // none of that shows at the edges.
            var color = GraphNodeStyles.ColorFor(flow.Status);
            DrawPolyline(line, color, thickness + 1f, antialiased: true);
            // Only a running child moves; a finished one is already fully told by its colour. The dots
            // are lighter than the wire, otherwise they would vanish into it.
            if (flow.Status == MissStatus.Running) DrawDots(line, color.Lightened(0.6f), zoom, thickness);
        }
    }

    GraphEdit Graph() {
        for (var node = GetParent(); node != null; node = node.GetParent()) {
            if (node is GraphEdit graph) return graph;
        }
        return null;
    }

    void DrawDots(Vector2[] line, Color color, float zoom, float thickness) {
        var spacing = Spacing * zoom;
        var next = _phase * zoom % spacing;
        var radius = thickness * 1.25f;
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
