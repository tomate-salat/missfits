using System;
using System.Text;
using Godot;
using Misscore;

namespace Misspeak;

/// <summary>
/// Runtime half of the live-debug channel: where a runner's dialogue is, sent to the editor that has
/// the dialogue open. The bookkeeping — who watches what, and sending only what changed — is
/// <see cref="RunnerDebugChannel"/>'s; this says what the state of a dialogue is.
/// </summary>
public static class MisspeakDebug {
    public const string Prefix = "misspeak";

    static readonly RunnerDebugChannel Channel = new(Prefix, SourcePath, StateOf);

    public static void Register(DialogueRunner runner) => Channel.Register(runner);

    public static void Unregister(DialogueRunner runner) => Channel.Unregister(runner);

    public static void SendState(DialogueRunner runner) => Channel.SendState(runner);

    /// <summary>A stream that sends to wherever it is told instead of to the debugger. For tests.</summary>
    internal static RunnerDebugStream NewStream(Action<string, Godot.Collections.Array> send, Func<ulong> clock) => Channel.NewStream(send, clock);

    static string SourcePath(MissRunner runner) => (runner as DialogueRunner)?.Dialogue?.ResourcePath;

    /// <summary>
    /// The section the dialogue is in — empty while none is running — the line it is at, what it is
    /// waiting for, the tick, and the option that led into the section.
    /// </summary>
    static RunnerDebugState? StateOf(MissRunner runner, int tick) {
        if (runner is not DialogueRunner { Instance: { } instance }) return null;

        var sectionId = instance.Current?.Id ?? "";
        var lineId = instance.ActiveLine?.Id ?? "";
        var waiting = (int) instance.Waiting;
        var enteredBy = instance.EnteredBy?.Id ?? "";

        var frame = Encoding.UTF8.GetBytes($"{sectionId}|{lineId}|{waiting}|{enteredBy}");
        return new RunnerDebugState(frame, [sectionId, lineId, waiting, tick, enteredBy]);
    }
}
