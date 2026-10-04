#if TOOLS
using Godot;

namespace Misstate.Editor;

/// <summary>
/// One line inside a state's box: an action the state runs, a transition out of it, or — indented
/// below its transition — a condition that transition checks. A transition row's slot carries the
/// output port on the right, so every transition has a wire of its own. The row reports clicks as
/// signals and leaves what they mean to <see cref="FsmGraphEdit"/>.
/// </summary>
[Tool]
public partial class FsmRow : PanelContainer {
    /// <summary><see cref="Kind"/> of a row showing one of the state's actions.</summary>
    public const string Action = "action";

    /// <summary><see cref="Kind"/> of a row showing one of the state's transitions.</summary>
    public const string Transition = "transition";

    /// <summary><see cref="Kind"/> of a row showing one condition of the transition above it.</summary>
    public const string Condition = "condition";

    /// <summary>How far a condition is set in from its transition.</summary>
    const int ConditionIndent = 18;

    /// <summary>The row was clicked, to inspect what it shows.</summary>
    [Signal]
    public delegate void PickedEventHandler(string kind, string id);

    /// <summary>The row was right-clicked; the position is in screen coordinates, ready for a popup.</summary>
    [Signal]
    public delegate void MenuRequestedEventHandler(string kind, string id, Vector2 screenPosition);

    public string Kind { get; private set; } = "";

    /// <summary>Id of the action node, the transition or the condition node this row shows.</summary>
    public string Id { get; private set; } = "";

    Label _text;
    Label _warning;

    public void Build(string kind, string id) {
        Kind = kind;
        Id = id;
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
        _text.Text = text;
        _warning.Text = string.IsNullOrEmpty(warning) ? "" : "⚠";
        _warning.TooltipText = warning;

        var style = new StyleBoxFlat {
            BgColor = new Color(1, 1, 1, picked ? 0.12f : 0.04f),
            ContentMarginLeft = Kind == Condition ? 6 + ConditionIndent : 6,
            ContentMarginRight = 6,
            ContentMarginTop = 1,
            ContentMarginBottom = 1,
        };
        style.SetCornerRadiusAll(4);
        style.SetBorderWidthAll(1);
        style.BorderColor = picked ? new Color("#8ab4f8") : new Color(1, 1, 1, 0f);
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
