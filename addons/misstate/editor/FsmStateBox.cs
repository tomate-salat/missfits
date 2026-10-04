#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;
using Misscore;
using Misscore.Editor;

namespace Misstate.Editor;

/// <summary>
/// One state in the graph: its name, the actions it runs, and its transitions.
/// <para>
/// The header carries the input port on the left — every transition into the state ends there.
/// Below it come the actions, one row each, and a button to add one. Then the transitions: each row
/// is a slot with an output port on the right, followed by one indented row per condition it
/// checks, and a last row offers a spare port, from which a dragged wire makes a new transition. GraphEdit numbers output ports by enabled slot, so port
/// <c>i</c> is transition <c>i</c> and the port after the last one is the spare.
/// </para>
/// </summary>
[Tool]
public partial class FsmStateBox : GraphNode {
    /// <summary>The box was right-clicked; the position is in screen coordinates.</summary>
    [Signal]
    public delegate void MenuRequestedEventHandler(StringName boxName, Vector2 screenPosition);

    /// <summary>A click picked a row of this state — or the state itself, with an empty id.</summary>
    [Signal]
    public delegate void RowPickedEventHandler(StringName boxName, string kind, string id);

    /// <summary>A row was right-clicked; the position is in screen coordinates.</summary>
    [Signal]
    public delegate void RowMenuRequestedEventHandler(StringName boxName, string kind, string id, Vector2 screenPosition);

    /// <summary>The "add action" button was pressed; the position is where to open the picker.</summary>
    [Signal]
    public delegate void AddActionRequestedEventHandler(StringName boxName, Vector2 screenPosition);

    // Untyped so an assembly reload can restore it — see ReloadSafe.
    GodotObject _state;

    public FsmState State => ReloadSafe.Get<FsmState>(ref _state);

    /// <summary>Kind of the row shown in the Inspector, or empty for the state itself.</summary>
    public string PickedKind { get; private set; } = "";

    /// <summary>Id of the row shown in the Inspector, or empty for the state itself.</summary>
    public string PickedId { get; private set; } = "";

    static readonly Color PortColor = new("#d0d3d8");
    static readonly Color InitialColor = new("#5dcaa5");
    static readonly Color Edge = new("#3b3f46");
    static readonly Color SelectedEdge = new("#8ab4f8");

    Label _initial;
    Label _name;
    Label _warning;
    Label _mode;
    Button _addAction;
    Label _spare;

    public void Bind(FsmState state) {
        _state = state;
        Name = state.Id;

        // GraphNode's own look expects a title bar on top. Without one the body would have no
        // upper edge, so the whole outline is drawn here.
        AddThemeStyleboxOverride("panel", Outline(selected: false));
        AddThemeStyleboxOverride("panel_selected", Outline(selected: true));
        AddThemeStyleboxOverride("titlebar", new StyleBoxEmpty());
        AddThemeStyleboxOverride("titlebar_selected", new StyleBoxEmpty());
        AddThemeConstantOverride("separation", 3);

        // Zooming the graph scales the box as it is; an ordinary font would be a blown-up bitmap.
        Theme = ZoomFonts.ThemeFor(this);

        var header = new HBoxContainer { Name = "Header", CustomMinimumSize = new Vector2(170, 24) };
        header.AddThemeConstantOverride("separation", 6);
        _initial = new Label { Name = "Initial", TooltipText = "The machine starts in this state", MouseFilter = MouseFilterEnum.Pass };
        _initial.AddThemeColorOverride("font_color", InitialColor);
        _name = new Label { Name = "StateName", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        if (BlackboardStyles.BoldFont() is { } bold) _name.AddThemeFontOverride("font", ZoomFonts.Sharp(bold));
        _warning = new Label { Name = "Warning", MouseFilter = MouseFilterEnum.Pass };
        _warning.AddThemeColorOverride("font_color", new Color("#ffd24a"));
        header.AddChild(_initial);
        header.AddChild(_name);
        header.AddChild(_warning);
        AddChild(header);

        _mode = new Label { Name = "Mode" };
        _mode.AddThemeFontSizeOverride("font_size", 11);
        _mode.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.5f));
        AddChild(_mode);

        foreach (var action in state.Actions) {
            if (action != null) AddRow(FsmRow.Action, action.Id);
        }

        _addAction = new Button {
            Name = "AddAction",
            Text = "+ action",
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            TooltipText = "Add an action to this state",
            FocusMode = FocusModeEnum.None,
        };
        _addAction.AddThemeFontSizeOverride("font_size", 11);
        _addAction.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.45f));
        _addAction.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnAddActionPressed));
        AddChild(_addAction);

        foreach (var transition in state.Transitions) {
            if (transition == null) continue;
            AddRow(FsmRow.Transition, transition.Id);
            foreach (var condition in transition.Conditions) {
                if (condition != null) AddRow(FsmRow.Condition, condition.Id);
            }
        }

        _spare = new Label {
            Name = "Spare",
            Text = "new transition",
            HorizontalAlignment = HorizontalAlignment.Right,
            TooltipText = "Drag from the port to another state to add a transition",
            MouseFilter = MouseFilterEnum.Pass,
        };
        _spare.AddThemeFontSizeOverride("font_size", 11);
        _spare.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.3f));
        AddChild(_spare);

        Connect(GraphElement.SignalName.NodeDeselected, new Callable(this, MethodName.OnDeselected));

        // Title stays set — it is what the editor and tooltips refer to — but is not drawn.
        foreach (var label in GetTitlebarHBox().GetChildren(includeInternal: true).OfType<Label>()) label.Visible = false;
        Connect(Container.SignalName.SortChildren, new Callable(this, MethodName.FlattenTitlebar));
        FlattenTitlebar();
    }

    /// <summary>
    /// Keeps the unused title bar at no height. GraphNode places the rows by the bar's minimum
    /// height, which is nothing once its label is hidden — but draws the body below the bar's actual
    /// height, and a bar never shrinks by itself. If it was ever laid out while its label still took
    /// up room, the body would be drawn that much lower than the rows, with the header sticking out
    /// on top.
    /// </summary>
    void FlattenTitlebar() {
        var bar = GetTitlebarHBox();
        if (bar == null || bar.Size.Y <= 0) return;

        bar.Size = new Vector2(bar.Size.X, 0);
        QueueRedraw();
    }

    static StyleBoxFlat Outline(bool selected) {
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
        return style;
    }

    void AddRow(string kind, string id) {
        var row = new FsmRow();
        row.Build(kind, id);
        row.Connect(FsmRow.SignalName.Picked, new Callable(this, MethodName.OnRowPicked));
        row.Connect(FsmRow.SignalName.MenuRequested, new Callable(this, MethodName.OnRowMenu));
        AddChild(row);
    }

    public IEnumerable<FsmRow> Rows(string kind) => GetChildren().OfType<FsmRow>().Where(r => r.Kind == kind);

    /// <summary>Re-reads name, actions, transitions and warnings from the state. No row is created or freed.</summary>
    /// <param name="machine">The machine the state belongs to, which knows its entries and the names of the targets.</param>
    public void Refresh(Fsm machine) {
        var state = State;
        if (state == null) return;

        FlattenTitlebar();
        Title = state.Name;
        _name.Text = string.IsNullOrEmpty(state.Name) ? "(unnamed)" : state.Name;
        _initial.Text = ReferenceEquals(machine?.InitialState, state) ? "▶" : "";
        _mode.Text = FsmLabels.Mode(state);

        _warning.Text = "";
        _warning.TooltipText = "";
        if (machine != null && machine.States.Count(s => s != null && s.Name == state.Name) > 1) {
            _warning.Text = "⚠";
            _warning.TooltipText = "the name is used by more than one state";
        }

        // Slots follow the children. Only the header has an input, only transitions and the spare an output.
        for (var slot = 0; slot < GetChildCount(); slot++) {
            switch (GetChild(slot)) {
                case FsmRow { Kind: FsmRow.Action } row:
                    var action = state.Actions.FirstOrDefault(a => a != null && a.Id == row.Id);
                    row.Show(FsmLabels.Runs(action), FsmLabels.Problem(action, machine), IsPicked(row));
                    SetSlot(slot, false, 0, PortColor, false, 0, PortColor);
                    break;
                case FsmRow { Kind: FsmRow.Condition } row:
                    var owner = state.Transitions.FirstOrDefault(t => t != null && t.Conditions.Any(c => c != null && c.Id == row.Id));
                    var place = owner?.Conditions.Where(c => c != null).Select(c => c.Id).ToList().IndexOf(row.Id) ?? -1;
                    var condition = place < 0 ? null : owner.Conditions.Where(c => c != null).ElementAt(place);
                    row.Show(FsmLabels.Holds(condition, owner, place), FsmLabels.Problem(condition, machine), IsPicked(row));
                    SetSlot(slot, false, 0, PortColor, false, 0, PortColor);
                    break;
                case FsmRow row:
                    var transition = state.Transitions.FirstOrDefault(t => t != null && t.Id == row.Id);
                    row.Show(FsmLabels.Describe(transition, machine), FsmLabels.Problem(transition, machine), IsPicked(row));
                    SetSlot(slot, false, 0, PortColor, true, 0, PortColor);
                    break;
                case var child when child == _spare:
                    SetSlot(slot, false, 0, PortColor, true, 0, new Color(PortColor, 0.4f));
                    break;
                default:
                    SetSlot(slot, slot == 0, 0, PortColor, false, 0, PortColor);
                    break;
            }
        }
    }

    bool IsPicked(FsmRow row) => Selected && PickedKind == row.Kind && PickedId == row.Id;

    /// <summary>Marks a row — or the state itself, with an empty id — as what is being inspected.</summary>
    public void ShowPicked(string kind, string id) {
        PickedKind = string.IsNullOrEmpty(id) ? "" : kind ?? "";
        PickedId = id ?? "";
        foreach (var row in GetChildren().OfType<FsmRow>()) row.Show(row.Text, row.Warning, IsPicked(row));
    }

    void OnDeselected() => ShowPicked("", "");

    void OnRowPicked(string kind, string id) => EmitSignal(SignalName.RowPicked, Name, kind, id);

    void OnRowMenu(string kind, string id, Vector2 screenPosition)
        => EmitSignal(SignalName.RowMenuRequested, Name, kind, id, screenPosition);

    void OnAddActionPressed()
        => EmitSignal(SignalName.AddActionRequested, Name, _addAction.GetScreenPosition() + new Vector2(0, _addAction.Size.Y));

    public override void _GuiInput(InputEvent @event) {
        if (@event is not InputEventMouseButton { Pressed: true } click) return;

        if (click.ButtonIndex == MouseButton.Left) {
            // A click on the box that is not on a row goes back to the state itself.
            if (PickedId != "") EmitSignal(SignalName.RowPicked, Name, "", "");
        }
        else if (click.ButtonIndex == MouseButton.Right) {
            EmitSignal(SignalName.MenuRequested, Name, GetScreenPosition() + click.Position);
            AcceptEvent();
        }
    }
}

/// <summary>How states, actions and transitions read in the graph.</summary>
public static class FsmLabels {
    /// <summary>How the state works through its actions, or that it has none.</summary>
    public static string Mode(FsmState state) {
        if (!state.Actions.Any(a => a != null)) return "waits";
        var mode = state.Mode == ListMode.Sequence ? "sequence" : "selector";
        return state.Parallel ? $"{mode} · parallel" : mode;
    }

    public static string Runs(MissNode node) {
        if (node == null) return "";
        var summary = node.GetSummary();
        return string.IsNullOrEmpty(summary) ? node.GetLabel() : $"{node.GetLabel()} — {summary}";
    }

    /// <summary>E.g. "→ Chase" above its conditions, or "→ Idle  on success".</summary>
    public static string Describe(FsmTransition transition, Fsm machine) {
        if (transition == null) return "";

        var target = machine?.FindState(transition.TargetStateId);
        var targetName = target == null ? "?" : string.IsNullOrEmpty(target.Name) ? "(unnamed)" : target.Name;
        var on = transition.On switch {
            FsmTrigger.Finished => "when done",
            FsmTrigger.Succeeded => "on success",
            FsmTrigger.Failed => "on failure",
            _ => "",
        };
        var hasConditions = transition.Conditions.Any(c => c != null);
        var when = on != "" ? on : hasConditions ? "" : "always";
        return $"→ {targetName}  {when}".TrimEnd();
    }

    /// <summary>A condition as it reads below its transition: "if X" for the first, then "and X" or "or X".</summary>
    public static string Holds(MissNode condition, FsmTransition transition, int place) {
        if (condition == null) return "";
        var lead = place <= 0 ? "if" : transition?.Mode == ListMode.Selector ? "or" : "and";
        return $"{lead} {Runs(condition)}";
    }

    /// <summary>What is wrong with an action or a condition, one problem per line.</summary>
    public static string Problem(MissNode node, Fsm machine) {
        if (node == null) return "";
        var problems = new List<string>(node.GetConfigurationWarnings());
        if (machine != null) problems.AddRange(machine.LinkProblems(node));
        return string.Join("\n", problems);
    }

    public static string Problem(FsmTransition transition, Fsm machine) {
        if (transition == null) return "";

        var problems = new List<string>();
        if (machine?.FindState(transition.TargetStateId) == null) problems.Add("leads nowhere — drag its port onto a state");
        if (transition.On == FsmTrigger.Always && !transition.Conditions.Any(c => c != null)) {
            problems.Add("fires on every tick, so the state is left after one");
        }
        return string.Join("\n", problems);
    }
}
#endif
