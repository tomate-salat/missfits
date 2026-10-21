using Godot;

namespace Misspeak;

/// <summary>The speaker's name as a label, hidden while nobody is named.</summary>
[GlobalClass]
public partial class DialogueSpeakerLabel : Label, IDialogueSpeakerView {
    public void ShowSpeaker(string speaker) {
        Text = speaker;
        Visible = speaker != "";
    }
}
