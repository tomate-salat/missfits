using System;
using System.Collections.Generic;
using Misscore;

namespace Misstate;

/// <summary>
/// One runner's private copy of an <see cref="Fsm"/>: the cloned nodes of every state and
/// transition, and which state the machine is in.
/// </summary>
public sealed class FsmInstance {
    sealed class RuntimeTransition {
        public FsmTransition Definition;
        public MissNode Condition;
    }

    sealed class RuntimeState {
        public FsmState Definition;
        public MissNode Node;
        public RuntimeTransition[] Transitions;
    }

    public Fsm Definition { get; }

    /// <summary>
    /// Raised whenever the machine enters a state: with the state it left — null when it was in none —
    /// and the one it is in now.
    /// </summary>
    public event Action<FsmState, FsmState> StateChanged;

    /// <summary>The state the machine is in, as authored — or null before the first tick.</summary>
    public FsmState Current => _current?.Definition;

    /// <summary>The running copy of the current state's node, or null.</summary>
    public MissNode CurrentNode => _current?.Node;

    /// <summary>This instance's copy of the node of <paramref name="state"/>, or null.</summary>
    public MissNode NodeOf(FsmState state) => state != null && _states.TryGetValue(state.Id, out var found) ? found.Node : null;

    readonly Dictionary<string, RuntimeState> _states = [];
    RuntimeState _current;
    bool _nodeRunning;

    FsmInstance(Fsm definition) {
        Definition = definition;
        foreach (var state in definition.States) {
            if (state == null || _states.ContainsKey(state.Id)) continue;

            var transitions = new List<RuntimeTransition>();
            foreach (var transition in state.Transitions) {
                if (transition == null) continue;
                transitions.Add(new RuntimeTransition { Definition = transition, Condition = transition.Condition?.CloneRuntime() });
            }
            _states[state.Id] = new RuntimeState {
                Definition = state,
                Node = state.Node?.CloneRuntime(),
                Transitions = [.. transitions],
            };
        }
    }

    public static FsmInstance Create(Fsm definition) => definition?.InitialState == null ? null : new FsmInstance(definition);

    /// <summary>
    /// One tick: enters the initial state if the machine is in none, runs the current state's node,
    /// then takes the first transition that fires. The state arrived at starts on the next tick, so a
    /// tick never takes more than one transition.
    /// </summary>
    public void Tick(MissContext ctx) {
        if (_current == null) Enter(_states[Definition.InitialState.Id]);
        var state = _current;

        MissStatus? finished = null;
        if (state.Node is { } node) {
            if (!_nodeRunning) node.Begin(ctx);
            var status = node.Execute(ctx);

            // The node itself sent the machine elsewhere: it is done here, and the transitions of
            // the state just left no longer apply.
            if (!ReferenceEquals(_current, state)) {
                if (status == MissStatus.Running) node.Interrupt(ctx);
                else node.AfterRun(ctx);
                _nodeRunning = false;
                return;
            }

            _nodeRunning = status == MissStatus.Running;
            if (!_nodeRunning) {
                node.AfterRun(ctx);
                finished = status;
            }
        }

        foreach (var transition in state.Transitions) {
            if (!Fires(transition, finished, ctx)) continue;
            GoTo(transition.Definition.TargetStateId, ctx);
            break;
        }
    }

    static bool Fires(RuntimeTransition transition, MissStatus? finished, MissContext ctx) {
        var triggered = transition.Definition.On switch {
            FsmTrigger.Finished => finished != null,
            FsmTrigger.Succeeded => finished == MissStatus.Success,
            FsmTrigger.Failed => finished == MissStatus.Failure,
            _ => true,
        };
        if (!triggered) return false;
        if (transition.Condition is not { } condition) return true;

        // Checked afresh every time: a condition is never resumed, so one still running is dropped.
        condition.Begin(ctx);
        var status = condition.Execute(ctx);
        if (status == MissStatus.Running) condition.Interrupt(ctx);
        else condition.AfterRun(ctx);
        return status == MissStatus.Success;
    }

    /// <summary>
    /// Leaves the current state — interrupting its node if that is still running — for the given
    /// one. False when no such state exists, in which case nothing changes.
    /// </summary>
    public bool GoTo(string stateId, MissContext ctx) {
        if (string.IsNullOrEmpty(stateId) || !_states.TryGetValue(stateId, out var target)) return false;

        Interrupt(ctx);
        Enter(target);
        return true;
    }

    void Enter(RuntimeState state) {
        var left = _current?.Definition;
        _current = state;
        StateChanged?.Invoke(left, state.Definition);
    }

    /// <summary>Abandons whatever the current state's node left mid-run. The machine stays in its state.</summary>
    public void Interrupt(MissContext ctx) {
        if (_nodeRunning) _current?.Node?.Interrupt(ctx);
        _nodeRunning = false;
    }

    /// <summary>Back to square one: the next tick enters the initial state.</summary>
    public void Reset(MissContext ctx) {
        Interrupt(ctx);
        _current = null;
    }
}