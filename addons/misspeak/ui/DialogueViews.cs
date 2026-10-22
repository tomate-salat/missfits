using System;

namespace Misspeak;

// The parts a DialogueBox is made of. The box finds them among its descendants by these interfaces,
// so any Control can be one: put your own in the scene in place of the one it replaces. A part that
// is missing is simply not shown.

/// <summary>Shows who is speaking.</summary>
public interface IDialogueSpeakerView {
    /// <param name="speaker">The speaker's resource, for more than the name — or null for nobody.</param>
    /// <param name="name">The speaker's name, translated; empty for nobody.</param>
    void ShowSpeaker(DialogueSpeaker speaker, string name);
}

/// <summary>Shows the text of a line, at once or written out bit by bit.</summary>
public interface IDialogueTextView {
    /// <summary>Shows a line, starting to write it out if this view does that. Empty clears it.</summary>
    void ShowText(string text);

    /// <summary>Whether the line on show is still being written out.</summary>
    bool Writing { get; }

    /// <summary>Shows the rest of the line at once.</summary>
    void FinishWriting();

    /// <summary>The line has been written out — by itself, or by <see cref="FinishWriting"/>.</summary>
    event Action WritingFinished;
}

/// <summary>Offers choices and tells which one was picked.</summary>
public interface IDialogueChoiceView {
    /// <summary>Offers these, in order. None clears what was offered.</summary>
    void ShowChoices(string[] choices);

    /// <summary>One was picked, by its place among those shown.</summary>
    event Action<int> Chosen;
}

/// <summary>Tells the player that a press carries on.</summary>
public interface IDialogueContinueHint {
    void ShowHint(bool show);
}
