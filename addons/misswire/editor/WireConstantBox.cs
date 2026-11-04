#if TOOLS
using System.Globalization;
using Godot;

namespace Misswire.Editor;

/// <summary>
/// A fixed value in the graph: its type and the value, typed right into the box, and an output to
/// wire into the value port of steps.
/// </summary>
[Tool]
public partial class WireConstantBox : WireBox {
    public const string Output = "constant";

    /// <summary>The value was changed in the box.</summary>
    [Signal]
    public delegate void ValueEditedEventHandler(string constantId, Variant value);

    /// <summary>The types a constant can have, as the box offers them.</summary>
    static readonly (string Label, Variant.Type Type)[] Types = [
        ("Text", Variant.Type.String),
        ("Whole number", Variant.Type.Int),
        ("Number", Variant.Type.Float),
        ("Yes / no", Variant.Type.Bool),
        ("Colour", Variant.Type.Color),
        ("Node", Variant.Type.NodePath),
    ];

    public string ConstantId { get; private set; } = "";

    Variant _value;
    Control _editor;

    /// <summary>A node of the scene was picked for a constant of type Node; the path is as the node selector gives it.</summary>
    [Signal]
    public delegate void NodePickedEventHandler(string constantId, NodePath path);

    string _nodeName = "";

    /// <param name="nodeName">For a constant of type Node, the name of the node it points to, or empty.</param>
    public void Build(MissGraphConstant constant, string nodeName = "") {
        _nodeName = nodeName;
        ConstantId = constant.Id;
        _value = constant.Value;
        BuildFrame("Constant", "", "", PropertyColor);

        var line = new HBoxContainer { Name = "Line" };
        line.AddThemeConstantOverride("separation", 6);
        var type = new OptionButton { Name = "Type", FocusMode = FocusModeEnum.None, FitToLongestItem = false, CustomMinimumSize = new Vector2(70, 0) };
        type.AddThemeFontSizeOverride("font_size", 11);
        for (var i = 0; i < Types.Length; i++) type.AddItem(Types[i].Label, i);
        var index = System.Array.FindIndex(Types, t => t.Type == _value.VariantType);
        if (index >= 0) type.Select(index);
        type.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnTypeSelected));
        line.AddChild(type);

        _editor = Editor();
        line.AddChild(_editor);
        AddChild(line);
        SetPorts(line.GetIndex(), $"{Output}:", false, Colors.White, true, PropertyColor);
    }

    /// <summary>The field the value is typed into, fitting its type; for the tests.</summary>
    public Control ValueEditor => _editor;

    Control Editor() {
        switch (_value.VariantType) {
            case Variant.Type.Bool:
                var check = new CheckBox { Name = "Value", ButtonPressed = _value.AsBool(), FocusMode = FocusModeEnum.None };
                check.Connect(BaseButton.SignalName.Toggled, new Callable(this, MethodName.OnToggled));
                return check;
            case Variant.Type.Int or Variant.Type.Float:
                var whole = _value.VariantType == Variant.Type.Int;
                var spin = new SpinBox {
                    Name = "Value", MinValue = -1e9, MaxValue = 1e9, Step = whole ? 1 : 0.001, Rounded = whole,
                    Value = _value.AsDouble(), CustomMinimumSize = new Vector2(90, 0),
                };
                spin.Connect(Godot.Range.SignalName.ValueChanged, new Callable(this, MethodName.OnNumber));
                return spin;
            case Variant.Type.NodePath:
                var pick = new Button { Name = "Value", Text = _nodeName == "" ? "Pick a node" : _nodeName, CustomMinimumSize = new Vector2(120, 0), FocusMode = FocusModeEnum.None, TooltipText = "A node of this scene; the step gets the node itself" };
                pick.AddThemeFontSizeOverride("font_size", 11);
                pick.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnPickPressed));
                return pick;
            case Variant.Type.Color:
                var color = new ColorPickerButton { Name = "Value", Color = _value.AsColor(), CustomMinimumSize = new Vector2(60, 0) };
                color.Connect(ColorPickerButton.SignalName.PopupClosed, new Callable(this, MethodName.OnColorClosed));
                return color;
            default:
                var text = new LineEdit { Name = "Value", Text = _value.AsString(), CustomMinimumSize = new Vector2(120, 0), SelectAllOnFocus = true };
                text.AddThemeFontSizeOverride("font_size", 11);
                text.Connect(LineEdit.SignalName.TextSubmitted, new Callable(this, MethodName.OnText));
                text.Connect(Control.SignalName.FocusExited, new Callable(this, MethodName.OnTextLeft));
                return text;
        }
    }

    void OnTypeSelected(long index) {
        Variant start = Types[index].Type switch {
            Variant.Type.Int => (long) _value.AsDouble(),
            Variant.Type.Float => _value.AsDouble(),
            Variant.Type.Bool => false,
            Variant.Type.Color => Colors.White,
            Variant.Type.NodePath => new NodePath(),
            _ => _value.VariantType is Variant.Type.Int or Variant.Type.Float
                ? _value.AsDouble().ToString(CultureInfo.InvariantCulture) : "",
        };
        Emit(start);
    }

    void OnPickPressed() => EditorInterface.Singleton.PopupNodeSelector(new Callable(this, MethodName.OnNodePicked), []);

    /// <summary>The node selector closed: with a path when a node was picked, an empty one when cancelled.</summary>
    void OnNodePicked(NodePath path) {
        if (!path.IsEmpty) EmitSignal(SignalName.NodePicked, ConstantId, path);
    }

    void OnToggled(bool on) => Emit(on);
    void OnNumber(double value) => Emit(_value.VariantType == Variant.Type.Int ? (long) value : value);
    void OnColorClosed() => Emit(((ColorPickerButton) _editor).Color);
    void OnText(string text) => Emit(text);

    void OnTextLeft() {
        if (_editor is LineEdit text) OnText(text.Text);
    }

    void Emit(Variant value) {
        if (value.VariantType == _value.VariantType && GD.VarToStr(value) == GD.VarToStr(_value)) return;
        EmitSignal(SignalName.ValueEdited, ConstantId, value);
    }
}
#endif
