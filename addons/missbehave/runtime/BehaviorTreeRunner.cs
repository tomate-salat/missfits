using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Drives one <see cref="BehaviorTree"/> for one actor. Add it as a child of the actor and assign a
/// tree resource; the same resource can be shared by any number of runners, because each builds its
/// own runtime clone.
/// <para>
/// The Inspector lists the tree's blackboard entries under <i>Blackboard</i>, so each runner can
/// give them its own values — including scene nodes, which a tree resource cannot hold. Those values
/// are stored by entry id and survive renaming the entry. A tool script for that alone: in the editor
/// it does nothing else.
/// </para>
/// </summary>
[GlobalClass, Tool, Icon("res://addons/missbehave/icons/runner.svg")]
public partial class BehaviorTreeRunner : Node, ISerializationListener, IMissRunner {
    /// <summary>When the runner ticks its tree on its own.</summary>
    public enum ProcessThread {
        /// <summary>Once per rendered frame, in <c>_Process</c>, with a variable delta.</summary>
        EveryFrame,

        /// <summary>Once per physics step, in <c>_PhysicsProcess</c>, with a fixed delta.</summary>
        Physics,

        /// <summary>Not on its own — something else calls <see cref="Tick"/>.</summary>
        Manual,
    }

    /// <summary>Inspector section listing the tree's entries by name.</summary>
    public const string BlackboardGroup = "Blackboard/";

    /// <summary>Where overrides are stored, by entry id. Not shown.</summary>
    public const string OverridePrefix = "blackboard_overrides/";

    /// <summary>
    /// The tree this runner plays. Shared, not consumed: it is cloned on <see cref="Rebuild"/>, so the
    /// same resource can drive any number of runners without them interfering with each other.
    /// </summary>
    [Export]
    public BehaviorTree Tree {
        get {
            if (_tree == null && _treeAcrossReload != null && !_reloading) ResolveTreeAfterReload();
            return _tree;
        }
        set {
            if (ReferenceEquals(Tree, value)) return;
            if (Engine.IsEditorHint() && _tree != null && _tree.IsConnected(Resource.SignalName.Changed, TreeChanged)) {
                _tree.Disconnect(Resource.SignalName.Changed, TreeChanged);
            }
            _tree = value;
            FollowTree();
            NotifyPropertyListChanged();
        }
    }

    BehaviorTree _tree;

    Callable TreeChanged => new(this, MethodName.OnTreeChanged);

    /// <summary>
    /// The tree's entries decide which fields the Inspector shows, so edits to them are followed.
    /// Checked rather than assumed: the connection is native and outlives an assembly reload, so the
    /// tree picked up again afterwards is usually still connected.
    /// </summary>
    void FollowTree() {
        if (!Engine.IsEditorHint() || _tree == null || _tree.IsConnected(Resource.SignalName.Changed, TreeChanged)) return;
        _tree.Connect(Resource.SignalName.Changed, TreeChanged);
    }

    /// <summary>
    /// The tree, untyped, while the assembly reloads — which, as a tool script, happens to a runner in
    /// an open scene on every build. Restoring a field typed as one of this assembly's own classes can
    /// throw InvalidCastException, because the tree may not have its script back yet; so the typed
    /// field goes out empty and the tree is picked up again on first use.
    /// </summary>
    GodotObject _treeAcrossReload;

    /// <summary>
    /// Keeps the getter from picking the tree straight back up while the reload saves the properties:
    /// the generated code reads <see cref="Tree"/> right after <see cref="OnBeforeSerialize"/>, which
    /// would otherwise put the typed tree back into the saved data. And on restore it assigns
    /// <see cref="Tree"/> before the stash is back, so a saved tree would look like a change and
    /// connect <c>changed</c> a second time.
    /// </summary>
    bool _reloading;

    public void OnBeforeSerialize() {
        _treeAcrossReload = _tree;
        _tree = null;
        _reloading = true;
    }

    public void OnAfterDeserialize() => _reloading = false;

    void ResolveTreeAfterReload() {
        if (!IsInstanceValid(_treeAcrossReload)) {
            _treeAcrossReload = null;
            return;
        }
        if (InstanceFromId(_treeAcrossReload.GetInstanceId()) is not BehaviorTree tree) return;
        _tree = tree;
        _treeAcrossReload = null;
        FollowTree();
    }

    /// <summary>
    /// The node the tree acts upon — what its leaves reach through the context. Left empty, it is this
    /// runner's parent.
    /// </summary>
    [Export]
    public Node Actor { get; set; }

    /// <summary>
    /// When the tree is ticked: on every rendered frame, on every physics step, or not on its own, in
    /// which case your own code calls <see cref="Tick"/>. Physics by default, so a tree sees the same
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
    /// Switching this off stops the tree and interrupts whatever was running. Safe to do from inside
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
    public BehaviorTreeInstance Instance { get; private set; }
    public MissStatus Status { get; private set; } = MissStatus.Failure;

    bool _enabled = true;
    bool _ticking;
    bool _interruptAfterTick;
    int _frameCounter;
    double _accumulatedDelta;

    /// <summary>This runner's own blackboard values, by entry id. Absent means the tree's default.</summary>
    Godot.Collections.Dictionary _overrides = [];

    public override void _Ready() {
        if (Engine.IsEditorHint()) {
            SetProcess(false);
            SetPhysicsProcess(false);
            return;
        }

        Actor ??= GetParent();
        Blackboard.Declare(Tree?.Blackboard);
        foreach (var (id, value) in _overrides) Blackboard.SetById(id.AsString(), value);
        Rebuild();
        ApplyProcessMode();
    }

    void OnTreeChanged() => NotifyPropertyListChanged();

    // ---- blackboard overrides ----------------------------------------------------------------

    /// <summary>
    /// Gives this runner its own value for an entry, instead of the tree's default. Takes effect when
    /// the runner is ready; afterwards, write to <see cref="Blackboard"/> directly.
    /// </summary>
    public void SetOverride(string entryId, Variant value) => _overrides[entryId] = value;

    public void ClearOverride(string entryId) => _overrides.Remove(entryId);

    public bool TryGetOverride(string entryId, out Variant value) => _overrides.TryGetValue(entryId, out value);

    public override Godot.Collections.Array<Godot.Collections.Dictionary> _GetPropertyList() {
        var properties = new Godot.Collections.Array<Godot.Collections.Dictionary>();

        // Shown: one field per entry, named after it. Nothing is stored under these names.
        if (Tree != null && Tree.Blackboard.Count > 0) {
            properties.Add(new Godot.Collections.Dictionary {
                { "name", "Blackboard" },
                { "type", (int) Variant.Type.Nil },
                { "hint_string", BlackboardGroup },
                { "usage", (int) PropertyUsageFlags.Group },
            });
            foreach (var entry in Tree.Blackboard) {
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

    public override Variant _Get(StringName property) {
        var name = property.ToString();
        if (name.StartsWith(OverridePrefix)) {
            return _overrides.TryGetValue(name[OverridePrefix.Length..], out var stored) ? stored : default;
        }
        if (!name.StartsWith(BlackboardGroup) || Tree?.FindEntryByName(name[BlackboardGroup.Length..]) is not { } entry) {
            return default;
        }
        return _overrides.TryGetValue(entry.Id, out var value) ? value : entry.Default;
    }

    public override bool _Set(StringName property, Variant value) {
        var name = property.ToString();
        if (name.StartsWith(OverridePrefix)) {
            _overrides[name[OverridePrefix.Length..]] = value;
            return true;
        }
        if (!name.StartsWith(BlackboardGroup) || Tree?.FindEntryByName(name[BlackboardGroup.Length..]) is not { } entry) {
            return false;
        }

        // Setting the tree's own default back — the Inspector's revert does exactly that — is the
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

    public override bool _PropertyCanRevert(StringName property) {
        var name = property.ToString();
        return name.StartsWith(BlackboardGroup) && Tree?.FindEntryByName(name[BlackboardGroup.Length..]) is { } entry
                                                && _overrides.ContainsKey(entry.Id);
    }

    public override Variant _PropertyGetRevert(StringName property) {
        var name = property.ToString();
        return name.StartsWith(BlackboardGroup) && Tree?.FindEntryByName(name[BlackboardGroup.Length..]) is { } entry
            ? entry.Default
            : default;
    }

    public override void _ExitTree() {
        if (Engine.IsEditorHint()) return;
        MissbehaveDebug.Unregister(this);
    }

    public override void _Process(double delta) => Advance(delta);

    public override void _PhysicsProcess(double delta) => Advance(delta);

    /// <summary>Rebuilds the runtime clone from the current <see cref="Tree"/>.</summary>
    public void Rebuild() {
        if (Instance != null) MissbehaveDebug.Unregister(this);

        Instance = BehaviorTreeInstance.Create(Tree);
        _frameCounter = 0;
        _accumulatedDelta = 0;

        if (Instance == null) {
            if (Tree != null) GD.PushWarning($"missbehave: {Name} has a tree without a root node.");
            return;
        }
        MissbehaveDebug.Register(this);
    }

    /// <summary>Stops the tree; same as setting <see cref="Enabled"/> to false. Resume with <c>Enabled = true</c>.</summary>
    public void Stop() => Enabled = false;

    /// <summary>
    /// Ticks the tree once. Called automatically unless <see cref="Thread"/> is
    /// <see cref="ProcessThread.Manual"/>; tests drive it directly with an artificial delta.
    /// A stopped runner ignores the call and just reports its last status.
    /// </summary>
    public MissStatus Tick(double delta) {
        if (Instance == null) return MissStatus.Failure;
        if (!_enabled) return Status;

        var ctx = Context(delta);
        Instance.BeginFrame();

        _ticking = true;
        var root = Instance.Root;
        if (Status != MissStatus.Running) root.Begin(ctx);
        var status = root.Execute(ctx);
        if (status != MissStatus.Running) root.AfterRun(ctx);
        _ticking = false;

        if (status != Status) {
            Status = status;
            EmitSignalStatusChanged((int) status);
        }

        MissbehaveDebug.SendFrame(this);

        if (_interruptAfterTick) {
            _interruptAfterTick = false;
            if (!_enabled) Interrupt();
        }

        return Status;
    }

    public void Interrupt() {
        if (Instance == null) return;
        Instance.Root.Interrupt(Context(0));
        Status = MissStatus.Failure;
    }

    MissContext Context(double delta) => new() {
        Actor = Actor,
        Blackboard = Blackboard,
        Delta = delta,
        Observer = Instance,
        Runner = this,
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
