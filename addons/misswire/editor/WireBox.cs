#if TOOLS
using System.Collections.Generic;
using Godot;
using Misscore.Editor;

namespace Misswire.Editor;

/// <summary>
/// Base of the boxes in the misswire graph: a header, then one <see cref="GraphRow"/> per line.
/// GraphEdit numbers ports by enabled slot, so each box keeps which row every port belongs to.
/// </summary>
[Tool]
public abstract partial class WireBox : MissGraphBox {
    /// <summary>A row was clicked; an empty kind means the header.</summary>
    [Signal]
    public delegate void RowPickedEventHandler(StringName boxName, string kind, string id);

    /// <summary>A row was right-clicked; the position is in screen coordinates.</summary>
    [Signal]
    public delegate void RowMenuRequestedEventHandler(StringName boxName, string kind, string id, Vector2 screenPosition);

    public static readonly Color PropertyColor = new("#8ab4f8");
    public static readonly Color SignalColor = new("#e3b341");
    public static readonly Color MethodColor = new("#5dcaa5");
    protected static readonly Color Faded = new(1, 1, 1, 0.45f);

    static readonly Color Edge = new("#3b3f46");
    static readonly Color SelectedEdge = new("#8ab4f8");

    /// <summary>"kind:id" of the row behind each input and output port, in port order.</summary>
    readonly Godot.Collections.Array<string> _inputs = [];
    readonly Godot.Collections.Array<string> _outputs = [];

    Label _title;
    Label _subtitle;
    Label _warning;

    protected void BuildFrame(string title, string subtitle, string warning, Color accent) {
        AddThemeStyleboxOverride("panel", Outline(false, accent));
        AddThemeStyleboxOverride("panel_selected", Outline(true, accent));
        AddThemeConstantOverride("separation", 3);
        Theme = ZoomFonts.ThemeFor(this);
        Title = title;

        var header = new HBoxContainer { Name = "Header", CustomMinimumSize = new Vector2(160, 22), MouseFilter = MouseFilterEnum.Pass };
        header.AddThemeConstantOverride("separation", 6);
        var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        texts.AddThemeConstantOverride("separation", 0);
        _title = new Label { Name = "BoxTitle", Text = title, MouseFilter = MouseFilterEnum.Ignore };
        if (BlackboardStyles.BoldFont() is { } bold) _title.AddThemeFontOverride("font", ZoomFonts.Sharp(bold));
        _subtitle = new Label { Name = "Subtitle", Text = subtitle, Visible = !string.IsNullOrEmpty(subtitle), MouseFilter = MouseFilterEnum.Ignore };
        _subtitle.AddThemeFontSizeOverride("font_size", 11);
        _subtitle.AddThemeColorOverride("font_color", Faded);
        texts.AddChild(_title);
        texts.AddChild(_subtitle);
        _warning = new Label { Name = "Warning", MouseFilter = MouseFilterEnum.Pass };
        _warning.AddThemeColorOverride("font_color", new Color("#ffd24a"));
        header.AddChild(texts);
        header.AddChild(_warning);
        AddChild(header);
        ShowWarning(warning);
        HideTitlebar();
    }

    public string Warning => _warning.TooltipText;
    public string Subtitle => _subtitle.Text;

    protected void ShowWarning(string warning) {
        _warning.Text = string.IsNullOrEmpty(warning) ? "" : "⚠";
        _warning.TooltipText = warning ?? "";
    }

    static StyleBoxFlat Outline(bool selected, Color accent) {
        var style = new StyleBoxFlat {
            BgColor = selected ? new Color("#31353c") : new Color("#2a2d32"),
            BorderColor = selected ? SelectedEdge : Edge,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 6,
            ContentMarginBottom = 8,
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        // Node boxes and binding boxes tell themselves apart by the tint of their outline.
        if (!selected) style.BorderColor = new Color(accent, 0.45f);
        return style;
    }

    /// <summary>Adds a row; <paramref name="input"/> and <paramref name="output"/> say which ports it gets.</summary>
    /// <param name="rich">The text with BBCode, if parts of it are set off; <paramref name="text"/> is its plain version.</param>
    protected GraphRow AddRow(string kind, string id, string text, string warning, bool input, Color inputColor, bool output, Color outputColor,
                              string rich = null) {
        var row = new GraphRow();
        row.Build(kind, id);
        row.Show(text, warning, picked: false);
        if (rich != null) row.ShowRich(rich, text);
        row.Connect(GraphRow.SignalName.Picked, new Callable(this, MethodName.OnRowPicked));
        row.Connect(GraphRow.SignalName.MenuRequested, new Callable(this, MethodName.OnRowMenu));
        AddChild(row);
        SetPorts(row.GetIndex(), $"{kind}:{id}", input, inputColor, output, outputColor);
        return row;
    }

    protected void SetPorts(int slot, string key, bool input, Color inputColor, bool output, Color outputColor) {
        SetSlot(slot, input, 0, inputColor, output, 0, outputColor);
        if (input) _inputs.Add(key);
        if (output) _outputs.Add(key);
    }

    /// <summary>Registers an input added by hand with SetSlotEnabledLeft, in port order.</summary>
    protected void AddInput(string key) => _inputs.Add(key);

    public int InputPort(string kind, string id) => _inputs.IndexOf($"{kind}:{id}");
    public int OutputPort(string kind, string id) => _outputs.IndexOf($"{kind}:{id}");

    /// <summary>The row behind a port, as kind and id; nulls for a port that does not exist.</summary>
    public (string Kind, string Id) AtInput(int port) => Split(_inputs, port);
    public (string Kind, string Id) AtOutput(int port) => Split(_outputs, port);

    static (string, string) Split(Godot.Collections.Array<string> keys, int port) {
        if (port < 0 || port >= keys.Count) return (null, null);
        var key = keys[port];
        var colon = key.IndexOf(':');
        return (key[..colon], key[(colon + 1)..]);
    }

    public IEnumerable<GraphRow> Rows() {
        foreach (var child in GetChildren()) {
            if (child is GraphRow row) yield return row;
        }
    }

    /// <summary>Shows which row is the one in the Inspector, if any is in this box.</summary>
    public void ShowPicked(string kind, string id) {
        foreach (var row in Rows()) row.Show(row.Text, row.Warning, row.Kind == kind && row.Id == id);
    }

    void OnRowPicked(string kind, string id) => EmitSignal(SignalName.RowPicked, Name, kind, id);

    void OnRowMenu(string kind, string id, Vector2 screenPosition) => EmitSignal(SignalName.RowMenuRequested, Name, kind, id, screenPosition);

    public override void _GuiInput(InputEvent @event) {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } click) {
            EmitSignal(SignalName.RowMenuRequested, Name, "", "", GetScreenPosition() + click.Position * GetGlobalTransformWithCanvas().Scale);
            AcceptEvent();
        }
    }
}

#endif
