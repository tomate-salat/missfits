using Godot;

namespace Misscore;

/// <summary>
/// Base of the scene nodes that drive something for one actor — a behavior tree, a state machine.
/// It holds what they all share: who the actor is, when and how often to tick, switching the whole
/// thing off and on, and a blackboard filled from the source's entries.
/// <para>
/// The Inspector lists the source's blackboard entries under <i>Blackboard</i>, so each runner can
/// give them its own values — including scene nodes, which a resource cannot hold. Those values are
/// stored by entry id and survive renaming the entry. A tool script for that alone: in the editor it
/// does nothing else.
/// </para>
/// </summary>
[GlobalClass, Tool]
public abstract partial class MissRunner : Node, IMissRunner, ISerializationListener {
    /// <summary>When the runner ticks on its own.</summary>
    public enum ProcessThread {
        /// <summary>Once per rendered frame, in <c>_Process</c>, with a variable delta.</summary>
        EveryFrame,

        /// <summary>Once per physics step, in <c>_PhysicsProcess</c>, with a fixed delta.</summary>
        Physics,

        /// <summary>Not on its own — something else calls <see cref="Tick"/>.</summary>
        Manual,
    }

    /// <summary>Inspector section listing the source's entries by name.</summary>
    public const string BlackboardGroup = "Blackboard/";

    /// <summary>Where overrides are stored, by entry id. Not shown.</summary>
    public const string OverridePrefix = "blackboard_overrides/";

    // ---- the source --------------------------------------------------------------------------

    /// <summary>
    /// The resource this runner plays, untyped. A subclass exposes it as an exported property of its
    /// own type through <see cref="SourceAs{T}"/> and <see cref="SetSource"/>.
    /// <para>
    /// Untyped because of assembly reloads — which, as a tool script, happen to a runner in an open
    /// scene on every build. Restoring a field typed as one of this assembly's own classes can throw
    /// InvalidCastException, since the resource may not have its script back yet; a plain
    /// <see cref="GodotObject"/> restores without a cast, and the typed instance is picked up again on
    /// first use.
    /// </para>
    /// </summary>
    GodotObject _source;

    /// <summary>
    /// Makes the typed property read as empty while a reload saves and restores the properties. The
    /// generated code would otherwise save the typed resource through the property and cast it back.
    /// </summary>
    bool _reloading;

    Callable SourceChanged => new(this, MethodName.OnSourceChanged);

    protected T SourceAs<T>() where T : Resource {
        if (_reloading) return null;
        if (_source is T typed) return typed;
        if (_source == null || !IsInstanceValid(_source)) return null;
        if (InstanceFromId(_source.GetInstanceId()) is not T current) return null;

        _source = current;
        FollowSource();
        return current;
    }

    protected void SetSource(Resource value) {
        if (_reloading || ReferenceEquals(SourceAs<Resource>(), value)) return;
        if (Engine.IsEditorHint() && _source is Resource old && IsInstanceValid(old)
                                  && old.IsConnected(Resource.SignalName.Changed, SourceChanged)) {
            old.Disconnect(Resource.SignalName.Changed, SourceChanged);
        }
        _source = value;
        FollowSource();
        NotifyPropertyListChanged();
    }

    /// <summary>
    /// The source's entries decide which fields the Inspector shows, so edits to them are followed.
    /// Checked rather than assumed: the connection is native and outlives an assembly reload, so the
    /// source picked up again afterwards is usually still connected.
    /// </summary>
    void FollowSource() {
        if (!Engine.IsEditorHint() || _source is not Resource source
                                   || source.IsConnected(Resource.SignalName.Changed, SourceChanged)) return;
        source.Connect(Resource.SignalName.Changed, SourceChanged);
    }

    void OnSourceChanged() => NotifyPropertyListChanged();

    public virtual void OnBeforeSerialize() => _reloading = true;

    public virtual void OnAfterDeserialize() => _reloading = false;

    /// <summary>The source's blackboard, whatever kind of resource it is.</summary>
    IBlackboardSource Entries => SourceAs<Resource>() as IBlackboardSource;

    // ---- settings ----------------------------------------------------------------------------

    /// <summary>
    /// The node being acted upon — what nodes reach through the context. Left empty, it is this
    /// runner's parent.
    /// </summary>
    [Export]
    public Node Actor { get; set; }

    /// <summary>
    /// When the runner ticks: on every rendered frame, on every physics step, or not on its own, in
    /// which case your own code calls <see cref="Tick"/>. Physics by default, so nodes see the same
    /// delta that movement and collisions run on.
    /// </summary>
    [Export]
    public ProcessThread Thread { get; set; } = ProcessThread.Physics;

    /// <summary>
    /// Ticks only on every Nth step of the chosen <see cref="Thread"/>, to spread many actors out over
    /// time; 1 ticks on every one. The deltas of the skipped steps are added up and handed to the tick
    /// that does run, so no time goes missing.
    /// </summary>
    [Export(PropertyHint.Range, "1,60,1,or_greater")]
    public int TickRate { get; set; } = 1;

    /// <summary>
    /// Switching this off stops the runner and interrupts whatever was running. Safe to do from inside
    /// a tick — e.g. from a leaf via <see cref="Stop"/>: the current tick finishes first, and the
    /// interrupt follows right after it.
    /// </summary>
    [Export]
    public bool Enabled {
        get => _enabled;
        set {
            _enabled = value;
            if (!IsInsideTree() || Engine.IsEditorHint()) return;
            ApplyProcessMode();
            if (_enabled) return;

            // Interrupting mid-tick would tear down the very branch that is still unwinding.
            if (_ticking) _interruptAfterTick = true;
            else Interrupt();
        }
    }

    [Signal]
    public delegate void StatusChangedEventHandler(int status);

    public Blackboard Blackboard { get; private set; } = new();
    public MissStatus Status { get; private set; } = MissStatus.Failure;

    bool _enabled = true;
    bool _ticking;
    bool _interruptAfterTick;
    int _frameCounter;
    double _accumulatedDelta;

    /// <summary>This runner's own blackboard values, by entry id. Absent means the source's default.</summary>
    Godot.Collections.Dictionary _overrides = [];

    public override void _Ready() {
        if (Engine.IsEditorHint()) {
            SetProcess(false);
            SetPhysicsProcess(false);
            return;
        }

        Actor ??= GetParent();
        Blackboard.Declare(Entries?.Blackboard);
        foreach (var (id, value) in _overrides) Blackboard.SetById(id.AsString(), value);
        Rebuild();
        ApplyProcessMode();
    }

    // ---- what a subclass runs ----------------------------------------------------------------

    /// <summary>Rebuilds the runtime copy from the current source.</summary>
    public void Rebuild() {
        _frameCounter = 0;
        _accumulatedDelta = 0;
        BuildInstance();
    }

    /// <summary>Builds — or drops — the runtime copy of the source.</summary>
    protected abstract void BuildInstance();

    /// <summary>Whether there is a runtime copy to tick.</summary>
    protected abstract bool HasInstance { get; }

    /// <summary>Advances the runtime copy by one tick.</summary>
    protected abstract MissStatus TickInstance(MissContext ctx);

    /// <summary>Abandons whatever the runtime copy left mid-run.</summary>
    protected abstract void InterruptInstance(MissContext ctx);

    /// <summary>Called once a tick is through and <see cref="Status"/> is up to date.</summary>
    protected virtual void AfterTick() { }

    /// <summary>What follows the tick node by node, for live debugging. Null by default.</summary>
    protected virtual INodeObserver Observer => null;

    // ---- blackboard overrides ----------------------------------------------------------------

    /// <summary>
    /// Gives this runner its own value for an entry, instead of the source's default. Takes effect
    /// when the runner is ready; afterwards, write to <see cref="Blackboard"/> directly.
    /// </summary>
    public void SetOverride(string entryId, Variant value) => _overrides[entryId] = value;

    public void ClearOverride(string entryId) => _overrides.Remove(entryId);

    public bool TryGetOverride(string entryId, out Variant value) => _overrides.TryGetValue(entryId, out value);

    public override Godot.Collections.Array<Godot.Collections.Dictionary> _GetPropertyList() {
        var properties = new Godot.Collections.Array<Godot.Collections.Dictionary>();

        // Shown: one field per entry, named after it. Nothing is stored under these names.
        var entries = Entries?.Blackboard;
        if (entries != null && entries.Count > 0) {
            properties.Add(new Godot.Collections.Dictionary {
                { "name", "Blackboard" },
                { "type", (int) Variant.Type.Nil },
                { "hint_string", BlackboardGroup },
                { "usage", (int) PropertyUsageFlags.Group },
            });
            foreach (var entry in entries) {
                if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
                properties.Add(Describe(BlackboardGroup + entry.Name, entry.VariantType, entry.ClassName, PropertyUsageFlags.Editor));
            }
        }

        // Stored: only the values this runner actually overrides, keyed by id so renames cannot orphan them.
        foreach (var (id, value) in _overrides) {
            var className = value.VariantType == Variant.Type.Object ? value.AsGodotObject()?.GetClass() ?? "" : "";
            properties.Add(Describe(OverridePrefix + id.AsString(), value.VariantType, className, PropertyUsageFlags.Storage));
        }
        return properties;
    }

    static Godot.Collections.Dictionary Describe(string name, Variant.Type type, string className, PropertyUsageFlags usage) {
        var hint = PropertyHint.None;
        if (type == Variant.Type.Object) hint = BbTypes.IsNodeClass(className) ? PropertyHint.NodeType : PropertyHint.ResourceType;
        if (type == Variant.Type.Nil) usage |= PropertyUsageFlags.NilIsVariant;

        return new Godot.Collections.Dictionary {
            { "name", name },
            { "type", (int) type },
            { "hint", (int) hint },
            { "hint_string", hint == PropertyHint.None ? "" : className },
            { "usage", (int) usage },
        };
    }

    BlackboardEntry EntryShownAs(string property)
        => property.StartsWith(BlackboardGroup) ? Entries.FindEntryByName(property[BlackboardGroup.Length..]) : null;

    public override Variant _Get(StringName property) {
        var name = property.ToString();
        if (name.StartsWith(OverridePrefix)) {
            return _overrides.TryGetValue(name[OverridePrefix.Length..], out var stored) ? stored : default;
        }
        if (EntryShownAs(name) is not { } entry) return default;
        return _overrides.TryGetValue(entry.Id, out var value) ? value : entry.Default;
    }

    public override bool _Set(StringName property, Variant value) {
        var name = property.ToString();
        if (name.StartsWith(OverridePrefix)) {
            _overrides[name[OverridePrefix.Length..]] = value;
            return true;
        }
        if (EntryShownAs(name) is not { } entry) return false;

        // Setting the source's own default back — the Inspector's revert does exactly that — is the
        // same as having no override at all, and keeps the scene file free of redundant values.
        var had = _overrides.ContainsKey(entry.Id);
        if (BbTypes.SameValue(value, entry.Default) || (value.VariantType == Variant.Type.Nil && entry.IsNode)) {
            _overrides.Remove(entry.Id);
        }
        else {
            _overrides[entry.Id] = value;
        }
        // Only when a stored property appears or disappears: rebuilding the Inspector on every change
        // would interrupt a slider drag.
        if (had != _overrides.ContainsKey(entry.Id)) NotifyPropertyListChanged();
        return true;
    }

    public override bool _PropertyCanRevert(StringName property)
        => EntryShownAs(property.ToString()) is { } entry && _overrides.ContainsKey(entry.Id);

    public override Variant _PropertyGetRevert(StringName property)
        => EntryShownAs(property.ToString()) is { } entry ? entry.Default : default;

    // ---- ticking -----------------------------------------------------------------------------

    public override void _Process(double delta) => Advance(delta);

    public override void _PhysicsProcess(double delta) => Advance(delta);

    /// <summary>Stops the runner; same as setting <see cref="Enabled"/> to false. Resume with <c>Enabled = true</c>.</summary>
    public void Stop() => Enabled = false;

    /// <summary>
    /// Ticks once. Called automatically unless <see cref="Thread"/> is
    /// <see cref="ProcessThread.Manual"/>; tests drive it directly with an artificial delta.
    /// A stopped runner ignores the call and just reports its last status.
    /// </summary>
    public MissStatus Tick(double delta) {
        if (!HasInstance) return MissStatus.Failure;
        if (!_enabled) return Status;

        _ticking = true;
        var status = TickInstance(Context(delta));
        _ticking = false;

        if (status != Status) {
            Status = status;
            EmitSignalStatusChanged((int) status);
        }

        AfterTick();

        if (_interruptAfterTick) {
            _interruptAfterTick = false;
            if (!_enabled) Interrupt();
        }

        return Status;
    }

    public void Interrupt() {
        if (!HasInstance) return;
        InterruptInstance(Context(0));
        Status = MissStatus.Failure;
    }

    protected MissContext Context(double delta) => new() {
        Actor = Actor,
        Blackboard = Blackboard,
        Delta = delta,
        Runner = this,
        Observer = Observer,
    };

    void Advance(double delta) {
        if (!_enabled) return;

        _accumulatedDelta += delta;
        _frameCounter++;
        if (_frameCounter < TickRate) return;

        _frameCounter = 0;
        var elapsed = _accumulatedDelta;
        _accumulatedDelta = 0;
        Tick(elapsed);
    }

    void ApplyProcessMode() {
        SetProcess(_enabled && Thread == ProcessThread.EveryFrame);
        SetPhysicsProcess(_enabled && Thread == ProcessThread.Physics);
    }
}
