using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using Misscore;

namespace Misstate;

/// <summary>
/// Game-side bookkeeping of the live-debug channel: which runners exist, which one the editor
/// watches, and when the state a runner is in goes out. <see cref="MisstateDebug"/> is only the
/// static facade that hands this the real debugger; the tests hand it a recorder instead.
/// </summary>
internal sealed class FsmDebugStream {
    public const ulong MinSendIntervalMsec = 33;

    sealed class Tracked {
        public FsmRunner Runner;
        public readonly FrameThrottle Throttle = new();
        public int TickCount;
    }

    readonly Dictionary<ulong, Tracked> _tracked = [];
    readonly Action<string, Godot.Collections.Array> _send;
    readonly Func<ulong> _clock;

    public string WatchedPath { get; private set; } = "";
    public long WatchedInstance { get; private set; } = -1;

    /// <param name="send">Receives the message name without prefix, e.g. "state".</param>
    public FsmDebugStream(Action<string, Godot.Collections.Array> send, Func<ulong> clock) {
        _send = send;
        _clock = clock;
    }

    public void Register(FsmRunner runner) {
        if (runner?.Instance == null) return;

        _tracked[runner.GetInstanceId()] = new Tracked { Runner = runner };
        SendRegister(runner);
    }

    public void Unregister(FsmRunner runner) {
        if (runner == null) return;

        var id = runner.GetInstanceId();
        if (!_tracked.Remove(id)) return;

        // The editor picks another runner and says so, but until it does, a filter on a runner that
        // no longer exists would keep every other one silent.
        if (WatchedInstance == (long) id) WatchedInstance = -1;
        _send("unregister", [(long) id]);
    }

    /// <summary>Called after every tick of a runner: sends where its machine is, if anyone is watching.</summary>
    public void SendState(FsmRunner runner) {
        if (!_tracked.TryGetValue(runner.GetInstanceId(), out var tracked)) return;

        tracked.TickCount++;
        if (IsStreaming(tracked)) Send(tracked);
    }

    void Send(Tracked tracked) {
        var instance = tracked.Runner.Instance;
        var stateId = instance.Current?.Id ?? "";
        var statuses = instance.ActionStatuses;

        // The throttle compares frames, so the state travels in it too: a change of state with the
        // same action statuses is still a change.
        var id = Encoding.UTF8.GetBytes(stateId);
        var frame = new byte[id.Length + statuses.Length];
        id.CopyTo(frame, 0);
        statuses.CopyTo(frame, id.Length);
        if (!tracked.Throttle.TryTake(frame, _clock(), MinSendIntervalMsec)) return;

        _send("state", [(long) tracked.Runner.GetInstanceId(), stateId, (byte[]) statuses.Clone(), tracked.TickCount]);
    }

    bool IsStreaming(Tracked tracked) {
        var id = (long) tracked.Runner.GetInstanceId();
        if (WatchedInstance >= 0 && WatchedInstance != id) return false;
        if (string.IsNullOrEmpty(WatchedPath)) return false;
        return tracked.Runner.Machine?.ResourcePath == WatchedPath;
    }

    void SendRegister(FsmRunner runner) => _send("register", [
        (long) runner.GetInstanceId(),
        runner.Machine?.ResourcePath ?? "",
        runner.Actor?.Name.ToString() ?? runner.Name.ToString(),
    ]);

    /// <summary>Handles editor to game messages, with or without the "misstate:" prefix.</summary>
    public bool OnEditorMessage(string message, Godot.Collections.Array data) {
        switch (message) {
            case "watch_path": {
                var path = data.Count > 0 ? data[0].AsString() : "";

                // The editor answers every registration with the path it wants, and re-announcing
                // triggers another answer — so an unchanged path has to be a no-op, otherwise the
                // two sides ping-pong forever.
                if (path == WatchedPath) return true;

                WatchedPath = path;
                Announce();
                return true;
            }

            case "watch_instance": {
                var wanted = data.Count > 0 ? data[0].AsInt64() : -1;
                if (wanted == WatchedInstance) return true;

                WatchedInstance = wanted;
                SendCurrentStates();
                return true;
            }

            case "announce":
                // The editor lost track of the runners — its assembly reloaded mid-game — and asks
                // for them again, along with where each one is.
                Announce();
                return true;

            default:
                return false;
        }
    }

    void Announce() {
        foreach (var tracked in _tracked.Values) {
            if (GodotObject.IsInstanceValid(tracked.Runner)) SendRegister(tracked.Runner);
        }
        SendCurrentStates();
    }

    /// <summary>
    /// Sends where every watched runner is, right away. Waiting for the next tick is not enough: a
    /// paused game does not tick, so the editor, which clears its view whenever it switches what it
    /// watches, would stay blank until the game resumes.
    /// </summary>
    void SendCurrentStates() {
        foreach (var tracked in _tracked.Values) {
            tracked.Throttle.Invalidate();
            if (tracked.TickCount == 0) continue;
            if (!GodotObject.IsInstanceValid(tracked.Runner) || tracked.Runner.Instance == null) continue;
            if (IsStreaming(tracked)) Send(tracked);
        }
    }
}