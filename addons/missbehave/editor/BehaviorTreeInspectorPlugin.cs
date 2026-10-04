#if TOOLS
using Godot;
using Misscore;
using Misscore.Editor;

namespace Missbehave.Editor;

/// <summary>
/// Adds an "Open in Missbehave" button to a BehaviorTree's inspector — double-clicking the resource
/// already opens the panel; this is the discoverable second entry point — and gives every
/// <see cref="BbParam{T}"/> of a node its own editor.
/// </summary>
[Tool]
public partial class BehaviorTreeInspectorPlugin : EditorInspectorPlugin {
    // Untyped so an assembly reload can restore them — see ReloadSafe.
    GodotObject _plugin;
    GodotObject _current;

    MissbehaveEditorPlugin Plugin => ReloadSafe.Get<MissbehaveEditorPlugin>(ref _plugin);
    BehaviorTree Current => ReloadSafe.Get<BehaviorTree>(ref _current);

    public void Attach(MissbehaveEditorPlugin plugin) => _plugin = plugin;

    public override bool _CanHandle(GodotObject @object) => @object is BehaviorTree or ABehaviorNode;

    public override void _ParseBegin(GodotObject @object) {
        _current = @object as BehaviorTree;
        if (_current == null) return;

        var button = new Button { Text = "Open in Missbehave" };
        // A native method callable rather than +=, which would not survive an assembly reload.
        button.Connect(BaseButton.SignalName.Pressed, new Callable(this, MethodName.OnOpenPressed));
        AddCustomControl(button);
    }

    public override bool _ParseProperty(GodotObject @object, Variant.Type type, string name, PropertyHint hintType,
        string hintString, PropertyUsageFlags usageFlags, bool wide) {
        if (@object is not ABehaviorNode || hintString != BbParams.HintString) return false;

        var editor = new BbParamEditorProperty();
        editor.Attach(Plugin?.Blackboard);
        AddPropertyEditor(name, editor);
        return true;
    }

    void OnOpenPressed() => Plugin?.OpenTree(Current);
}
#endif
