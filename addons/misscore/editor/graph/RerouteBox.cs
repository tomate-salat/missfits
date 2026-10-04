#if TOOLS
using Godot;

namespace Misscore.Editor;

/// <summary>
/// A reroute in a graph: a small pill with one port in and one out, to lead wires around the boxes.
/// Any number of wires may end at it; the one that leaves it says where they all go.
/// </summary>
[Tool]
public partial class RerouteBox : MissGraphBox {
    /// <summary>The reroute was right-clicked; the position is in screen coordinates.</summary>
    [Signal]
    public delegate void MenuRequestedEventHandler(StringName boxName, Vector2 screenPosition);

    /// <summary>
    /// Wide enough to leave a strip between the hot zones of the two ports, by which to drag it —
    /// see <see cref="MissGraphEdit.PortReach"/>.
    /// </summary>
    public static readonly Vector2 BodySize = new(44, 18);

    static readonly Color PortColor = new("#d0d3d8");

    // Untyped so an assembly reload can restore it — see ReloadSafe.
    GodotObject _reroute;

    public MissReroute Reroute => ReloadSafe.Get<MissReroute>(ref _reroute);

    bool _flipped;

    /// <summary>
    /// Turned round: wires arrive at the right end and leave from the left. GraphNode itself only
    /// knows inputs on the left and outputs on the right, so this is a flag that
    /// <see cref="MissGraphEdit"/> honours when it draws wires and decides which end was grabbed. It
    /// sets it whenever the reroute leads to something on its left, so the wires do not cross.
    /// </summary>
    public bool Flipped {
        get => _flipped;
        set {
            _flipped = value;
            QueueRedraw();
        }
    }

    /// <summary>A small chevron saying which way the wires run through.</summary>
    public override void _Draw() {
        var centre = Size / 2;
        var way = Flipped ? -1 : 1;
        DrawPolyline([centre + new Vector2(-3 * way, -4), centre + new Vector2(3 * way, 0), centre + new Vector2(-3 * way, 4)],
            new Color(PortColor, 0.6f), 1.5f, antialiased: true);
    }

    public void Bind(MissReroute reroute) {
        _reroute = reroute;
        Name = reroute.Id;
        TooltipText = "Reroute — wires that end here go on to where it leads";

        AddThemeStyleboxOverride("panel", Pill(selected: false));
        AddThemeStyleboxOverride("panel_selected", Pill(selected: true));

        AddChild(new Control { Name = "Body", CustomMinimumSize = BodySize, MouseFilter = MouseFilterEnum.Ignore });
        SetSlot(0, true, 0, PortColor, true, 0, PortColor);
        HideTitlebar();
    }

    static StyleBoxFlat Pill(bool selected) {
        var style = new StyleBoxFlat {
            BgColor = selected ? new Color("#31353c") : new Color("#2a2d32"),
            BorderColor = selected ? new Color("#8ab4f8") : new Color("#6b7078"),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(9);
        return style;
    }

    public override void _GuiInput(InputEvent @event) {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } click) return;

        EmitSignal(SignalName.MenuRequested, Name, GetScreenPosition() + click.Position);
        AcceptEvent();
    }
}
#endif
