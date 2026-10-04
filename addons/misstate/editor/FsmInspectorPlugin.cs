#if TOOLS
using Godot;
using Misscore;
using Misscore.Editor;

namespace Misstate.Editor;

/// <summary>
/// Adds an "Open in Misstate" button to an Fsm's inspector, and gives the <see cref="BbParam{T}"/>
/// of every node in the open machine its own editor.
/// </summary>
[Tool]
public partial class FsmInspectorPlugin : EditorInspectorPlugin {
    // Untyped so an assembly reload can restore them — see ReloadSafe.
    GodotObject _plugin;
    GodotObject _current;

    MisstateEditorPlugin Plugin => ReloadSafe.Get<MisstateEditorPlugin>(ref _plugin);
    Fsm Current => ReloadSafe.Get<Fsm>(ref _current);

    public void Attach(MisstateEditorPlugin plugin) => _plugin = plugin;

    public override bool _CanHandle(GodotObject @object) => @object is Fsm or IBbParamHost;

    public override void _ParseBegin(GodotObject @object) {
        _current = @object as Fsm;
        if (_current == null) return;

        var button = new Button { Text = "Open in Misstate" };
        // A native method callable rather than +=, which would not survive an assembly reload.
        button.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnOpenPressed));
        AddCustomControl(button);
    }

    public override bool _ParseProperty(GodotObject @object, Variant.Type type, string name, PropertyHint hintType,
        string hintString, PropertyUsageFlags usageFlags, bool wide) {
        // Null for anything but a parameter, and for one another addon's panel takes care of.
        var editor = BbParamEditorProperty.CreateFor(Plugin?.Blackboard, @object, hintString);
        if (editor == null) return false;

        AddPropertyEditor(name, editor);
        return true;
    }

    void OnOpenPressed() => Plugin?.OpenMachine(Current);
}
#endif