using System;
using System.Text;
using Godot;
using Misscore;

namespace Misstate;

/// <summary>
/// Runtime half of the live-debug channel: where a runner's machine is, sent to the editor that has
/// the machine open. The bookkeeping — who watches what, and sending only what changed — is
/// <see cref="RunnerDebugChannel"/>'s; this says what the state of a machine is.
/// </summary>
public static class MisstateDebug {
    public const string Prefix = "misstate";

    static readonly RunnerDebugChannel Channel = new(Prefix, SourcePath, StateOf);

    public static void Register(FsmRunner runner) => Channel.Register(runner);

    public static void Unregister(FsmRunner runner) => Channel.Unregister(runner);

    public static void SendState(FsmRunner runner) => Channel.SendState(runner);

    /// <summary>A stream that sends to wherever it is told instead of to the debugger. For tests.</summary>
    internal static RunnerDebugStream NewStream(Action<string, Godot.Collections.Array> send, Func<ulong> clock) => Channel.NewStream(send, clock);

    static string SourcePath(MissRunner runner) => (runner as FsmRunner)?.Machine?.ResourcePath;

    /// <summary>The state the machine is in, what its actions last returned, the tick, and the transition that led there.</summary>
    static RunnerDebugState? StateOf(MissRunner runner, int tick) {
        if (runner is not FsmRunner { Instance: { } instance }) return null;

        var stateId = instance.Current?.Id ?? "";
        var statuses = instance.ActionStatuses;
        var enteredBy = instance.EnteredBy?.Id ?? "";

        // What tells a change: the state and the transition travel in the frame too, since a state
        // entered again by another way has the same action statuses.
        var id = Encoding.UTF8.GetBytes(stateId + enteredBy);
        var frame = new byte[id.Length + statuses.Length];
        id.CopyTo(frame, 0);
        statuses.CopyTo(frame, id.Length);

        return new RunnerDebugState(frame, [stateId, (byte[]) statuses.Clone(), tick, enteredBy]);
    }
}
