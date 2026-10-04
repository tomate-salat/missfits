#if TOOLS
using System.Linq;
using Godot;

namespace Misscore.Editor;

/// <summary>
/// Inspector editor for a <see cref="BbParam{T}"/>: the fixed value, edited with the editor's own
/// editor for the type, or the name of the linked blackboard entry — plus a menu to switch between
/// them, pick a matching entry or create one.
/// </summary>
[Tool]
public partial class BbParamEditorProperty : EditorProperty {
    const int MenuFixed = 0;
    const int MenuNewEntry = 1;
    const int MenuFirstEntry = 100;

    // Untyped so an assembly reload can restore it — see ReloadSafe.
    GodotObject _blackboard;

    BlackboardPanel Blackboard => ReloadSafe.Get<BlackboardPanel>(ref _blackboard);

    HBoxContainer _row;
    Label _linked;
    MenuButton _menu;
    EditorProperty _literal;
    ConfirmationDialog _nameDialog;
    LineEdit _nameField;

    public void Attach(BlackboardPanel blackboard) => _blackboard = blackboard;

    GodotObject Host => GetEditedObject();
    string Member => GetEditedProperty();
    BbParamMember Info => Host == null ? null : BbParams.Find(Host.GetType(), Member);

    public override void _Ready() => EnsureUi();

    /// <summary>Built on first need: the Inspector may ask for an update before this enters the tree.</summary>
    void EnsureUi() {
        if (_row != null) return;

        _row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_row);

        _linked = new Label {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
            MouseFilter = MouseFilterEnum.Pass,
        };
        _row.AddChild(_linked);

        _menu = new MenuButton {
            Flat = true,
            Icon = ResourceLoader.Load<Texture2D>("res://addons/misscore/icons/blackboard.svg"),
            ExpandIcon = false,
            TooltipText = "Fixed value or blackboard entry",
        };
        _menu.GetPopup().Connect(PopupMenu.SignalName.AboutToPopup, new Callable(this, MethodName.OnMenuAboutToPopup));
        _menu.GetPopup().Connect(PopupMenu.SignalName.IdPressed, new Callable(this, MethodName.OnMenuIdPressed));
        _row.AddChild(_menu);
        AddFocusable(_menu);

        _nameDialog = new ConfirmationDialog { Title = "New blackboard entry", OkButtonText = "Create" };
        _nameField = new LineEdit { CustomMinimumSize = new Vector2(260, 0) };
        _nameDialog.AddChild(_nameField);
        _nameDialog.RegisterTextEnter(_nameField);
        _nameDialog.Connect(AcceptDialog.SignalName.Confirmed, new Callable(this, MethodName.OnNameConfirmed));
        AddChild(_nameDialog);
    }

    public override void _UpdateProperty() {
        EnsureUi();
        var info = Info;
        var param = info?.On(Host);
        if (param == null) return;

        if (param.IsLinked) {
            var entry = Blackboard?.Source?.FindEntry(param.EntryId);
            var fits = entry != null && BbTypes.Accepts(info.ValueType, entry);
            _linked.Text = entry?.Name ?? param.EntryName;
            _linked.TooltipText = entry == null ? "This blackboard entry no longer exists."
                : fits ? $"Blackboard entry {entry.Name} ({entry.TypeLabel})"
                : $"{entry.Name} is {entry.TypeLabel}, which does not fit this parameter.";
            _linked.AddThemeColorOverride("font_color", fits ? BlackboardStyles.Linked : Warning);
            ShowLiteral(false);
            return;
        }

        if (info.EntryOnly) {
            _linked.Text = "not linked";
            _linked.TooltipText = "Pick a blackboard entry from the menu.";
            _linked.AddThemeColorOverride("font_color", Warning);
            ShowLiteral(false);
            return;
        }

        ShowLiteral(true);
        _literal?.UpdateProperty();
    }

    static Color Warning => new("#e0b400");

    void ShowLiteral(bool show) {
        _linked.Visible = !show;
        if (show && _literal == null) _literal = BuildLiteralEditor();
        if (_literal != null) _literal.Visible = show;
    }

    /// <summary>
    /// The stock editor for <c>T</c>, pointed at the host's hidden literal property. Its changes are
    /// turned into a change of the whole parameter, so the Inspector records one ordinary undo step.
    /// </summary>
    EditorProperty BuildLiteralEditor() {
        var info = Info;
        var (type, className) = BbTypes.Describe(info.ValueType);
        var hint = PropertyHint.None;
        var hintString = "";
        var usage = PropertyUsageFlags.Default;

        if (type == Variant.Type.Nil) usage |= PropertyUsageFlags.NilIsVariant;
        else if (type == Variant.Type.Object) (hint, hintString) = (PropertyHint.ResourceType, className);
        else if (info.ValueType.IsEnum) {
            hint = PropertyHint.Enum;
            hintString = string.Join(",", System.Enum.GetNames(info.ValueType)
                .Select(n => $"{n}:{System.Convert.ToInt64(System.Enum.Parse(info.ValueType, n))}"));
        }

        var property = Member + BbParams.LiteralSuffix;
        var editor = EditorInspector.InstantiatePropertyEditor(Host, type, property, hint, hintString, (uint) usage);
        if (editor == null) return null;

        editor.DrawLabel = false;
        editor.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        editor.SetObjectAndProperty(Host, property);
        editor.Connect(EditorProperty.SignalName.PropertyChanged, new Callable(this, MethodName.OnLiteralChanged));
        _row.AddChild(editor);
        _row.MoveChild(editor, 0);
        editor.UpdateProperty();
        return editor;
    }

    void OnLiteralChanged(StringName property, Variant value, StringName field, bool changing) {
        var param = Info?.On(Host);
        if (param == null) return;

        var stored = BbParams.ToStorage(param);
        stored["value"] = value;
        EmitChanged(Member, stored, "", changing);
    }

    void OnMenuAboutToPopup() {
        var popup = _menu.GetPopup();
        popup.Clear();

        var info = Info;
        var param = info?.On(Host);
        if (param == null) return;

        var blackboard = Blackboard;
        var inOpenSource = blackboard != null && blackboard.Contains(Host as IBbParamHost);

        if (!info.EntryOnly) {
            popup.AddRadioCheckItem("Fixed value", MenuFixed);
            popup.SetItemChecked(popup.GetItemIndex(MenuFixed), !param.IsLinked);
        }
        popup.AddSeparator("Blackboard");

        if (!inOpenSource) {
            popup.AddItem(blackboard?.NotOpenHint ?? "Open this in its editor to link entries");
            popup.SetItemDisabled(popup.ItemCount - 1, true);
            return;
        }

        var entries = blackboard.Source.Blackboard;
        var matching = 0;
        for (var i = 0; i < entries.Count; i++) {
            var entry = entries[i];
            if (entry == null || !BbTypes.Accepts(info.ValueType, entry)) continue;
            popup.AddRadioCheckItem($"{entry.Name}  ({entry.TypeLabel})", MenuFirstEntry + i);
            popup.SetItemChecked(popup.ItemCount - 1, param.EntryId == entry.Id);
            matching++;
        }
        if (matching == 0) {
            popup.AddItem("No entry of a matching type");
            popup.SetItemDisabled(popup.ItemCount - 1, true);
        }

        popup.AddSeparator();
        popup.AddItem("New entry…", MenuNewEntry);
    }

    void OnMenuIdPressed(long id) {
        var blackboard = Blackboard;
        if (blackboard == null || Host is not IBbParamHost host) return;

        switch (id) {
            case MenuFixed:
                blackboard.UnlinkParam(host, Member);
                break;
            case MenuNewEntry:
                _nameField.Text = blackboard.UniqueName(Member);
                _nameDialog.PopupCentered();
                _nameField.SelectAll();
                _nameField.GrabFocus();
                break;
            default:
                var index = (int) id - MenuFirstEntry;
                var entries = blackboard.Source?.Blackboard;
                if (entries != null && index >= 0 && index < entries.Count) blackboard.LinkParam(host, Member, entries[index].Id);
                break;
        }
    }

    void OnNameConfirmed() => Blackboard?.CreateEntryForParam(Host as IBbParamHost, Member, _nameField.Text);
}
#endif
