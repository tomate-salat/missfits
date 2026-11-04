using Godot;
using Misscore;

namespace Misswire;

/// <summary>What a binding listens to.</summary>
public enum MissSourceKind {
    /// <summary>
    /// The value of <see cref="MissBinding.Property"/>. In the game it is read whenever the node emits
    /// <see cref="MissBinding.ChangeSignal"/>; without one, only <see cref="MissBinding.PushInitial"/>
    /// passes it on.
    /// </summary>
    Property,

    /// <summary><see cref="MissBinding.Signal"/> of the node itself, with its arguments.</summary>
    Signal,

    /// <summary>
    /// <see cref="MissBinding.Signal"/> of the resource that <see cref="MissBinding.Property"/> holds,
    /// such as <c>changed</c> of an outfit. When the node emits <see cref="MissBinding.ChangeSignal"/>,
    /// the binding lets go of the old resource and listens to the new one.
    /// </summary>
    ResourceSignal,
}

/// <summary>When a binding runs in the editor, for a source that cannot emit there.</summary>
public enum MissEditorTrigger {
    /// <summary>After an edit, when the source's value differs from what it was before.</summary>
    ValueChanged,

    /// <summary>
    /// After every edit, whatever the value: for a resource without <c>[Tool]</c> whose inside
    /// changes while the reference stays the same.
    /// </summary>
    EveryEdit,
}

/// <summary>
/// One source and what happens when it fires: its <see cref="Steps"/>, top to bottom. Node paths
/// are relative to the <see cref="MissMediator"/>.
/// </summary>
[GlobalClass, Tool]
public partial class MissBinding : MissResource {
    /// <summary>Stable identity, by which an editor tells the bindings apart.</summary>
    [Export]
    public string Id { get; set; } = BlackboardEntry.NewId();

    [Export]
    public MissSourceKind Kind { get; set; } = MissSourceKind.Property;

    [Export]
    public NodePath Source { get; set; } = new();

    /// <summary>The property read (<see cref="MissSourceKind.Property"/>) or holding the resource (<see cref="MissSourceKind.ResourceSignal"/>).</summary>
    [Export]
    public StringName Property { get; set; } = new();

    /// <summary>The signal listened to (<see cref="MissSourceKind.Signal"/>, <see cref="MissSourceKind.ResourceSignal"/>).</summary>
    [Export]
    public StringName Signal { get; set; } = new();

    /// <summary>
    /// The node's signal for a new value of <see cref="Property"/>, such as <c>OutfitChanged</c>.
    /// Left empty, <c>&lt;Property&gt;Changed</c> or <c>&lt;property&gt;_changed</c> is used if the
    /// node has one (<see cref="MissWiring.ChangeSignalOf"/>).
    /// </summary>
    [Export]
    public StringName ChangeSignal { get; set; } = new();

    /// <summary>
    /// Whether the game follows the property's changes (<see cref="ChangeSignal"/>, and for a
    /// property inside a resource the resource's <c>changed</c>). Turned off, the game gets the
    /// first value only. The editor follows changes either way, as long as the binding is active there.
    /// </summary>
    [Export]
    public bool FollowChanges { get; set; } = true;

    /// <summary>
    /// Further signals that set a property binding off, as paths (<c>../Model:UpdateOutfit</c>):
    /// the property is read again and the steps run. Their arguments are ignored.
    /// </summary>
    [Export]
    public Godot.Collections.Array<NodePath> Triggers { get; set; } = [];

    /// <summary>
    /// Runs the steps once the scene is ready, without waiting for a signal: with the property's
    /// value, or for <see cref="MissSourceKind.ResourceSignal"/> without arguments. Plain signals
    /// have nothing to start with and ignore it.
    /// </summary>
    [Export]
    public bool PushInitial { get; set; } = true;

    /// <summary>Whether the binding runs in the editor too, so that a change shows right away.</summary>
    [Export]
    public bool EditorActive { get; set; }

    [Export]
    public MissEditorTrigger EditorTrigger { get; set; } = MissEditorTrigger.ValueChanged;

    [Export]
    public Godot.Collections.Array<MissBindingStep> Steps { get; set; } = [];

    /// <summary>Where the graph editor shows the binding's box.</summary>
    [Export]
    public Vector2 GraphPosition { get; set; }

    /// <summary>Which box of the source node the wire leaves in the graph editor; empty for its main box.</summary>
    [Export]
    public string SourceBox { get; set; } = "";

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(Id) or nameof(GraphPosition) or nameof(SourceBox)) property["usage"] = (int) PropertyUsageFlags.Storage;
    }
}
