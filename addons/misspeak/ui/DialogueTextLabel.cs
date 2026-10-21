using System;
using Godot;

namespace Misspeak;

/// <summary>The text of a line, written out letter by letter. BBCode works.</summary>
[GlobalClass]
public partial class DialogueTextLabel : RichTextLabel, IDialogueTextView {
    /// <summary>How fast a line is written out, in characters per second. 0 shows it at once.</summary>
    [Export(PropertyHint.Range, "0,200,1,or_greater")]
    public float CharactersPerSecond { get; set; } = 45f;

    public event Action WritingFinished;

    double _written;

    public bool Writing => VisibleCharacters >= 0 && VisibleCharacters < GetTotalCharacterCount();

    public void ShowText(string text) {
        Text = text;
        _written = 0;
        VisibleCharacters = CharactersPerSecond > 0 && text != "" ? 0 : -1;
        if (!Writing) WritingFinished?.Invoke();
    }

    public void FinishWriting() {
        if (Writing) Finish();
    }

    void Finish() {
        VisibleCharacters = -1;
        WritingFinished?.Invoke();
    }

    public override void _Process(double delta) {
        if (!Writing || !IsVisibleInTree()) return;

        _written += delta * CharactersPerSecond;
        // Once the last character shows it is no longer Writing, so FinishWriting would do nothing.
        if ((int) _written >= GetTotalCharacterCount()) Finish();
        else VisibleCharacters = (int) _written;
    }
}
