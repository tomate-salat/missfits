#if TOOLS
using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace Misswire.Editor;

/// <summary>
/// Runs the bindings of the edited scene in the editor, where every <see cref="MissMediator"/> is
/// only a placeholder.
/// <para>
/// A source that really runs in the editor (a <c>[Tool]</c> script, or no script at all) is listened
/// to through its signal, as in the game. Any other one cannot emit, so after each edit its value is
/// read and compared instead. A source always goes one of the two ways, never both, or its steps
/// would run twice.
/// </para>
/// <para>
/// Nothing is remembered about which way a source goes: every sync works it out afresh and connects
/// or disconnects accordingly. A reload, a change of scene and a script gaining or losing
/// <c>[Tool]</c> are therefore all the same case. Every field is a Godot type, so a reload keeps it.
/// </para>
/// </summary>
[Tool]
public partial class MissEditorSync : RefCounted {
    /// <summary>How often one sync compares again, for chains where one binding feeds the next.</summary>
    public const int MaxPasses = 8;

    const string MediatorClass = nameof(MissMediator);

    [Signal]
    public delegate void WarnedEventHandler(string message);

    readonly Array<MissSignalRelay> _relays = [];

    /// <summary>What each compared binding saw last, by <see cref="Key"/>.</summary>
    readonly Dictionary _seen = [];

    bool _pending;
    bool _edited;
    int _depth;

    /// <summary>Lost on a reload, which only means a problem may be reported once more.</summary>
    readonly HashSet<string> _warned = [];

    /// <summary>How many syncs have run; for the tests.</summary>
    public int Syncs { get; private set; }

    public int RelayCount => _relays.Count;

    // ---- triggers ------------------------------------------------------------------------------

    /// <summary>Something was edited, undone or redone. Several edits in one frame make one sync.</summary>
    public void OnEdited() {
        _edited = true;
        Request();
    }

    public void OnSceneChanged(Node root) => Request();

    public void Request() {
        if (_pending) return;
        _pending = true;
        CallDeferred(MethodName.Sync);
    }

    /// <summary>A relay fired: a source running in the editor emitted its signal.</summary>
    public void Fired(GodotObject mediator, int binding, Array arguments) {
        if (mediator is not Node node || Graph(node) is not { } graph || binding >= graph.Bindings.Count) return;
        if (graph.Bindings[binding] is not { EditorActive: true } b || MissWiring.SourceNode(node, b) is not { } source) return;
        Run(node, b, MissWiring.Input(source, b, arguments));
        // What the steps set may feed a binding that is compared rather than listened to.
        Request();
    }

    public void Shutdown() {
        foreach (var relay in _relays) relay.Disconnect();
        _relays.Clear();
        _seen.Clear();
    }

    // ---- sync ----------------------------------------------------------------------------------

    public void Sync() {
        _pending = false;
        Syncs++;
        var edited = _edited;
        _edited = false;

        var root = EditorInterface.Singleton.GetEditedSceneRoot();
        var mediators = new List<Node>();
        if (root != null) FindMediators(root, mediators);

        Reconcile(mediators);
        Forget(mediators);
        for (var pass = 0; pass < MaxPasses; pass++) {
            if (!Compare(mediators, everyEdit: edited && pass == 0)) return;
        }
        Warn($"bindings were still changing each other after {MaxPasses} rounds; is there a loop?");
    }

    public static void FindMediators(Node node, List<Node> found) {
        if (IsMediator(node)) found.Add(node);
        foreach (var child in node.GetChildren()) FindMediators(child, found);
    }

    /// <summary>Whether this is a <see cref="MissMediator"/>, which in the editor is only a placeholder.</summary>
    public static bool IsMediator(GodotObject obj) =>
        obj is Node && obj.GetScript().As<Script>() is { } script && script.GetGlobalName() == MediatorClass;

    public static MissBindingGraph Graph(Node mediator) => mediator.Get(MissMediator.PropertyName.Graph).As<MissBindingGraph>();

    /// <summary>Whether an object's own code runs in the editor, so that it emits and can be called.</summary>
    public static bool Runs(GodotObject obj) => obj.GetScript().As<Script>() is not { } script || script.IsTool();

    /// <summary>The object and signal a binding is listened to by in the editor, or null if it has to be compared.</summary>
    static (GodotObject Emitter, StringName Signal)? Emitter(Node source, MissBinding binding) {
        switch (binding.Kind) {
            // A nested property (Outfit:Hair) is compared: what reads its value may change in more places than one signal tells.
            case MissSourceKind.Property when !MissWiring.IsNested(binding) && MissWiring.ChangeSignalOf(source, binding) is { IsEmpty: false } changed && Runs(source):
                return (source, changed);
            case MissSourceKind.Signal when Runs(source):
                return (source, binding.Signal);
            case MissSourceKind.ResourceSignal when MissWiring.HeldResource(source, binding) is { } resource && Runs(resource):
                return (resource, binding.Signal);
            default:
                return null;
        }
    }

    /// <summary>Connects what should be listened to now and lets go of the rest.</summary>
    void Reconcile(List<Node> mediators) {
        var wanted = new List<(Node Mediator, int Binding, GodotObject Emitter, StringName Signal)>();
        foreach (var mediator in mediators) {
            if (Graph(mediator) is not { } graph) continue;
            for (var i = 0; i < graph.Bindings.Count; i++) {
                if (graph.Bindings[i] is not { EditorActive: true } binding) continue;
                if (MissWiring.SourceNode(mediator, binding) is not { } source) continue;
                if (Emitter(source, binding) is { } emitter) wanted.Add((mediator, i, emitter.Emitter, emitter.Signal));
                // Further triggers fire in the editor only from nodes that run there.
                if (binding.Kind == MissSourceKind.Property) {
                    foreach (var trigger in binding.Triggers) {
                        if (trigger.GetSubNameCount() > 0 && mediator.GetNodeOrNull(MissWiring.NodePart(trigger)) is { } node && Runs(node)) {
                            wanted.Add((mediator, i, node, trigger.GetConcatenatedSubNames()));
                        }
                    }
                }
            }
        }

        for (var i = _relays.Count - 1; i >= 0; i--) {
            var relay = _relays[i];
            var index = wanted.FindIndex(w => w.Mediator == relay.Context && w.Binding == relay.Binding
                                              && w.Emitter == relay.Emitter && w.Signal == relay.Signal);
            if (index >= 0 && IsInstanceValid(relay.Emitter)) {
                wanted.RemoveAt(index);
                continue;
            }
            relay.Disconnect();
            _relays.RemoveAt(i);
        }

        foreach (var (mediator, binding, emitter, signal) in wanted) {
            var relay = MissSignalRelay.Connect(emitter, signal, this, MethodName.Fired, binding, out var problem, mediator);
            if (relay != null) _relays.Add(relay);
            else Warn($"{MissWiring.Describe(Graph(mediator).Bindings[binding])}: {problem}");
        }
    }

    /// <summary>Drops what was seen of mediators no longer in the edited scene; reopening it starts over.</summary>
    void Forget(List<Node> mediators) {
        var prefixes = new HashSet<string>();
        foreach (var mediator in mediators) prefixes.Add($"{mediator.GetInstanceId()}:");
        foreach (var key in new Array(_seen.Keys)) {
            var text = key.AsString();
            if (!prefixes.Contains(text[..(text.IndexOf(':') + 1)])) _seen.Remove(key);
        }
    }

    /// <returns>Whether any binding ran.</returns>
    bool Compare(List<Node> mediators, bool everyEdit) {
        var ran = false;
        foreach (var mediator in mediators) {
            if (Graph(mediator) is not { } graph) continue;
            for (var i = 0; i < graph.Bindings.Count; i++) {
                if (graph.Bindings[i] is not { EditorActive: true } binding) continue;
                if (MissWiring.SourceNode(mediator, binding) is not { } source) continue;

                var key = Key(mediator, binding);
                var compared = binding.Kind != MissSourceKind.Signal && Emitter(source, binding) == null;
                var fingerprint = compared ? Fingerprint(source, binding) : "";
                var first = !_seen.TryGetValue(key, out var before);
                _seen[key] = fingerprint;

                var run = first ? binding.PushInitial && binding.Kind != MissSourceKind.Signal
                    : !compared ? false
                    : binding.EditorTrigger == MissEditorTrigger.EveryEdit ? everyEdit
                    : fingerprint != before.AsString();
                if (!run) continue;
                Run(mediator, binding, MissWiring.Input(source, binding, []));
                ran = true;
            }
        }
        return ran;
    }

    static string Key(Node mediator, MissBinding binding) => $"{mediator.GetInstanceId()}:{binding.Id}";

    /// <summary>
    /// What "changed" means for a source that has to be compared: another value of the property
    /// (another object, for a resource), or for a resource signal anything inside the resource.
    /// </summary>
    static string Fingerprint(Node source, MissBinding binding) {
        var value = MissWiring.Read(source, binding.Property);
        if (binding.Kind == MissSourceKind.ResourceSignal) return GD.VarToStr(value);
        return value.VariantType == Variant.Type.Object
            ? $"object {value.AsGodotObject()?.GetInstanceId() ?? 0}"
            : GD.VarToStr(value);
    }

    void Run(Node mediator, MissBinding binding, Array input) {
        if (_depth >= MaxPasses) {
            Warn($"{MissWiring.Describe(binding)}: bindings set each other off in a loop");
            return;
        }
        _depth++;
        try {
            MissWiring.Run(mediator, Graph(mediator), binding, input, Warn, target => Runs(target));
        } finally {
            _depth--;
        }
    }

    void Warn(string message) {
        if (!_warned.Add(message)) return;
        GD.PushWarning($"misswire: {message}");
        EmitSignal(SignalName.Warned, message);
    }
}
#endif
