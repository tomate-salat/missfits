#if TOOLS
using Godot;
using Misscore.Editor;

namespace Misswire.Editor;

/// <summary>A group of boxes in the graph: a frame that moves what it holds, with a title and a tint.</summary>
[Tool]
public partial class WireGroupFrame : GraphFrame {
    /// <summary>The frame was right-clicked; the position is in screen coordinates.</summary>
    [Signal]
    public delegate void MenuRequestedEventHandler(StringName frameName, Vector2 screenPosition);

    public string GroupId { get; private set; } = "";

    public void Build(MissGraphGroupInfo group) {
        GroupId = group.Id;
        Title = group.Title;
        TintColorEnabled = true;
        TintColor = new Color(group.Color, 0.35f);
        AutoshrinkEnabled = true;
        Theme = ZoomFonts.ThemeFor(this);
    }

    public override void _GuiInput(InputEvent @event) {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } click) return;
        EmitSignal(SignalName.MenuRequested, Name, GetScreenPosition() + click.Position * GetGlobalTransformWithCanvas().Scale);
        AcceptEvent();
    }
}
#endif
