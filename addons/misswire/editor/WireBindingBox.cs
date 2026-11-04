#if TOOLS
using System.Collections.Generic;
using Godot;

namespace Misswire.Editor;

/// <summary>What a binding box shows of one step: its line, and whether it takes its value through a port.</summary>
public record WireStepView(string Text, string Rich, string Warning, Color Color, bool ValuePort);

/// <summary>
/// A binding: its source comes in on the left of the header, and each step leaves on the right of
/// its row, top to bottom. A step that takes its value through a port has an input on the left of
/// its row too. The last row is a spare port, from which a wire makes a new step.
/// </summary>
[Tool]
public partial class WireBindingBox : WireBox {
    public const string Source = "source";
    public const string Step = "step";
    public const string Value = "value";
    public const string Spare = "spare";
    public const string Trigger = "trigger";

    public string BindingId { get; private set; } = "";

    /// <param name="triggers">For a property binding, what sets it off, as the trigger row says it; null for no such row.</param>
    public void Build(string bindingId, string title, string subtitle, string warning, Color sourceColor, IEnumerable<WireStepView> steps,
                      string triggers = null) {
        BindingId = bindingId;
        BuildFrame(title, subtitle, warning, SignalColor);
        SetPorts(0, $"{Source}:", true, sourceColor, false, Colors.White);
        if (triggers != null) AddRow(Trigger, "", triggers, "", true, SignalColor, false, Colors.White);

        var index = 0;
        foreach (var step in steps) {
            var row = AddRow(Step, index.ToString(), $"{index + 1}  {step.Text}", step.Warning, false, step.Color, true, step.Color, $"{index + 1}  {step.Rich}");
            if (step.ValuePort) AddValuePort(row.GetIndex(), index, step.Color);
            index++;
        }

        var spare = new Label {
            Name = "Spare",
            Text = "+ step",
            HorizontalAlignment = HorizontalAlignment.Right,
            TooltipText = "Drag from the port to a method or property to add a step",
            MouseFilter = MouseFilterEnum.Pass,
        };
        spare.AddThemeFontSizeOverride("font_size", 11);
        spare.AddThemeColorOverride("font_color", Faded);
        AddChild(spare);
        SetPorts(spare.GetIndex(), $"{Spare}:", false, Colors.White, true, new Color(MethodColor, 0.4f));
    }

    /// <summary>A step row's input on the left, where its value is wired in.</summary>
    void AddValuePort(int slot, int step, Color color) {
        SetSlotEnabledLeft(slot, true);
        SetSlotColorLeft(slot, PropertyColor);
        AddInput($"{Value}:{step}");
    }
}
#endif
