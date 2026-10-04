#if TOOLS
using Godot;
using Misscore.Editor;

namespace Misspeak.Editor;

/// <summary>
/// Entry point of the addon. For now all it adds to the editor is the translation parser, which puts
/// the texts of dialogues into a translation template.
/// <para>
/// References to the addon's own classes are stored untyped, as everywhere in the Missfits editors:
/// a reload of the assembly keeps the objects and rebuilds only their C# side (see <see cref="ReloadSafe"/>).
/// </para>
/// </summary>
[Tool]
public partial class MisspeakEditorPlugin : EditorPlugin {
    GodotObject _parser;

    public override void _EnterTree() {
        var parser = new DialogueTranslationParser();
        _parser = parser;
        AddTranslationParserPlugin(parser);
    }

    public override void _ExitTree() {
        if (ReloadSafe.Get<DialogueTranslationParser>(ref _parser) is { } parser) RemoveTranslationParserPlugin(parser);
        _parser = null;
    }
}
#endif
