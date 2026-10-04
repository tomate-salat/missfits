#if TOOLS
using Godot;
using Misscore;
using Misscore.Editor;

namespace Misspeak.Editor;

/// <summary>
/// Adds an "Open in Misspeak" button to a dialogue's inspector, and gives the <see cref="BbParam{T}"/>
/// of every node in the open dialogue its own editor.
/// </summary>
[Tool]
public partial class DialogueInspectorPlugin : EditorInspectorPlugin {
    // Untyped so an assembly reload can restore them — see ReloadSafe.
    GodotObject _plugin;
    GodotObject _current;

    MisspeakEditorPlugin Plugin => ReloadSafe.Get<MisspeakEditorPlugin>(ref _plugin);
    Dialogue Current => ReloadSafe.Get<Dialogue>(ref _current);

    public void Attach(MisspeakEditorPlugin plugin) => _plugin = plugin;

    public override bool _CanHandle(GodotObject @object) => @object is Dialogue or DialogueLine or IBbParamHost;

    public override void _ParseBegin(GodotObject @object) {
        _current = @object as Dialogue;
        if (_current == null) return;

        var button = new Button { Text = "Open in Misspeak" };
        // A native method callable rather than +=, which would not survive an assembly reload.
        button.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnOpenPressed));
        AddCustomControl(button);
    }

    public override bool _ParseProperty(GodotObject @object, Variant.Type type, string name, PropertyHint hintType,
        string hintString, PropertyUsageFlags usageFlags, bool wide) {
        // A line of the open dialogue picks its speaker from that dialogue's speakers.
        if (SpeakerEditorFor(Plugin?.OpenDialogueOf(@object), name) is { } speakers) {
            AddPropertyEditor(name, speakers);
            return true;
        }

        // Null for anything but a parameter, and for one another addon's panel takes care of.
        var editor = BbParamEditorProperty.CreateFor(Plugin?.Blackboard, @object, hintString);
        if (editor == null) return false;

        AddPropertyEditor(name, editor);
        return true;
    }

    /// <summary>
    /// The dropdown for a line's speaker — or null when the property is not that, or the line
    /// belongs to no open dialogue, in which case the plain text field stays.
    /// </summary>
    public static SpeakerEditorProperty SpeakerEditorFor(Dialogue dialogue, string property) {
        if (dialogue == null || property != nameof(DialogueLine.Speaker)) return null;

        var listed = new System.Collections.Generic.List<string>();
        foreach (var speaker in dialogue.Speakers) {
            if (!string.IsNullOrEmpty(speaker?.Name) && !listed.Contains(speaker.Name)) listed.Add(speaker.Name);
        }
        var editor = new SpeakerEditorProperty();
        editor.Offer(listed, dialogue.SpeakerNames());
        return editor;
    }

    void OnOpenPressed() => Plugin?.OpenDialogue(Current);
}
#endif
