#if TOOLS
using Godot;

namespace Misspeak.Editor;

/// <summary>
/// Lets <i>Project Settings → Localization → Template Generation</i> read dialogues: add a dialogue's
/// <c>.tres</c> to the files there, and its speakers, lines and choices end up in the template.
/// Which texts those are is decided by <see cref="Dialogue.TranslatableTexts"/>; this only hands
/// them over.
/// </summary>
[Tool]
public partial class DialogueTranslationParser : EditorTranslationParserPlugin {
    public override string[] _GetRecognizedExtensions() => ["tres"];

    public override Godot.Collections.Array<string[]> _ParseFile(string path) {
        var messages = new Godot.Collections.Array<string[]>();

        // Every .tres comes by here, whatever it holds.
        if (ResourceLoader.Load(path) is not Dialogue dialogue) return messages;

        // Per message: the text, its context, its plural, a comment for the translator.
        foreach (var (text, context) in dialogue.TranslatableTexts()) messages.Add([text, context, "", ""]);
        return messages;
    }
}
#endif
