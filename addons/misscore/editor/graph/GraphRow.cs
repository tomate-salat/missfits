#if TOOLS
using Godot;

namespace Misscore.Editor;

/// <summary>
/// One line inside a box of a graph: a text, a warning sign with what is wrong as its tooltip, and
/// the looks of "picked" and of a live status. What a row stands for is up to the editor that uses
/// it — it tells its rows apart by <see cref="Kind"/> and <see cref="Id"/>. The row reports clicks
/// as signals and leaves what they mean to the graph.
/// </summary>
[Tool]
public partial class GraphRow : PanelContainer {
    /// <summary>The row was clicked, to inspect what it shows.</summary>
    [Signal]
    public delegate void PickedEventHandler(string kind, string id);

    /// <summary>The row was right-clicked; the position is in screen coordinates, ready for a popup.</summary>
    [Signal]
    public delegate void MenuRequestedEventHandler(string kind, string id, Vector2 screenPosition);

    /// <summary>What sort of thing the row shows, in the words of the editor that made it.</summary>
    public string Kind { get; private set; } = "";

    /// <summary>Id of what the row shows.</summary>
    public string Id { get; private set; } = "";

    /// <summary>How far the row's text is set in, for rows that belong to the one above them.</summary>
    public int Indent { get; private set; }

    Label _text;
    Label _warning;
    bool _picked;

    /// <summary>What a running game last reported for this row, or -1 for nothing.</summary>
    int _status = -1;

    public static readonly Color Success = new("#3fb950");
    public static readonly Color Failure = new("#f85149");
    public static readonly Color Running = new("#e3b341");

    public static Color ColorOf(MissStatus status) => status switch {
        MissStatus.Success => Success,
        MissStatus.Failure => Failure,
        _ => Running,
    };

    /// <summary>The status a running game reported for this row, or null when there is none to show.</summary>
    public MissStatus? LiveStatus => _status < 0 ? null : (MissStatus) _status;

    /// <summary>Tints the row with a live status, or takes the tint away again.</summary>
    public void ShowStatus(MissStatus? status) {
        var value = status == null ? -1 : (int) status.Value;
        if (value == _status) return;
        _status = value;
        Show(_text.Text, _warning.TooltipText, _picked);
    }

    public void Build(string kind, string id, int indent = 0) {
        Kind = kind;
        Id = id;
        Indent = indent;
        Name = $"{kind}_{id}";
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(0, 20);

        var line = new HBoxContainer { Name = "Line", MouseFilter = MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 6);
        _text = new Label { Name = "Text", SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        _text.AddThemeFontSizeOverride("font_size", 12);
        _warning = new Label { Name = "Warning", MouseFilter = MouseFilterEnum.Pass };
        _warning.AddThemeColorOverride("font_color", new Color("#ffd24a"));
        line.AddChild(_text);
        line.AddChild(_warning);
        AddChild(line);

        Show("", "", picked: false);
    }

    public string Text => _text.Text;

    public string Warning => _warning.TooltipText;

    public void Show(string text, string warning, bool picked) {
        _picked = picked;
        _text.Text = text;
        _warning.Text = string.IsNullOrEmpty(warning) ? "" : "⚠";
        _warning.TooltipText = warning;

        var style = new StyleBoxFlat {
            BgColor = LiveStatus is { } live ? new Color(ColorOf(live), 0.25f) : new Color(1, 1, 1, picked ? 0.12f : 0.04f),
            ContentMarginLeft = 6 + Indent,
            ContentMarginRight = 6,
            ContentMarginTop = 1,
            ContentMarginBottom = 1,
        };
        style.SetCornerRadiusAll(4);
        style.SetBorderWidthAll(1);
        style.BorderColor = picked ? new Color("#8ab4f8") : LiveStatus is { } status ? ColorOf(status) : new Color(1, 1, 1, 0f);
        AddThemeStyleboxOverride("panel", style);
    }

    public override void _GuiInput(InputEvent @event) {
        if (@event is not InputEventMouseButton { Pressed: true } click) return;

        if (click.ButtonIndex == MouseButton.Left) {
            EmitSignal(SignalName.Picked, Kind, Id);
        }
        else if (click.ButtonIndex == MouseButton.Right) {
            EmitSignal(SignalName.Picked, Kind, Id);
            EmitSignal(SignalName.MenuRequested, Kind, Id, GetScreenPosition() + click.Position);
            AcceptEvent();
        }
    }
}
#endif
