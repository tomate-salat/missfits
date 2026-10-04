using System;
using System.Collections.Generic;
using System.Linq;
using Misscore;

namespace Misstate;

/// <summary>
/// One runner's private copy of an <see cref="Fsm"/>: the cloned actions of every state and
/// conditions of every transition, and which state the machine is in.
/// </summary>
public sealed class FsmInstance {
    sealed class RuntimeTransition {
        public FsmTransition Definition;
        public MissNode[] Conditions;
    }

    sealed class RuntimeState {
        public FsmState Definition;
        public MissNode[] Actions;
        public RuntimeTransition[] Transitions;

        /// <summary>One after the other: the action to resume, or -1 when a run starts afresh.</summary>
        public int RunningIndex = -1;

        /// <summary>All at once: whether a run is under way, which actions run and which are through.</summary>
        public bool ParallelRun;
        public bool[] Running;
        public bool[] Done;
    }

    public Fsm Definition { get; }

    /// <summary>
    /// Raised whenever the machine enters a state: with the state it left — null when it was in none —
    /// and the one it is in now.
    /// </summary>
    public event Action<FsmState, FsmState> StateChanged;

    /// <summary>The state the machine is in, as authored — or null before the first tick.</summary>
    public FsmState Current => _current?.Definition;

    /// <summary>This instance's copies of the actions of <paramref name="state"/>, in order.</summary>
    public IReadOnlyList<MissNode> ActionsOf(FsmState state)
        => state != null && _states.TryGetValue(state.Id, out var found) ? found.Actions : [];

    readonly Dictionary<string, RuntimeState> _states = [];
    RuntimeState _current;

    FsmInstance(Fsm definition) {
        Definition = definition;
        foreach (var state in definition.States) {
            if (state == null || _states.ContainsKey(state.Id)) continue;

            var transitions = new List<RuntimeTransition>();
            foreach (var transition in state.Transitions) {
                if (transition == null) continue;
                transitions.Add(new RuntimeTransition { Definition = transition, Conditions = Clones(transition.Conditions) });
            }
            var actions = Clones(state.Actions);
            _states[state.Id] = new RuntimeState {
                Definition = state,
                Actions = actions,
                Transitions = [.. transitions],
                Running = new bool[actions.Length],
                Done = new bool[actions.Length],
            };
        }
    }

    static MissNode[] Clones(IEnumerable<MissNode> nodes) => [.. nodes.Where(n => n != null).Select(n => n.CloneRuntime())];

    public static FsmInstance Create(Fsm definition) => definition?.InitialState == null ? null : new FsmInstance(definition);

    /// <summary>
    /// One tick: enters the initial state if the machine is in none, runs the current state's
    /// actions, then takes the first transition that fires. The state arrived at starts on the next
    /// tick, so a tick never takes more than one transition.
    /// </summary>
    public void Tick(MissContext ctx) {
        if (_current == null) Enter(_states[Definition.InitialState.Id]);
        var state = _current;

        var finished = state.Definition.Parallel ? TickAllAtOnce(state, ctx) : TickOneByOne(state, ctx);

        // An action itself sent the machine elsewhere: the transitions of the state just left no
        // longer apply.
        if (!ReferenceEquals(_current, state)) return;

        foreach (var transition in state.Transitions) {
            if (!Fires(transition, finished, ctx)) continue;
            GoTo(transition.Definition.TargetStateId, ctx);
            break;
        }
    }

    /// <summary>How a run that worked through every action without a deciding result ends.</summary>
    static MissStatus Undecided(ListMode mode) => mode == ListMode.Sequence ? MissStatus.Success : MissStatus.Failure;

    /// <summary>The result that ends a run at once: a failure for a sequence, a success for a selector.</summary>
    static MissStatus Decisive(ListMode mode) => mode == ListMode.Sequence ? MissStatus.Failure : MissStatus.Success;

    /// <summary>Front to back, each action waiting for the one before it. Null while the run is not over.</summary>
    MissStatus? TickOneByOne(RuntimeState state, MissContext ctx) {
        if (state.Actions.Length == 0) return null;

        var mode = state.Definition.Mode;
        for (var i = Math.Max(state.RunningIndex, 0); i < state.Actions.Length; i++) {
            var action = state.Actions[i];
            var resumed = i == state.RunningIndex;
            if (!resumed) action.Begin(ctx);
            var status = action.Execute(ctx);

            if (!ReferenceEquals(_current, state)) {
                CloseAfterLeaving(action, status, resumed, ctx);
                return null;
            }
            if (status == MissStatus.Running) {
                state.RunningIndex = i;
                return null;
            }

            action.AfterRun(ctx);
            if (status == Decisive(mode)) {
                state.RunningIndex = -1;
                return status;
            }
        }

        state.RunningIndex = -1;
        return Undecided(mode);
    }

    /// <summary>
    /// Every action that is not through yet is ticked. The first deciding result ends the run and
    /// interrupts the rest. Null while the run is not over.
    /// </summary>
    MissStatus? TickAllAtOnce(RuntimeState state, MissContext ctx) {
        if (state.Actions.Length == 0) return null;

        if (!state.ParallelRun) {
            state.ParallelRun = true;
            Array.Fill(state.Running, false);
            Array.Fill(state.Done, false);
        }

        var mode = state.Definition.Mode;
        for (var i = 0; i < state.Actions.Length; i++) {
            if (state.Done[i]) continue;

            var action = state.Actions[i];
            var resumed = state.Running[i];
            if (!resumed) action.Begin(ctx);
            var status = action.Execute(ctx);

            if (!ReferenceEquals(_current, state)) {
                CloseAfterLeaving(action, status, resumed, ctx);
                return null;
            }
            if (status == MissStatus.Running) {
                state.Running[i] = true;
                continue;
            }

            action.AfterRun(ctx);
            state.Running[i] = false;
            state.Done[i] = true;
            if (status == Decisive(mode)) {
                InterruptRun(state, ctx);
                return status;
            }
        }

        if (!state.Done.All(done => done)) return null;
        InterruptRun(state, ctx);
        return Undecided(mode);
    }

    /// <summary>
    /// An action sent the machine to another state from inside its own tick. Leaving the state has
    /// interrupted every action it knew to be running, which includes this one if it was being
    /// resumed. One begun on this very tick is closed here instead.
    /// </summary>
    static void CloseAfterLeaving(MissNode action, MissStatus status, bool resumed, MissContext ctx) {
        if (resumed) return;
        if (status == MissStatus.Running) action.Interrupt(ctx);
        else action.AfterRun(ctx);
    }

    static bool Fires(RuntimeTransition transition, MissStatus? finished, MissContext ctx) {
        var triggered = transition.Definition.On switch {
            FsmTrigger.Finished => finished != null,
            FsmTrigger.Succeeded => finished == MissStatus.Success,
            FsmTrigger.Failed => finished == MissStatus.Failure,
            _ => true,
        };
        if (!triggered) return false;
        if (transition.Conditions.Length == 0) return true;

        var all = transition.Definition.Mode == ListMode.Sequence;
        foreach (var condition in transition.Conditions) {
            var holds = Holds(condition, ctx);
            if (holds != all) return holds;
        }
        return all;
    }

    /// <summary>Checked afresh every time: a condition is never resumed, so one still running is dropped.</summary>
    static bool Holds(MissNode condition, MissContext ctx) {
        condition.Begin(ctx);
        var status = condition.Execute(ctx);
        if (status == MissStatus.Running) condition.Interrupt(ctx);
        else condition.AfterRun(ctx);
        return status == MissStatus.Success;
    }

    /// <summary>
    /// Leaves the current state — interrupting whatever it left running — for the given one. False
    /// when no such state exists, in which case nothing changes.
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

    /// <summary>Abandons whatever the current state left mid-run. The machine stays in its state.</summary>
    public void Interrupt(MissContext ctx) {
        if (_current != null) InterruptRun(_current, ctx);
    }

    /// <summary>Interrupts the actions still running and puts the state back to "no run under way".</summary>
    static void InterruptRun(RuntimeState state, MissContext ctx) {
        if (state.RunningIndex >= 0) state.Actions[state.RunningIndex].Interrupt(ctx);
        state.RunningIndex = -1;

        for (var i = 0; i < state.Actions.Length; i++) {
            if (state.Running[i]) state.Actions[i].Interrupt(ctx);
            state.Running[i] = false;
        }
        state.ParallelRun = false;
    }

    /// <summary>Back to square one: the next tick enters the initial state.</summary>
    public void Reset(MissContext ctx) {
        Interrupt(ctx);
        _current = null;
    }
}