#if TOOLS
using System.Linq;
using Godot;

namespace Misscore.Editor;

/// <summary>
/// Base of the boxes in the Missfits graph editors that draw their own outline instead of
/// GraphNode's title bar.
/// </summary>
[Tool]
public abstract partial class MissGraphBox : GraphNode {
    static readonly Color HintColor = new("#8ab4f8");

    bool _hinted;

    /// <summary>
    /// Marks the box, quietly, as where what is selected leads — the target of the picked option or
    /// transition, or of the selected reroute. A thin outline a little outside its own.
    /// </summary>
    public bool Hinted {
        get => _hinted;
        set {
            if (_hinted == value) return;
            _hinted = value;
            QueueRedraw();
        }
    }

    bool _awaited;

    /// <summary>
    /// Marks the box, while a game runs, as where the game may come back to — the section an option
    /// that leads back would return to. The same thin outline as <see cref="Hinted"/>, in the colour
    /// of "running".
    /// </summary>
    public bool Awaited {
        get => _awaited;
        set {
            if (_awaited == value) return;
            _awaited = value;
            QueueRedraw();
        }
    }

    public override void _Draw() {
        if (_awaited) Outline(GraphRow.Running, 4);
        if (_hinted) Outline(HintColor, _awaited ? 8 : 4);
    }

    void Outline(Color color, float outside) {
        var style = new StyleBoxFlat { DrawCenter = false, BorderColor = new Color(color, 0.7f) };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(9);
        DrawStyleBox(style, new Rect2(Vector2.Zero, Size).Grow(outside));
    }

    /// <summary>
    /// Takes the title bar out of the picture. The title itself stays set — it is what the editor
    /// and tooltips refer to — but is not drawn. Without the bar the body has no upper edge of its
    /// own, so the box is expected to bring a whole outline as its <c>panel</c> stylebox.
    /// </summary>
    protected void HideTitlebar() {
        AddThemeStyleboxOverride("titlebar", new StyleBoxEmpty());
        AddThemeStyleboxOverride("titlebar_selected", new StyleBoxEmpty());

        foreach (var label in GetTitlebarHBox().GetChildren(includeInternal: true).OfType<Label>()) label.Visible = false;
        var flatten = new Callable(this, MethodName.FlattenTitlebar);
        if (!IsConnected(Container.SignalName.SortChildren, flatten)) Connect(Container.SignalName.SortChildren, flatten);
        FlattenTitlebar();
    }

    /// <summary>
    /// Keeps the unused title bar at no height. GraphNode places the rows by the bar's minimum
    /// height, which is nothing once its label is hidden — but draws the body below the bar's actual
    /// height, and a bar never shrinks by itself. If it was ever laid out while its label still took
    /// up room, the body would be drawn that much lower than the rows, with the first row sticking
    /// out on top.
    /// </summary>
    protected void FlattenTitlebar() {
        var bar = GetTitlebarHBox();
        if (bar == null || bar.Size.Y <= 0) return;

        bar.Size = new Vector2(bar.Size.X, 0);
        QueueRedraw();
    }
}
#endif
