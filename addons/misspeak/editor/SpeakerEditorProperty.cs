#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misspeak.Editor;

/// <summary>
/// The Inspector's editor for a line's speaker: a dropdown of the dialogue's speakers instead of a
/// text field. A name the line already has but the dialogue does not list is kept on offer, marked
/// as such, so opening a line never changes it.
/// </summary>
[Tool]
public partial class SpeakerEditorProperty : EditorProperty {
    /// <summary>What the dropdown shows for a line nobody speaks.</summary>
    public const string Nobody = "(nobody)";

    /// <summary>What it appends to a name the dialogue does not list.</summary>
    public const string Unlisted = " (not listed)";

    OptionButton _picker;

    /// <summary>The names on offer, as a Godot array so an assembly reload keeps them.</summary>
    Godot.Collections.Array<string> _names = [];

    /// <summary>How many of <see cref="_names"/>, from the start, are speakers the dialogue lists.</summary>
    int _listed;

    bool _updating;

    /// <param name="listed">The speakers the dialogue lists.</param>
    /// <param name="others">Further names its lines use.</param>
    public void Offer(IEnumerable<string> listed, IEnumerable<string> others) {
        _names = [.. listed];
        _listed = _names.Count;
        foreach (var name in others) {
            if (!_names.Contains(name)) _names.Add(name);
        }
    }

    /// <summary>The entries of the dropdown, top to bottom.</summary>
    public List<string> Entries => [Nobody, .. _names.Select((name, i) => i < _listed ? name : name + Unlisted)];

    public override void _Ready() {
        _picker = new OptionButton { ClipText = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _picker.Connect(OptionButton.SignalName.ItemSelected, new Callable(this, MethodName.OnPicked));
        AddChild(_picker);
        AddFocusable(_picker);
        Fill();
    }

    void Fill() {
        if (_picker == null) return;

        _updating = true;
        _picker.Clear();
        foreach (var entry in Entries) _picker.AddItem(entry);
        _updating = false;
    }

    public override void _UpdateProperty() {
        if (_picker == null || GetEditedObject() is not { } edited) return;

        var current = edited.Get(GetEditedProperty()).AsString();
        if (current != "" && !_names.Contains(current)) {
            _names.Add(current);
            Fill();
        }

        _updating = true;
        _picker.Selected = current == "" ? 0 : _names.IndexOf(current) + 1;
        _updating = false;
    }

    void OnPicked(long index) {
        if (_updating) return;
        EmitChanged(GetEditedProperty(), index <= 0 ? "" : _names[(int) index - 1]);
    }
}
#endif
