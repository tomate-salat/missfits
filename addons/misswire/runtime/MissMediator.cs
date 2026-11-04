using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;

namespace Misswire;

/// <summary>
/// Wires the nodes of its scene together in the game, as its <see cref="Graph"/> says. The nodes
/// themselves connect nothing; they emit signals and offer methods.
/// <para>
/// It has no <c>[Tool]</c> on purpose: in the editor it is a placeholder, and the editor plugin runs
/// the bindings there instead.
/// </para>
/// </summary>
[GlobalClass]
public partial class MissMediator : Node {
    /// <summary>A binding could not do what it says. Each problem is reported once.</summary>
    [Signal]
    public delegate void WarnedEventHandler(string message);

    [Export]
    public MissBindingGraph Graph { get; set; }

    readonly Array<MissSignalRelay> _relays = [];
    readonly HashSet<string> _warned = [];

    /// <summary>
    /// The whole scene is in the tree by now, although later siblings may not be ready yet; that is
    /// why the first values go out deferred.
    /// </summary>
    public override void _Ready() => Wire();

    /// <summary>Coming back into the tree does not run <see cref="_Ready"/> again.</summary>
    public override void _EnterTree() {
        if (IsNodeReady()) Wire();
    }

    public override void _ExitTree() => Unwire();

    void Wire() {
        Unwire();
        if (Graph == null) return;
        for (var i = 0; i < Graph.Bindings.Count; i++) WireBinding(i);
        CallDeferred(MethodName.PushInitialValues);
    }

    void Unwire() {
        foreach (var relay in _relays) relay.Disconnect();
        _relays.Clear();
    }

    void WireBinding(int index) {
        // A binding whose wire was taken off has no source; the graph says so, the game skips it.
        if (Graph.Bindings[index] is not { } binding || binding.Source.IsEmpty) return;
        var source = MissWiring.SourceNode(this, binding);
        if (source == null) {
            Warn($"{MissWiring.Describe(binding)}: no node at '{binding.Source}'");
            return;
        }

        // A nested property reads through resources that may be swapped: whatever sets it off
        // listens to the resources it passes through afresh.
        var nested = binding.Kind == MissSourceKind.Property && MissWiring.IsNested(binding);
        if (binding.Kind == MissSourceKind.Property) {
            foreach (var trigger in binding.Triggers) ListenAt(trigger, nested ? MethodName.Refresh : MethodName.Trigger, index);
        }
        switch (binding.Kind) {
            case MissSourceKind.Property when !binding.FollowChanges:
                // The first value only: PushInitialValues sends it, nothing is listened to.
                break;
            case MissSourceKind.Property when nested:
                // Outfit:Hair changes when the node gets another outfit, or the outfit says changed.
                var outer = binding.Property.ToString().Split(':')[0];
                foreach (var (path, _) in MissWiring.ReplaceSignals(this, Graph, binding.Source, outer)) ListenAt(path, MethodName.Refresh, index);
                ListenToChain(source, binding, index);
                break;
            case MissSourceKind.Property:
                if (MissWiring.ChangeSignalOf(source, binding) is { IsEmpty: false } changed) Listen(source, changed, MethodName.Trigger, index);
                break;
            case MissSourceKind.Signal:
                Listen(source, binding.Signal, MethodName.Trigger, index);
                break;
            case MissSourceKind.ResourceSignal:
                ListenToResource(source, binding, index);
                foreach (var (path, _) in MissWiring.ReplaceSignals(this, Graph, binding.Source, binding.Property)) ListenAt(path, MethodName.Rewire, index);
                break;
        }
    }

    /// <summary>Listens to a signal given as a path, <c>../Model:UpdateOutfit</c>.</summary>
    void ListenAt(NodePath path, StringName method, int index) {
        if (path.GetSubNameCount() > 0 && GetNodeOrNull(MissWiring.NodePart(path)) is { } emitter) Listen(emitter, path.GetConcatenatedSubNames(), method, index);
        else Warn($"{MissWiring.Describe(Graph.Bindings[index])}: no signal at '{path}'");
    }

    void ListenToResource(Node source, MissBinding binding, int index) {
        // An empty property is fine: there is nothing to listen to until it gets a resource.
        if (MissWiring.HeldResource(source, binding) is { } resource) Listen(resource, binding.Signal, MethodName.ResourceFired, index);
    }

    /// <summary>
    /// The resource of a <see cref="MissSourceKind.ResourceSignal"/> binding fired. If the property
    /// holds another one by now, the old one no longer counts: the binding listens to the new one
    /// instead and runs nothing.
    /// </summary>
    public void ResourceFired(int binding, Array arguments) {
        if (Graph == null || binding < 0 || binding >= Graph.Bindings.Count || Graph.Bindings[binding] is not { } b) return;
        if (MissWiring.SourceNode(this, b) is not { } source) return;
        var listened = _relays.FirstOrDefault(r => r.Binding == binding && r.Method == MethodName.ResourceFired)?.Emitter;
        if (listened != MissWiring.HeldResource(source, b)) {
            Rewire(binding, []);
            return;
        }
        Trigger(binding, arguments);
    }

    void ListenToChain(Node source, MissBinding binding, int index) {
        foreach (var resource in MissWiring.Chain(source, binding)) Listen(resource, Resource.SignalName.Changed, MethodName.Refresh, index);
    }

    /// <summary>
    /// Something along a nested property changed: listen to the resources it passes through now,
    /// which may be other ones, and pass the value on.
    /// </summary>
    public void Refresh(int binding, Array arguments) {
        for (var i = _relays.Count - 1; i >= 0; i--) {
            if (_relays[i].Binding != binding || _relays[i].Method != MethodName.Refresh || _relays[i].Emitter is not Resource) continue;
            _relays[i].Disconnect();
            _relays.RemoveAt(i);
        }
        if (Graph.Bindings[binding] is { } b && MissWiring.SourceNode(this, b) is { } source) ListenToChain(source, b, binding);
        Trigger(binding, []);
    }

    void Listen(GodotObject emitter, StringName signal, StringName method, int index) {
        var relay = MissSignalRelay.Connect(emitter, signal, this, method, index, out var problem);
        if (relay != null) _relays.Add(relay);
        else Warn($"{MissWiring.Describe(Graph.Bindings[index])}: {problem}");
    }

    /// <summary>Runs a binding's steps, as if its source had fired.</summary>
    public void Trigger(int binding, Array arguments) {
        if (Graph == null || binding < 0 || binding >= Graph.Bindings.Count || Graph.Bindings[binding] is not { } b) return;
        if (MissWiring.SourceNode(this, b) is not { } source) return;
        MissWiring.Run(this, Graph, b, MissWiring.Input(source, b, arguments), Warn);
    }

    /// <summary>The property of a <see cref="MissSourceKind.ResourceSignal"/> binding holds another resource now.</summary>
    public void Rewire(int binding, Array arguments) {
        for (var i = _relays.Count - 1; i >= 0; i--) {
            if (_relays[i].Binding != binding || _relays[i].Method != MethodName.ResourceFired) continue;
            _relays[i].Disconnect();
            _relays.RemoveAt(i);
        }
        if (Graph.Bindings[binding] is { } b && MissWiring.SourceNode(this, b) is { } source) ListenToResource(source, b, binding);
    }

    void PushInitialValues() {
        if (Graph == null || !IsInsideTree()) return;
        for (var i = 0; i < Graph.Bindings.Count; i++) {
            if (Graph.Bindings[i] is { PushInitial: true, Kind: not MissSourceKind.Signal }) Trigger(i, []);
        }
    }

    void Warn(string message) {
        if (!_warned.Add(message)) return;
        GD.PushWarning($"{Name}: {message}");
        EmitSignal(SignalName.Warned, message);
    }
}
