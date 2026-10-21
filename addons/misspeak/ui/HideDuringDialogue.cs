using Godot;

namespace Misspeak;

/// <summary>
/// Hides its parent while any dialogue is under way — a HUD element, say. Put it under the element;
/// no code needed. Afterwards the parent is as visible as it was before, so an element hidden for
/// another reason stays hidden.
/// </summary>
[GlobalClass]
public partial class HideDuringDialogue : Node {
    CanvasItem _target;
    bool _hidden;
    bool _wasVisible;

    public override void _EnterTree() {
        _target = GetParent() as CanvasItem;
        if (_target == null) {
            GD.PushWarning($"misspeak: {Name} hides its parent, which has to be a Control or a Node2D.");
            return;
        }

        var events = DialogueEvents.Of(this);
        events.AnyActiveChanged += OnAnyActiveChanged;
        OnAnyActiveChanged(events.IsAnyActive);
    }

    public override void _ExitTree() {
        if (DialogueEvents.Instance is { } events && IsInstanceValid(events)) events.AnyActiveChanged -= OnAnyActiveChanged;
    }

    void OnAnyActiveChanged(bool active) {
        if (_target == null || !IsInstanceValid(_target) || active == _hidden) return;

        _hidden = active;
        if (active) {
            _wasVisible = _target.Visible;
            _target.Visible = false;
        }
        else {
            _target.Visible = _wasVisible;
        }
    }
}
