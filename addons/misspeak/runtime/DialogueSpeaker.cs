using Godot;
using Misscore;

namespace Misspeak;

/// <summary>
/// Someone who speaks: a resource of its own, saved as a <c>.tres</c> and shared by every dialogue
/// they speak in. A line points at its speaker (<see cref="DialogueLine.SpokenBy"/>), so renaming
/// one here renames them everywhere. Whatever shows a dialogue gets the speaker along with each
/// line, which is where more about them — a portrait, a colour — can go.
/// </summary>
[GlobalClass, Tool]
public partial class DialogueSpeaker : MissResource {
    /// <summary>The name shown for them. Translated like any other text.</summary>
    [Export]
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}
