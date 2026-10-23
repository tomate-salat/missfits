#if TOOLS
using Godot;

namespace Misscore.Editor;

/// <summary>
/// Shortcuts an editor panel takes over from the Godot editor. A panel only takes one while the
/// focus is inside it — a panel that is merely open must not take Ctrl+S from the scene being
/// worked on elsewhere in the editor.
/// </summary>
public static class EditorShortcuts {
    /// <summary>Whether <paramref name="event"/> is Ctrl+S (Cmd+S on macOS) pressed while the focus is in <paramref name="panel"/>.</summary>
    public static bool IsSave(Control panel, InputEvent @event) =>
        @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.S, ShiftPressed: false, AltPressed: false } key
        && key.IsCommandOrControlPressed()
        && HasFocusInside(panel);

    /// <summary>Whether the focus owner is <paramref name="panel"/> or one of its descendants.</summary>
    public static bool HasFocusInside(Control panel) {
        if (panel == null || !panel.IsVisibleInTree()) return false;
        var owner = panel.GetViewport()?.GuiGetFocusOwner();
        return owner != null && (owner == panel || panel.IsAncestorOf(owner));
    }
}
#endif
