using Godot;
using Misscore;

namespace Misstate;

/// <summary>
/// Drives one <see cref="Fsm"/> for one actor. Add it as a child of the actor and assign a machine
/// resource; the same resource can be shared by any number of runners, because each builds its own
/// runtime copy.
/// <para>
/// Stopping the runner interrupts what the current state is doing but keeps the state: switched back
/// on, the machine carries on where it was. <see cref="Restart"/> sends it back to its initial state.
/// </para>
/// </summary>
[GlobalClass, Tool]
public partial class FsmRunner : MissRunner {
    /// <summary>The machine this runner plays. Shared, not consumed.</summary>
    [Export]
    public Fsm Machine {
        get => SourceAs<Fsm>();
        set => SetSource(value);
    }

    /// <summary>The machine moved from one state to another; both by name, the first empty on entering the initial state.</summary>
    [Signal]
    public delegate void StateChangedEventHandler(string from, string to);

    public FsmInstance Instance { get; private set; }

    /// <summary>Name of the state the machine is in, or empty before the first tick.</summary>
    public string CurrentState => Instance?.Current?.Name ?? "";

    protected override bool HasInstance => Instance != null;

    protected override void BuildInstance() {
        Instance = FsmInstance.Create(Machine);
        if (Instance != null) Instance.StateChanged += (from, to) => EmitSignalStateChanged(from?.Name ?? "", to?.Name ?? "");
        if (Instance == null && Machine != null) GD.PushWarning($"misstate: {Name} has a machine without states.");
    }

    /// <summary>A machine with a state to be in is running; there is nothing for it to finish.</summary>
    protected override MissStatus TickInstance(MissContext ctx) {
        Instance.Tick(ctx);
        return MissStatus.Running;
    }

    protected override void InterruptInstance(MissContext ctx) => Instance.Interrupt(ctx);

    /// <summary>
    /// Sends the machine to the state of that name, whatever its transitions say. Safe to call from
    /// inside a tick. False when the machine has no such state.
    /// </summary>
    public bool GoTo(string stateName) {
        if (Instance == null || Machine?.FindStateByName(stateName) is not { } target) return false;

        Instance.GoTo(target.Id, Context(0));
        return true;
    }

    /// <summary>Sends the machine back to its initial state, which it enters on the next tick.</summary>
    public void Restart() => Instance?.Reset(Context(0));
}