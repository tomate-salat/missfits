using System;
using System.Collections.Generic;
using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Game-side bookkeeping of the live-debug channel: which runners exist, which one the editor
/// watches, and when a status frame goes out. <see cref="MissbehaveDebug"/> is only the static
/// facade that hands this the real debugger; the tests hand it a recorder instead.
/// </summary>
internal sealed class DebugStream {
    public const ulong MinSendIntervalMsec = 33;

    sealed class Tracked {
        public BehaviorTreeRunner Runner;
        public readonly FrameThrottle Throttle = new();
        public int TickCount;
    }

    readonly Dictionary<ulong, Tracked> _tracked = [];
    readonly Action<string, Godot.Collections.Array> _send;
    readonly Func<ulong> _clock;

    public string WatchedPath { get; private set; } = "";
    public long WatchedInstance { get; private set; } = -1;
    public bool Visible { get; private set; } = true;

    /// <param name="send">Receives the message name without prefix, e.g. "frame".</param>
    public DebugStream(Action<string, Godot.Collections.Array> send, Func<ulong> clock) {
        _send = send;
        _clock = clock;
    }

    public void Register(BehaviorTreeRunner runner) {
        if (runner?.Instance == null) return;

        _tracked[runner.GetInstanceId()] = new Tracked { Runner = runner };
        SendRegister(runner);
    }

    public void Unregister(BehaviorTreeRunner runner) {
        if (runner == null) return;

        var id = runner.GetInstanceId();
        if (!_tracked.Remove(id)) return;

        // The editor picks another runner and says so, but until it does, a filter on a runner that
        // no longer exists would keep every other one silent — e.g. after the watched enemy died.
        if (WatchedInstance == (long) id) WatchedInstance = -1;
        _send("unregister", [(long) id]);
    }

    public void SendFrame(BehaviorTreeRunner runner) {
        if (!_tracked.TryGetValue(runner.GetInstanceId(), out var tracked)) return;

        tracked.TickCount++;
        if (!Visible || !IsStreaming(tracked)) return;

        var frame = runner.Instance.Frame;
        if (!tracked.Throttle.TryTake(frame, _clock(), MinSendIntervalMsec)) return;

        _send("frame", [(long) runner.GetInstanceId(), frame, tracked.TickCount]);
    }

    bool IsStreaming(Tracked tracked) {
        var id = (long) tracked.Runner.GetInstanceId();
        if (WatchedInstance >= 0 && WatchedInstance != id) return false;
        if (string.IsNullOrEmpty(WatchedPath)) return false;
        return tracked.Runner.Tree?.ResourcePath == WatchedPath;
    }

    void SendRegister(BehaviorTreeRunner runner) {
        var instance = runner.Instance;
        _send("register", [
            (long) runner.GetInstanceId(),
            runner.Tree?.ResourcePath ?? "",
            runner.Actor?.Name.ToString() ?? runner.Name.ToString(),
            instance.IdTable,
            instance.ParentTable,
            instance.ClassNames,
        ]);
    }

    /// <summary>Handles editor to game messages, with or without the "missbehave:" prefix.</summary>
    public bool OnEditorMessage(string message, Godot.Collections.Array data) {
        switch (message) {
            case "watch_path": {
                var path = data.Count > 0 ? data[0].AsString() : "";

                // The editor answers every registration with the path it wants, and re-announcing
                // triggers another answer — so an unchanged path has to be a no-op, otherwise the
                // two sides ping-pong forever.
                if (path == WatchedPath) return true;

                WatchedPath = path;
                foreach (var tracked in _tracked.Values) {
                    if (GodotObject.IsInstanceValid(tracked.Runner)) SendRegister(tracked.Runner);
                }
                SendCurrentFrames();
                return true;
            }

            case "watch_instance": {
                var wanted = data.Count > 0 ? data[0].AsInt64() : -1;
                if (wanted == WatchedInstance) return true;

                WatchedInstance = wanted;
                SendCurrentFrames();
                return true;
            }

            case "announce": {
                // The editor lost track of the runners — its assembly reloaded mid-game — and asks for
                // them again, along with what each one last showed.
                foreach (var tracked in _tracked.Values) {
                    if (GodotObject.IsInstanceValid(tracked.Runner)) SendRegister(tracked.Runner);
                }
                SendCurrentFrames();
                return true;
            }

            case "visible":
                Visible = data.Count == 0 || data[0].AsBool();
                SendCurrentFrames();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Sends what every watched runner showed on its last tick, right away. Waiting for the next tick
    /// is not enough: a paused game does not tick, so the editor, which clears its colours whenever it
    /// switches what it watches, would stay blank until the game resumes. It also has to bypass the
    /// identical-frame rule, since the newly watched runner's frame usually matches what it last sent.
    /// </summary>
    void SendCurrentFrames() {
        foreach (var tracked in _tracked.Values) {
            tracked.Throttle.Invalidate();
            if (!Visible || tracked.TickCount == 0) continue;
            if (!GodotObject.IsInstanceValid(tracked.Runner) || tracked.Runner.Instance == null) continue;
            if (!IsStreaming(tracked)) continue;

            var frame = tracked.Runner.Instance.Frame;
            if (tracked.Throttle.TryTake(frame, _clock(), MinSendIntervalMsec)) {
                _send("frame", [(long) tracked.Runner.GetInstanceId(), frame, tracked.TickCount]);
            }
        }
    }
}
