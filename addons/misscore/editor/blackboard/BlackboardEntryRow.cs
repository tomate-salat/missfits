#if TOOLS
using Godot;

namespace Misscore.Editor;

/// <summary>
/// One entry in the blackboard panel, drawn as a card: the name with a remove button, and below it
/// the type chip beside the default value. It knows its entry by id and reports edits as signals,
/// leaving the edit itself to <see cref="BlackboardPanel"/>.
/// </summary>
[Tool]
public partial class BlackboardEntryRow : PanelContainer {
    [Signal]
    public delegate void RenameRequestedEventHandler(string entryId, string name);

    [Signal]
    public delegate void TypeRequestedEventHandler(string entryId, Vector2 screenPosition);

    [Signal]
    public delegate void RemoveRequestedEventHandler(string entryId);

    [Signal]
    public delegate void DefaultEditedEventHandler(string entryId, Variant value);

    public string EntryId { get; private set; } = "";

    /// <summary>Opacity of the remove button while the card is not hovered: there, but not competing with the name.</summary>
    const float QuietAlpha = 0.35f;

    LineEdit _name;
    Button _type;
    Button _remove;
    Control _value;
    Variant.Type _variantType;

    public void Build(BlackboardEntry entry) {
        EntryId = entry.Id;
        Name = entry.Id;
        _variantType = entry.VariantType;
        AddThemeStyleboxOverride("panel", BlackboardStyles.Card(_variantType));
        Connect(Control.SignalName.MouseEntered, new Callable(this, MethodName.OnHover));
        Connect(Control.SignalName.MouseExited, new Callable(this, MethodName.OnUnhover));

        var content = new VBoxContainer { Name = "Content" };
        content.AddThemeConstantOverride("separation", 3);
        AddChild(content);

        var line = new HBoxContainer { Name = "Header" };
        line.AddThemeConstantOverride("separation", 2);
        _name = new LineEdit {
            Name = "EntryName",
            Text = entry.Name,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Flat = true,
            TooltipText = "Rename — parameters link to the entry itself, so nothing breaks",
        };
        if (BlackboardStyles.BoldFont() is { } bold) _name.AddThemeFontOverride("font", bold);
        _name.AddThemeConstantOverride("minimum_character_width", 4);
        _name.Connect(LineEdit.SignalName.TextSubmitted, new Callable(this, MethodName.OnNameSubmitted));
        _name.Connect(Control.SignalName.FocusExited, new Callable(this, MethodName.OnNameFocusLost));
        line.AddChild(_name);

        var removeIcon = BlackboardStyles.EditorIcon("Remove");
        _remove = new Button {
            Name = "Remove",
            Text = removeIcon == null ? "✕" : "",
            Icon = removeIcon,
            Flat = true,
            TooltipText = "Remove the entry",
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            Modulate = new Color(1, 1, 1, QuietAlpha),
        };
        _remove.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnRemovePressed));
        line.AddChild(_remove);
        content.AddChild(line);

        // Type and value share the second line, so a long class name never squeezes the entry's name.
        var valueLine = new HBoxContainer { Name = "Body" };
        valueLine.AddThemeConstantOverride("separation", 6);
        _type = TypeChip(entry);
        _type.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnTypePressed));
        valueLine.AddChild(_type);

        _value = BuildValueEditor(entry);
        _value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _value.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        valueLine.AddChild(_value);
        content.AddChild(valueLine);
    }

    static Button TypeChip(BlackboardEntry entry) {
        var color = BlackboardStyles.TypeColor(entry.VariantType);
        var chip = new Button {
            Name = "EntryType",
            Text = entry.TypeLabel,
            Icon = BlackboardStyles.TypeIcon(entry.VariantType, entry.ClassName),
            ExpandIcon = false,
            TooltipText = "Change the type",
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 20),
        };
        chip.AddThemeStyleboxOverride("normal", BlackboardStyles.Chip(entry.VariantType, hovered: false));
        chip.AddThemeStyleboxOverride("hover", BlackboardStyles.Chip(entry.VariantType, hovered: true));
        chip.AddThemeStyleboxOverride("pressed", BlackboardStyles.Chip(entry.VariantType, hovered: true));
        chip.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        chip.AddThemeFontSizeOverride("font_size", 11);
        chip.AddThemeConstantOverride("h_separation", 4);
        chip.AddThemeConstantOverride("icon_max_width", 14);
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" }) {
            chip.AddThemeColorOverride(state, color.Lightened(0.25f));
        }
        return chip;
    }

    /// <summary>
    /// The editor's own property editor for the entry's type, pointed straight at the entry's
    /// <see cref="BlackboardEntry.Default"/>. Outside the editor — in the headless test — a label.
    /// </summary>
    Control BuildValueEditor(BlackboardEntry entry) {
        if (entry.IsNode) return Hint("set per instance");
        if (!Engine.IsEditorHint()) return new Label { Name = "Value", Text = BbTypes.Format(entry.Default) };

        var hint = entry.VariantType == Variant.Type.Object ? PropertyHint.ResourceType : PropertyHint.None;
        var usage = PropertyUsageFlags.Default;
        if (entry.VariantType == Variant.Type.Nil) usage |= PropertyUsageFlags.NilIsVariant;

        var editor = EditorInspector.InstantiatePropertyEditor(entry, entry.VariantType, nameof(BlackboardEntry.Default),
            hint, entry.ClassName, (uint) usage);
        if (editor == null) return Hint("no editor for this type");

        editor.Name = "Value";
        editor.DrawLabel = false;
        editor.SetObjectAndProperty(entry, nameof(BlackboardEntry.Default));
        editor.Connect(EditorProperty.SignalName.PropertyChanged, new Callable(this, MethodName.OnDefaultChanged));
        editor.UpdateProperty();
        return editor;
    }

    static Label Hint(string text) {
        var label = new Label {
            Name = "Value",
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Right,
            TooltipText = "Scene nodes cannot be stored in a resource — assign this where the blackboard is used",
            MouseFilter = MouseFilterEnum.Pass,
        };
        label.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.4f));
        label.AddThemeFontSizeOverride("font_size", 11);
        return label;
    }

    void OnHover() {
        AddThemeStyleboxOverride("panel", BlackboardStyles.HoveredCard(_variantType));
        _remove.Modulate = Colors.White;
    }

    void OnUnhover() {
        // Moving onto a child control is not leaving the card.
        if (GetGlobalRect().HasPoint(GetGlobalMousePosition())) return;
        AddThemeStyleboxOverride("panel", BlackboardStyles.Card(_variantType));
        _remove.Modulate = new Color(1, 1, 1, QuietAlpha);
    }

    /// <summary>Re-reads the value into the editor after the panel applied a change.</summary>
    public void UpdateValue() {
        if (_value is EditorProperty editor) editor.UpdateProperty();
    }

    void OnDefaultChanged(StringName property, Variant value, StringName field, bool changing) {
        EmitSignal(SignalName.DefaultEdited, EntryId, value);
        UpdateValue();
    }

    void OnNameSubmitted(string text) => EmitSignal(SignalName.RenameRequested, EntryId, text);

    /// <summary>Leaving the field commits the name too — unless the row is on its way out anyway.</summary>
    void OnNameFocusLost() {
        if (IsQueuedForDeletion() || !IsInsideTree()) return;
        EmitSignal(SignalName.RenameRequested, EntryId, _name.Text);
    }

    void OnTypePressed() => EmitSignal(SignalName.TypeRequested, EntryId, _type.GetScreenPosition() + new Vector2(0, _type.Size.Y));

    void OnRemovePressed() => EmitSignal(SignalName.RemoveRequested, EntryId);
}
#endif
