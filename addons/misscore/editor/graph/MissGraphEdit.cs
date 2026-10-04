#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misscore.Editor;

/// <summary>
/// What the Missfits graph editors that flow from left to right have in common — the state machine,
/// the dialogue: boxes whose rows carry the output ports, reroutes to lead wires around them, wires
/// drawn with corners, and an overlay that highlights wires while a game runs. What the boxes are
/// and what an edit does to the resource is the subclass's business.
/// <para>
/// A subclass calls <c>base._Ready()</c>, hands every box it adds to <see cref="TrackMoves"/>, adds
/// its reroutes through <see cref="AddRerouteBox"/> and calls <see cref="UpdateFlips"/> once the
/// graph is built.
/// </para>
/// <para>
/// Reload safety, as everywhere in the Missfits editors: signals are connected with native method
/// callables and references to the addons' own classes are stored untyped (see <see cref="ReloadSafe"/>).
/// </para>
/// </summary>
[Tool]
public abstract partial class MissGraphEdit : GraphEdit {
    /// <summary>
    /// How far into a box a port can be grabbed. GraphEdit's own reach would cover a reroute from
    /// end to end, leaving nowhere to drag it by.
    /// </summary>
    public const int PortReach = 10;

    /// <summary>How close to a wire a click has to be to mean that wire.</summary>
    protected const float WireReach = 10f;

    /// <summary>How far a wire runs straight out of a port, and into one, before it may turn.</summary>
    const float WireStub = 18f;

    /// <summary>How far apart the wires of one box turn.</summary>
    const float WireLane = 8f;

    GodotObject _wireOverlay;

    /// <summary>Paints the wires of whatever a running game is in. See <see cref="LiveWireOverlay"/>.</summary>
    public LiveWireOverlay WireOverlay => ReloadSafe.Get<LiveWireOverlay>(ref _wireOverlay);

    public override void _Ready() {
        AddThemeConstantOverride("port_hotzone_inner_extent", PortReach);

        // An ordinary child, not an internal one — see PlaceWireOverlay.
        var overlay = new LiveWireOverlay();
        _wireOverlay = overlay;
        AddChild(overlay);
        PlaceWireOverlay();
    }

    // ---- boxes -------------------------------------------------------------------------------

    protected IEnumerable<RerouteBox> RerouteBoxes() => GetChildren().OfType<RerouteBox>();

    public RerouteBox RerouteBoxFor(string rerouteId)
        => string.IsNullOrEmpty(rerouteId) ? null : RerouteBoxes().FirstOrDefault(b => b.Name == rerouteId);

    /// <summary>The boxes that are not reroutes: the ones with rows.</summary>
    IEnumerable<GraphNode> RowBoxes() => GetChildren().OfType<GraphNode>().Where(box => box is not RerouteBox);

    /// <summary>Whether a wire can end at this id: something that has a box, reroutes included.</summary>
    protected bool HasBox(string id) => !string.IsNullOrEmpty(id) && GetChildren().OfType<GraphNode>().Any(box => box.Name == id);

    /// <summary>Adds the box of a reroute, where the reroute says it sits.</summary>
    protected RerouteBox AddRerouteBox(MissReroute reroute) {
        // Named before entering the tree so it can never collide with a leftover sibling.
        var box = new RerouteBox { Name = reroute.Id };
        AddChild(box);
        box.Bind(reroute);
        box.Connect(RerouteBox.SignalName.MenuRequested, new Callable(this, MethodName.OnRerouteMenuRequested));
        box.Connect(RerouteBox.SignalName.Activated, new Callable(this, MethodName.JumpFrom));
        TrackMoves(box);
        box.PositionOffset = reroute.GraphPosition;
        return box;
    }

    /// <summary>Has the reroutes turn to face what they lead to whenever this box moves.</summary>
    protected void TrackMoves(GraphElement box)
        => box.Connect(GraphElement.SignalName.PositionOffsetChanged, new Callable(this, MethodName.OnBoxMoved));

    /// <summary>
    /// Whether the wire that leaves a reroute is to be drawn: not for a port, which names its target
    /// instead. A subclass asks before it connects a reroute to what it leads to.
    /// </summary>
    protected static bool IsWired(MissReroute reroute) => reroute != null && !reroute.Wireless;

    /// <summary>Scrolls to where a port leads, so that its target sits in the middle of the view.</summary>
    public void JumpFrom(StringName boxName) {
        if (RerouteBoxFor(boxName) is not { IsPort: true } port) return;

        var target = GetChildren().OfType<GraphNode>().FirstOrDefault(box => box.Name == port.Reroute.TargetId);
        if (target != null) ScrollOffset = (target.PositionOffset + target.Size / 2) * Zoom - Size / 2;
    }

    void OnRerouteMenuRequested(StringName boxName, Vector2 screenPosition) => RerouteMenuRequested(boxName, screenPosition);

    /// <summary>A reroute was right-clicked; the position is in screen coordinates.</summary>
    protected virtual void RerouteMenuRequested(StringName boxName, Vector2 screenPosition) { }

    // ---- where the selection leads -----------------------------------------------------------

    /// <summary>
    /// Marks the box a target stands for — through any reroutes and ports — as where the selection
    /// leads, and no other. With a selected reroute or port and no id given, it is that one's target.
    /// </summary>
    protected void HintTarget(string targetId) {
        if (string.IsNullOrEmpty(targetId)) targetId = RerouteBoxes().FirstOrDefault(box => box.Selected)?.Reroute?.TargetId;

        for (var hops = RerouteBoxes().Count(); hops >= 0 && RerouteBoxFor(targetId)?.Reroute is { } reroute; hops--) targetId = reroute.TargetId;
        foreach (var box in GetChildren().OfType<MissGraphBox>()) box.Hinted = !string.IsNullOrEmpty(targetId) && box is not RerouteBox && box.Name == targetId;
    }

    // ---- wires under the mouse ---------------------------------------------------------------

    /// <summary>The wire at a position in the graph's own coordinates, as the box and port it leaves.</summary>
    protected bool TryWireAt(Vector2 position, out StringName fromNode, out int fromPort) {
        var wire = GetClosestConnectionAtPoint(position, WireReach);
        fromNode = wire.Count > 0 ? wire["from_node"].AsStringName() : null;
        fromPort = wire.Count > 0 ? wire["from_port"].AsInt32() : 0;
        return fromNode != null;
    }

    public override void _GuiInput(InputEvent @event) {
        if (@event is not InputEventMouseButton { Pressed: true, DoubleClick: true, ButtonIndex: MouseButton.Left } click) return;
        if (!TryWireAt(click.Position, out var fromNode, out var fromPort)) return;

        WireDoubleClicked((click.Position + ScrollOffset) / Zoom, fromNode, fromPort);
        AcceptEvent();
    }

    /// <summary>A wire was double-clicked, at this position on the canvas; it leaves the given box at the given port.</summary>
    protected virtual void WireDoubleClicked(Vector2 position, StringName fromNode, int fromPort) { }

    // ---- reroutes that turn round ------------------------------------------------------------

    // GraphEdit only knows wires that leave a box on the right and arrive on the left. A reroute
    // that leads back to the left is turned round instead (RerouteBox.Flipped), which takes three
    // things: knowing when, drawing its wires from the other end, and grabbing them there.

    bool _updatingFlips;

    void OnBoxMoved() => UpdateFlips();

    /// <summary>Turns every reroute to face what it leads to.</summary>
    protected void UpdateFlips() {
        if (_updatingFlips) return;
        _updatingFlips = true;
        foreach (var box in RerouteBoxes().ToList()) {
            var flipped = LeadsLeft(box);
            if (flipped == box.Flipped) continue;

            box.Flipped = flipped;
            // GraphEdit works out the wires of a box again when it has moved; this asks for just that.
            box.EmitSignal(GraphElement.SignalName.PositionOffsetChanged);
        }
        _updatingFlips = false;
    }

    bool LeadsLeft(RerouteBox box) {
        // A port has no wire to keep from crossing.
        var targetId = box.Reroute?.TargetId;
        if (string.IsNullOrEmpty(targetId) || box.IsPort) return false;

        if (RerouteBoxFor(targetId) is { } next) return next.PositionOffset.X < box.PositionOffset.X;
        var target = RowBoxes().FirstOrDefault(candidate => candidate.Name == targetId);
        return target != null && target.PositionOffset.X < box.PositionOffset.X + RerouteBox.BodySize.X / 2;
    }

    // ---- drawing wires -----------------------------------------------------------------------

    /// <summary>
    /// Wires are drawn with corners instead of GraphEdit's curves. A wire leaves a flipped reroute
    /// at its left end heading left, and arrives at one at its right end. The ends are told by
    /// where they are: GraphEdit passes nothing but the two positions.
    /// </summary>
    public override Vector2[] _GetConnectionLine(Vector2 fromPosition, Vector2 toPosition) {
        var (leaves, arrives) = (1f, 1f);
        foreach (var box in RerouteBoxes()) {
            if (!box.Flipped) continue;

            var left = box.GetInputPortPosition(0) * Zoom;
            var right = box.GetOutputPortPosition(0) * Zoom;
            // Wires that exist are measured from the graph's origin, one being dragged from the view's.
            foreach (var origin in new[] { box.PositionOffset * Zoom, box.Position }) {
                if (leaves > 0 && fromPosition.DistanceSquaredTo(origin + right) < 1f) (fromPosition, leaves) = (origin + left, -1f);
                if (arrives > 0 && toPosition.DistanceSquaredTo(origin + left) < 1f) (toPosition, arrives) = (origin + right, -1f);
            }
        }

        return ElbowLine(fromPosition, leaves, toPosition, arrives, WireStub * Zoom, LaneOf(fromPosition, toPosition) * WireLane * Zoom);
    }

    /// <summary>
    /// Which lane a wire out of a box takes, so that the wires of one box turn side by side instead
    /// of on top of each other: the port nearest to where the wire is going turns first.
    /// </summary>
    int LaneOf(Vector2 fromPosition, Vector2 toPosition) {
        foreach (var box in RowBoxes()) {
            var ports = box.GetOutputPortCount();
            for (var port = 0; port < ports; port++) {
                var at = box.GetOutputPortPosition(port) * Zoom;
                if (fromPosition.DistanceSquaredTo(box.PositionOffset * Zoom + at) >= 1f && fromPosition.DistanceSquaredTo(box.Position + at) >= 1f) continue;
                return toPosition.Y > fromPosition.Y ? ports - 1 - port : port;
            }
        }
        return 0;
    }

    /// <summary>
    /// A wire of horizontal and vertical runs only, as in the Missbehave graph. It leaves its port
    /// heading <paramref name="leaves"/> (1 for right, -1 for left) and arrives heading
    /// <paramref name="arrives"/>, with a straight bit at either end so it never turns right at a port.
    /// </summary>
    /// <param name="lane">How much further than the stub this wire runs before its first turn.</param>
    static Vector2[] ElbowLine(Vector2 from, float leaves, Vector2 to, float arrives, float stub, float lane = 0) {
        var start = from + new Vector2(leaves * (stub + lane), 0);
        var end = to - new Vector2(arrives * stub, 0);
        var points = new List<Vector2> { from };

        if (leaves != arrives) {
            // Out and back in from the same side: one turn-round, past whichever end sticks out further.
            var x = leaves > 0 ? Mathf.Max(start.X, end.X) : Mathf.Min(start.X, end.X);
            points.Add(new Vector2(x, from.Y));
            points.Add(new Vector2(x, to.Y));
        }
        else if ((end.X - start.X) * leaves >= 0) {
            // The target lies ahead: one step up or down, close to where the wire starts — halfway
            // would often be behind a box that sits in between.
            points.Add(start);
            points.Add(new Vector2(start.X, to.Y));
        }
        else {
            // The target lies behind: out, back across above both ends — a box's input sits at its
            // top, so that clears the boxes — and in from the far side.
            var y = Mathf.Min(from.Y, to.Y) - 3 * stub - lane;
            points.Add(start);
            points.Add(new Vector2(start.X, y));
            points.Add(new Vector2(end.X, y));
            points.Add(end);
        }

        points.Add(to);
        return [.. points.Where((point, i) => i == 0 || !point.IsEqualApprox(points[i - 1]))];
    }

    // ---- grabbing ports ----------------------------------------------------------------------

    public override bool _IsInInputHotzone(GodotObject inNode, int inPort, Vector2 mousePosition)
        => inNode is GraphNode node && InHotzone(node, inPort, mousePosition, input: true);

    public override bool _IsInOutputHotzone(GodotObject inNode, int inPort, Vector2 mousePosition)
        => inNode is GraphNode node && InHotzone(node, inPort, mousePosition, input: false);

    /// <summary>
    /// Whether the mouse is where a port can be grabbed — as GraphEdit decides it, but with the ends
    /// of a flipped reroute swapped. The mouse position comes divided by the zoom.
    /// </summary>
    bool InHotzone(GraphNode node, int port, Vector2 mouse, bool input) {
        var reroute = node as RerouteBox;
        var onLeft = input != (reroute?.Flipped ?? false);
        if (port < 0 || port >= (input ? node.GetInputPortCount() : node.GetOutputPortCount())) return false;

        // Only a reroute is ever flipped, and it has the one port at either end.
        var local = onLeft ? node.GetInputPortPosition(input ? port : 0) : node.GetOutputPortPosition(input ? 0 : port);
        var at = (local * Zoom + node.Position) / Zoom;
        var inner = GetThemeConstant("port_hotzone_inner_extent");
        var outer = GetThemeConstant("port_hotzone_outer_extent");
        var height = node.GetThemeIcon("port")?.GetHeight() ?? 10;
        var zone = new Rect2(at.X - (onLeft ? outer : inner), at.Y - height / 2f, inner + outer, height);
        if (!zone.HasPoint(mouse)) return false;

        // What a box shows wins over the ports beside it: a click on a row is a click on that row.
        foreach (var box in RowBoxes()) {
            var within = (mouse * Zoom - box.Position) / Zoom;
            if (!new Rect2(Vector2.Zero, box.Size).HasPoint(within)) continue;
            if (box.GetChildren().OfType<Control>().Any(child => child.Visible && child.GetRect().HasPoint(within))) return false;
        }
        return true;
    }

    // ---- live wires --------------------------------------------------------------------------

    /// <summary>
    /// Adds the wire from a port to its target to the wires to highlight, and with it every wire on
    /// from there through the reroutes.
    /// </summary>
    protected void FollowWire(List<LiveWire> wires, string from, int port, string targetId, bool taken) {
        for (var hops = RerouteBoxes().Count(); hops >= 0 && HasBox(targetId); hops--) {
            wires.Add(new LiveWire(from, port, targetId, taken));
            // Through ports as well: while a game runs, the wire a port hides is drawn after all, so
            // that the way the game takes can be followed.
            if (RerouteBoxFor(targetId)?.Reroute is not { } reroute) return;
            (from, port, targetId) = (reroute.Id, 0, reroute.TargetId);
        }
    }

    /// <summary>
    /// Keeps the highlighted wires on top of the plain ones but beneath the boxes. GraphEdit draws
    /// its wires in <c>_connection_layer</c>, an ordinary first child, so the overlay is an ordinary
    /// child right after it; boxes are added behind it, never in front.
    /// </summary>
    protected void PlaceWireOverlay() {
        var overlay = WireOverlay;
        var layer = GetChildren().FirstOrDefault(c => c.Name == "_connection_layer");
        if (overlay == null || layer == null || overlay.GetParent() != this) return;
        if (overlay.GetIndex() != layer.GetIndex() + 1) MoveChild(overlay, layer.GetIndex() + 1);
    }
}
#endif
