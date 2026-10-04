using Godot;
using Misscore;

namespace Misspeak;

/// <summary>
/// Someone who speaks in a <see cref="Dialogue"/>. Listing the speakers on the dialogue lets the
/// editor offer them for each line instead of having the name typed every time, and flags a line
/// whose speaker is not among them. A line names its speaker by <see cref="Name"/>.
/// </summary>
[GlobalClass, Tool]
public partial class DialogueSpeaker : MissResource {
    /// <summary>What a line's <see cref="DialogueLine.Speaker"/> says to mean this speaker. Translated like any other text.</summary>
    [Export]
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}
