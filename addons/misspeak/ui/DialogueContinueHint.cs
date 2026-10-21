using Godot;

namespace Misspeak;

/// <summary>A label — "▼" by default — shown while a press carries on.</summary>
[GlobalClass]
public partial class DialogueContinueHint : Label, IDialogueContinueHint {
    public void ShowHint(bool show) => Visible = show;
}
