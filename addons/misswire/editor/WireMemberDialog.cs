#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misswire.Editor;

/// <summary>
/// Picks members to show in a box: a search field above a tree that lists them by the class that
/// brings them, the object's own class first and <c>Object</c> last. Members the box shows already
/// are listed but cannot be picked. Several can be picked at once; a double click picks one.
/// </summary>
[Tool]
public partial class WireMemberDialog : ConfirmationDialog {
    /// <summary>Members were picked, as <c>kind:name</c>.</summary>
    [Signal]
    public delegate void MembersChosenEventHandler(string[] choices);

    LineEdit _search;
    Tree _tree;
    List<(string Class, List<WireMember> Members)> _groups = [];
    HashSet<string> _shown = [];

    public override void _Ready() {
        Title = "Add members";
        OkButtonText = "Add";
        MinSize = new Vector2I(440, 540);

        var content = new VBoxContainer();
        _search = new LineEdit { PlaceholderText = "Search members", ClearButtonEnabled = true };
        _search.Connect(LineEdit.SignalName.TextChanged, new Callable(this, MethodName.Filter));
        _tree = new Tree { HideRoot = true, SelectMode = Tree.SelectModeEnum.Multi, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _tree.Connect(Tree.SignalName.ItemActivated, new Callable(this, MethodName.OnItemActivated));
        content.AddChild(_search);
        content.AddChild(_tree);
        AddChild(content);
        RegisterTextEnter(_search);
        Connect(AcceptDialog.SignalName.Confirmed, new Callable(this, MethodName.OnConfirmed));
    }

    public void Open(string title, List<(string Class, List<WireMember> Members)> groups, HashSet<string> shown) {
        Title = title;
        _groups = groups;
        _shown = shown;
        _search.Text = "";
        Filter("");
        PopupCentered();
        _search.GrabFocus();
    }

    /// <summary>Shows the members whose name contains the text; with no text, only the first class is open.</summary>
    public void Filter(string text) {
        _tree.Clear();
        var root = _tree.CreateItem();
        var searching = !string.IsNullOrWhiteSpace(text);
        var first = true;
        foreach (var (type, members) in _groups) {
            var matching = members.Where(m => !searching || m.Name.Contains(text.Trim(), System.StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count == 0) continue;

            var group = _tree.CreateItem(root);
            group.SetText(0, type);
            group.SetSelectable(0, false);
            group.SetCustomColor(0, new Color(1, 1, 1, 0.6f));
            group.Collapsed = !searching && !first;
            first = false;
            foreach (var member in matching) {
                var key = $"{member.Kind}:{member.Name}";
                var item = _tree.CreateItem(group);
                var shown = _shown.Contains(key);
                item.SetText(0, $"{KindMark(member.Kind)}  {member.Label}{(shown ? "   (shown)" : "")}");
                item.SetMetadata(0, key);
                item.SetSelectable(0, !shown);
                item.SetCustomColor(0, shown ? new Color(1, 1, 1, 0.35f) : KindColor(member.Kind));
            }
        }
    }

    static string KindMark(string kind) => kind switch { WireMember.Property => "P", WireMember.Signal => "S", _ => "M" };

    static Color KindColor(string kind) => kind switch {
        WireMember.Signal => WireBox.SignalColor,
        WireMember.Method => WireBox.MethodColor,
        _ => WireBox.PropertyColor,
    };

    /// <summary>The class titles listed now, in order; for the tests.</summary>
    public List<string> Classes() => Items().Where(i => i.GetParent() == _tree.GetRoot()).Select(i => i.GetText(0)).ToList();

    /// <summary>The members listed now, as <c>kind:name</c>, opened or not; for the tests.</summary>
    public List<string> Members() => Items().Where(i => i.GetParent() != _tree.GetRoot()).Select(i => i.GetMetadata(0).AsString()).ToList();

    /// <summary>Whether a class's group is folded away now; for the tests.</summary>
    public bool IsCollapsed(string type) => Items().FirstOrDefault(i => i.GetParent() == _tree.GetRoot() && i.GetText(0) == type)?.Collapsed ?? true;

    IEnumerable<TreeItem> Items() {
        for (var item = _tree.GetRoot()?.GetFirstChild(); item != null; item = item.GetNextInTree()) yield return item;
    }

    public void Choose(IEnumerable<string> keys) {
        var choices = keys.Where(k => !_shown.Contains(k)).Distinct().ToArray();
        Hide();
        if (choices.Length > 0) EmitSignal(SignalName.MembersChosen, choices);
    }

    void OnConfirmed() {
        var picked = new List<string>();
        for (var item = _tree.GetNextSelected(null); item != null; item = _tree.GetNextSelected(item)) {
            if (item.GetMetadata(0).VariantType == Variant.Type.String) picked.Add(item.GetMetadata(0).AsString());
        }
        Choose(picked);
    }

    void OnItemActivated() {
        if (_tree.GetSelected() is { } item && item.GetMetadata(0).VariantType == Variant.Type.String) Choose([item.GetMetadata(0).AsString()]);
    }
}
#endif
