#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;
using Misscore.Editor;

namespace Misspeak.Editor;

/// <summary>The kinds of rows a <see cref="SectionBox"/> has, as <see cref="GraphRow.Kind"/>.</summary>
public static class SpeakRow {
    /// <summary>A line of the section.</summary>
    public const string Line = "line";

    /// <summary>A condition of the line above it.</summary>
    public const string LineCondition = "line_condition";

    /// <summary>An action of the line above it.</summary>
    public const string Action = "action";

    /// <summary>An option — a way on — which carries an output port.</summary>
    public const string Option = "option";

    /// <summary>A condition of the option above it.</summary>
    public const string OptionCondition = "option_condition";

    /// <summary>How far what belongs to a line or an option is set in from it.</summary>
    public const int Indent = 18;
}

/// <summary>
/// One section of a dialogue in the graph: its name, its lines, and its options.
/// <para>
/// The header carries the input port on the left — every option that leads to the section ends
/// there. Below it come the lines, each followed by its conditions and actions, set in, and a button
/// to add one. Then the options: each row is a slot with an output port on the right, followed by
/// one indented row per condition, and a last row offers a spare port, from which a dragged wire
/// makes a new option. GraphEdit numbers output ports by enabled slot, so port <c>i</c> is option
/// <c>i</c> and the port after the last one is the spare.
/// </para>
/// </summary>
[Tool]
public partial class SectionBox : MissGraphBox {
    /// <summary>The box was right-clicked; the position is in screen coordinates.</summary>
    [Signal]
    public delegate void MenuRequestedEventHandler(StringName boxName, Vector2 screenPosition);

    /// <summary>A click picked a row of this section — or the section itself, with an empty id.</summary>
    [Signal]
    public delegate void RowPickedEventHandler(StringName boxName, string kind, string id);

    /// <summary>A row was right-clicked; the position is in screen coordinates.</summary>
    [Signal]
    public delegate void RowMenuRequestedEventHandler(StringName boxName, string kind, string id, Vector2 screenPosition);

    /// <summary>The "add line" button was pressed.</summary>
    [Signal]
    public delegate void AddLineRequestedEventHandler(StringName boxName);

    // Untyped so an assembly reload can restore it — see ReloadSafe.
    GodotObject _section;

    public DialogueSection Section => ReloadSafe.Get<DialogueSection>(ref _section);

    /// <summary>Kind of the row shown in the Inspector, or empty for the section itself.</summary>
    public string PickedKind { get; private set; } = "";

    /// <summary>Id of the row shown in the Inspector, or empty for the section itself.</summary>
    public string PickedId { get; private set; } = "";

    static readonly Color PortColor = new("#d0d3d8");
    static readonly Color StartColor = new("#5dcaa5");
    static readonly Color Edge = new("#3b3f46");
    static readonly Color SelectedEdge = new("#8ab4f8");

    Label _start;
    Label _name;
    Label _warning;
    Button _addLine;
    Label _spare;

    public void Bind(DialogueSection section) {
        _section = section;
        Name = section.Id;

        // Without a title bar the body has no upper edge of its own, so the whole outline is drawn here.
        AddThemeStyleboxOverride("panel", Outline(selected: false));
        AddThemeStyleboxOverride("panel_selected", Outline(selected: true));
        AddThemeConstantOverride("separation", 3);

        // Zooming the graph scales the box as it is; an ordinary font would be a blown-up bitmap.
        Theme = ZoomFonts.ThemeFor(this);

        var header = new HBoxContainer { Name = "Header", CustomMinimumSize = new Vector2(230, 24) };
        header.AddThemeConstantOverride("separation", 6);
        _start = new Label { Name = "Start", TooltipText = "The dialogue starts with this section", MouseFilter = MouseFilterEnum.Pass };
        _start.AddThemeColorOverride("font_color", StartColor);
        _name = new Label { Name = "SectionName", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        if (BlackboardStyles.BoldFont() is { } bold) _name.AddThemeFontOverride("font", ZoomFonts.Sharp(bold));
        _warning = new Label { Name = "Warning", MouseFilter = MouseFilterEnum.Pass };
        _warning.AddThemeColorOverride("font_color", new Color("#ffd24a"));
        header.AddChild(_start);
        header.AddChild(_name);
        header.AddChild(_warning);
        AddChild(header);

        foreach (var line in section.Lines) {
            if (line == null) continue;
            AddRow(SpeakRow.Line, line.Id, 0);
            foreach (var condition in line.Conditions) {
                if (condition != null) AddRow(SpeakRow.LineCondition, condition.Id, SpeakRow.Indent);
            }
            foreach (var action in line.Actions) {
                if (action != null) AddRow(SpeakRow.Action, action.Id, SpeakRow.Indent);
            }
        }

        _addLine = new Button {
            Name = "AddLine",
            Text = "+ line",
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            TooltipText = "Add a line to this section",
            FocusMode = FocusModeEnum.None,
        };
        _addLine.AddThemeFontSizeOverride("font_size", 11);
        _addLine.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.45f));
        _addLine.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddLinePressed));
        AddChild(_addLine);

        foreach (var option in section.Options) {
            if (option == null) continue;
            AddRow(SpeakRow.Option, option.Id, 0);
            foreach (var condition in option.Conditions) {
                if (condition != null) AddRow(SpeakRow.OptionCondition, condition.Id, SpeakRow.Indent);
            }
        }

        _spare = new Label {
            Name = "Spare",
            Text = "new option",
            HorizontalAlignment = HorizontalAlignment.Right,
            TooltipText = "Drag from the port to another section to add a way on",
            MouseFilter = MouseFilterEnum.Pass,
        };
        _spare.AddThemeFontSizeOverride("font_size", 11);
        _spare.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.3f));
        AddChild(_spare);

        Connect(GraphElement.SignalName.NodeDeselected, new Callable(this, MethodName.OnDeselected));
        HideTitlebar();
    }

    /// <summary>Opacity of a section the running dialogue is not in.</summary>
    public const float DimmedAlpha = 0.45f;

    /// <summary>Whether a running game last reported the dialogue to be in this section.</summary>
    public bool IsCurrent { get; private set; }

    /// <summary>
    /// Shows where a running dialogue is: the section it is in gets an outline in the colour of
    /// "running" and the line it is at the same tint; every other section fades.
    /// </summary>
    /// <param name="lineId">The line the dialogue is at, or empty for none.</param>
    /// <param name="dialogue">The dialogue the section belongs to.</param>
    /// <param name="backTo">Name of the section an option that leads back would return to; null when there is none.</param>
    /// <param name="wentBackBy">Id of the option that has just led the dialogue back, or empty.</param>
    public void ShowLive(bool current, string lineId, Dialogue dialogue = null, string backTo = null, string wentBackBy = "") {
        IsCurrent = current;
        Modulate = new Color(1, 1, 1, current ? 1f : DimmedAlpha);
        AddThemeStyleboxOverride("panel", Outline(selected: false, current));
        AddThemeStyleboxOverride("panel_selected", Outline(selected: true, current));
        foreach (var row in Rows(SpeakRow.Line)) row.ShowStatus(current && row.Id == lineId ? MissStatus.Running : null);

        // An option that leads back has no wire to light up, so its row says it: where it would
        // go while the dialogue is here, and that it was the way out once it has been taken.
        foreach (var row in Rows(SpeakRow.Option)) {
            var option = Section?.Options.FirstOrDefault(o => o != null && o.Id == row.Id);
            if (option == null) continue;

            var waiting = current && option.Back;
            var text = SpeakLabels.Describe(option, dialogue);
            if (waiting) text += backTo == null ? " — ends the dialogue" : $" to {backTo}";
            row.Show(text, row.Warning, IsPicked(row));
            row.ShowStatus(option.Id == wentBackBy && wentBackBy != "" ? MissStatus.Success : waiting ? MissStatus.Running : null);
        }
    }

    /// <summary>Back to how the box looks while no dialogue is running.</summary>
    public void ClearLive(Dialogue dialogue = null) {
        IsCurrent = false;
        Modulate = Colors.White;
        AddThemeStyleboxOverride("panel", Outline(selected: false));
        AddThemeStyleboxOverride("panel_selected", Outline(selected: true));
        foreach (var row in Rows(SpeakRow.Line)) row.ShowStatus(null);
        foreach (var row in Rows(SpeakRow.Option)) {
            var option = Section?.Options.FirstOrDefault(o => o != null && o.Id == row.Id);
            if (option != null) row.Show(SpeakLabels.Describe(option, dialogue), row.Warning, IsPicked(row));
            row.ShowStatus(null);
        }
    }

    static StyleBoxFlat Outline(bool selected, bool current = false) {
        var style = new StyleBoxFlat {
            BgColor = selected ? new Color("#31353c") : new Color("#2a2d32"),
            BorderColor = current ? GraphRow.Running : selected ? SelectedEdge : Edge,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 6,
            ContentMarginBottom = 8,
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(6);
        return style;
    }

    void AddRow(string kind, string id, int indent) {
        var row = new GraphRow();
        row.Build(kind, id, indent);
        row.Connect(GraphRow.SignalName.Picked, new Callable(this, MethodName.OnRowPicked));
        row.Connect(GraphRow.SignalName.MenuRequested, new Callable(this, MethodName.OnRowMenu));
        AddChild(row);
    }

    public IEnumerable<GraphRow> Rows(string kind) => GetChildren().OfType<GraphRow>().Where(r => r.Kind == kind);

    /// <summary>Re-reads name, lines, options and warnings from the section. No row is created or freed.</summary>
    /// <param name="dialogue">The dialogue the section belongs to, which knows its entries and the names of the targets.</param>
    public void Refresh(Dialogue dialogue) {
        var section = Section;
        if (section == null) return;

        FlattenTitlebar();
        Title = section.Name;
        _name.Text = string.IsNullOrEmpty(section.Name) ? "(unnamed)" : section.Name;
        _start.Text = ReferenceEquals(dialogue?.StartSection, section) ? "▶" : "";

        _warning.Text = "";
        _warning.TooltipText = "";
        if (!string.IsNullOrEmpty(section.Name) && dialogue != null && dialogue.Sections.Count(s => s != null && s.Name == section.Name) > 1) {
            _warning.Text = "⚠";
            _warning.TooltipText = "the name is used by more than one section";
        }

        // Slots follow the children. Only the header has an input, only options and the spare an output.
        for (var slot = 0; slot < GetChildCount(); slot++) {
            var output = false;
            var color = PortColor;
            switch (GetChild(slot)) {
                case GraphRow { Kind: SpeakRow.Line } row:
                    var line = section.Lines.FirstOrDefault(l => l != null && l.Id == row.Id);
                    row.Show(SpeakLabels.Says(line), SpeakLabels.Problem(line, dialogue), IsPicked(row));
                    break;
                case GraphRow { Kind: SpeakRow.Action } row:
                    var action = section.Lines.Where(l => l != null).SelectMany(l => l.Actions).FirstOrDefault(a => a != null && a.Id == row.Id);
                    row.Show(SpeakLabels.Does(action), SpeakLabels.Problem(action, dialogue), IsPicked(row));
                    break;
                case GraphRow { Kind: SpeakRow.LineCondition } row:
                    var owner = section.Lines.FirstOrDefault(l => l != null && l.Conditions.Any(c => c != null && c.Id == row.Id));
                    ShowCondition(row, owner?.Conditions, owner?.Mode ?? ListMode.Sequence, dialogue);
                    break;
                case GraphRow { Kind: SpeakRow.OptionCondition } row:
                    var holder = section.Options.FirstOrDefault(o => o != null && o.Conditions.Any(c => c != null && c.Id == row.Id));
                    ShowCondition(row, holder?.Conditions, holder?.Mode ?? ListMode.Sequence, dialogue);
                    break;
                case GraphRow { Kind: SpeakRow.Option } row:
                    var option = section.Options.FirstOrDefault(o => o != null && o.Id == row.Id);
                    row.Show(SpeakLabels.Describe(option, dialogue), SpeakLabels.Problem(option, dialogue), IsPicked(row));
                    output = true;
                    break;
                case var child when child == _spare:
                    output = true;
                    color = new Color(PortColor, 0.4f);
                    break;
            }
            SetSlot(slot, slot == 0, 0, PortColor, output, 0, color);
        }
    }

    void ShowCondition(GraphRow row, Godot.Collections.Array<MissNode> conditions, ListMode mode, Dialogue dialogue) {
        var kept = conditions?.Where(c => c != null).ToList() ?? [];
        var place = kept.FindIndex(c => c.Id == row.Id);
        var condition = place < 0 ? null : kept[place];
        row.Show(SpeakLabels.Holds(condition, mode, place), SpeakLabels.Problem(condition, dialogue), IsPicked(row));
    }

    bool IsPicked(GraphRow row) => Selected && PickedKind == row.Kind && PickedId == row.Id;

    /// <summary>Marks a row — or the section itself, with an empty id — as what is being inspected.</summary>
    public void ShowPicked(string kind, string id) {
        PickedKind = string.IsNullOrEmpty(id) ? "" : kind ?? "";
        PickedId = id ?? "";
        foreach (var row in GetChildren().OfType<GraphRow>()) row.Show(row.Text, row.Warning, IsPicked(row));
    }

    void OnDeselected() => ShowPicked("", "");

    void OnRowPicked(string kind, string id) => EmitSignal(SignalName.RowPicked, Name, kind, id);

    void OnRowMenu(string kind, string id, Vector2 screenPosition)
        => EmitSignal(SignalName.RowMenuRequested, Name, kind, id, screenPosition);

    void OnAddLinePressed() => EmitSignal(SignalName.AddLineRequested, Name);

    public override void _GuiInput(InputEvent @event) {
        if (@event is not InputEventMouseButton { Pressed: true } click) return;

        if (click.ButtonIndex == MouseButton.Left) {
            // A click on the box that is not on a row goes back to the section itself.
            if (PickedId != "") EmitSignal(SignalName.RowPicked, Name, "", "");
        }
        else if (click.ButtonIndex == MouseButton.Right) {
            EmitSignal(SignalName.MenuRequested, Name, GetScreenPosition() + click.Position);
            AcceptEvent();
        }
    }
}

/// <summary>How lines, actions and options read in the graph.</summary>
public static class SpeakLabels {
    /// <summary>How much of a text a row shows before it is cut short.</summary>
    const int MaxLength = 44;

    static string Short(string text) {
        var flat = (text ?? "").ReplaceLineEndings(" ").Trim();
        return flat.Length <= MaxLength ? flat : flat[..(MaxLength - 1)].TrimEnd() + "…";
    }

    /// <summary>E.g. "Smith: Welcome to my forge." — or that the line is silent.</summary>
    public static string Says(DialogueLine line) {
        if (line == null) return "";
        if (string.IsNullOrEmpty(line.Text)) return "(silent)";
        return string.IsNullOrEmpty(line.Speaker) ? Short(line.Text) : Short($"{line.Speaker}: {line.Text}");
    }

    public static string Does(MissNode action) => action == null ? "" : $"▸ {Runs(action)}";

    static string Runs(MissNode node) {
        var summary = node.GetSummary();
        return string.IsNullOrEmpty(summary) ? node.GetLabel() : $"{node.GetLabel()} — {summary}";
    }

    /// <summary>A condition as it reads below what it belongs to: "if X" for the first, then "and X" or "or X".</summary>
    public static string Holds(MissNode condition, ListMode mode, int place) {
        if (condition == null) return "";
        var lead = place <= 0 ? "if" : mode == ListMode.Selector ? "or" : "and";
        return $"{lead} {Runs(condition)}";
    }

    /// <summary>
    /// A choice reads as its text and where it leads, the way on by itself as "→ Hub"; "→ end"
    /// where it leads nowhere, "↩ back" where it leads back.
    /// </summary>
    public static string Describe(DialogueOption option, Dialogue dialogue) {
        if (option == null) return "";

        if (option.Back) return option.IsChoice ? $"“{Short(option.Text)}” ↩ back" : "↩ back";

        var target = dialogue?.Destination(option.TargetSectionId);
        var where = string.IsNullOrEmpty(option.TargetSectionId) ? "end"
            : target == null ? "?"
            : string.IsNullOrEmpty(target.Name) ? "(unnamed)" : target.Name;
        return option.IsChoice ? $"“{Short(option.Text)}” → {where}" : $"→ {where}";
    }

    public static string Problem(DialogueLine line, Dialogue dialogue) {
        if (line == null) return "";
        if (string.IsNullOrEmpty(line.Text) && !line.Actions.Any(a => a != null)) return "says nothing and does nothing";
        return dialogue != null && !dialogue.KnowsSpeaker(line.Speaker) ? $"{line.Speaker} is not among the dialogue's speakers" : "";
    }

    /// <summary>What is wrong with an action or a condition, one problem per line.</summary>
    public static string Problem(MissNode node, Dialogue dialogue) {
        if (node == null) return "";
        var problems = new List<string>(node.GetConfigurationWarnings());
        if (dialogue != null) problems.AddRange(dialogue.LinkProblems(node));
        return string.Join("\n", problems);
    }

    public static string Problem(DialogueOption option, Dialogue dialogue) {
        if (option == null || option.Back || string.IsNullOrEmpty(option.TargetSectionId)) return "";
        return dialogue?.Destination(option.TargetSectionId) == null ? "leads nowhere — drag its port onto a section" : "";
    }
}
#endif
