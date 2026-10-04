using System;
using System.Collections.Generic;
using Godot;

namespace Misscore;

/// <summary>What a runner has to report at the moment: the data to send, and the bytes by which a change is told.</summary>
/// <param name="Frame">Compared with the last frame sent; an unchanged one is not sent again.</param>
/// <param name="Data">What goes out after the runner's id.</param>
public readonly record struct RunnerDebugState(byte[] Frame, Godot.Collections.Array Data);

/// <summary>
/// Game-side bookkeeping of a live-debug channel: which runners exist, which one the editor watches,
/// and when a runner's state goes out. Only runners whose source is the one open in the editor send
/// anything, and only when something changed. What the state of a runner is, is up to the addon —
/// this knows runners and sources, nothing more.
/// <para>
/// <see cref="RunnerDebugChannel"/> hands this the real debugger; tests hand it a recorder instead.
/// </para>
/// </summary>
public sealed class RunnerDebugStream {
    public const ulong MinSendIntervalMsec = 33;

    sealed class Tracked {
        public MissRunner Runner;
        public readonly FrameThrottle Throttle = new();
        public int TickCount;
    }

    readonly Dictionary<ulong, Tracked> _tracked = [];
    readonly Action<string, Godot.Collections.Array> _send;
    readonly Func<ulong> _clock;
    readonly Func<MissRunner, string> _sourcePath;
    readonly Func<MissRunner, int, RunnerDebugState?> _state;

    public string WatchedPath { get; private set; } = "";
    public long WatchedInstance { get; private set; } = -1;

    /// <param name="send">Receives the message name without prefix, e.g. "state".</param>
    /// <param name="sourcePath">The path of the resource a runner plays.</param>
    /// <param name="state">What a runner has to report after its tick of that number; null for nothing.</param>
    public RunnerDebugStream(Action<string, Godot.Collections.Array> send, Func<ulong> clock,
        Func<MissRunner, string> sourcePath, Func<MissRunner, int, RunnerDebugState?> state) {
        _send = send;
        _clock = clock;
        _sourcePath = sourcePath;
        _state = state;
    }

    public void Register(MissRunner runner) {
        if (runner == null) return;

        _tracked[runner.GetInstanceId()] = new Tracked { Runner = runner };
        SendRegister(runner);
    }

    public void Unregister(MissRunner runner) {
        if (runner == null) return;

        var id = runner.GetInstanceId();
        if (!_tracked.Remove(id)) return;

        // The editor picks another runner and says so, but until it does, a filter on a runner that
        // no longer exists would keep every other one silent.
        if (WatchedInstance == (long) id) WatchedInstance = -1;
        _send("unregister", [(long) id]);
    }

    /// <summary>Called after every tick of a runner: sends its state, if anyone is watching.</summary>
    public void SendState(MissRunner runner) {
        if (!_tracked.TryGetValue(runner.GetInstanceId(), out var tracked)) return;

        tracked.TickCount++;
        if (IsStreaming(tracked)) Send(tracked);
    }

    void Send(Tracked tracked) {
        if (_state(tracked.Runner, tracked.TickCount) is not { } state) return;
        if (!tracked.Throttle.TryTake(state.Frame, _clock(), MinSendIntervalMsec)) return;

        var message = new Godot.Collections.Array { (long) tracked.Runner.GetInstanceId() };
        message.AddRange(state.Data);
        _send("state", message);
    }

    bool IsStreaming(Tracked tracked) {
        var id = (long) tracked.Runner.GetInstanceId();
        if (WatchedInstance >= 0 && WatchedInstance != id) return false;
        if (string.IsNullOrEmpty(WatchedPath)) return false;
        return _sourcePath(tracked.Runner) == WatchedPath;
    }

    void SendRegister(MissRunner runner) => _send("register", [
        (long) runner.GetInstanceId(),
        _sourcePath(runner) ?? "",
        runner.Actor?.Name.ToString() ?? runner.Name.ToString(),
    ]);

    /// <summary>Handles editor to game messages, with or without the channel's prefix.</summary>
    public bool OnEditorMessage(string message, Godot.Collections.Array data) {
        var colon = message.LastIndexOf(':');
        switch (colon < 0 ? message : message[(colon + 1)..]) {
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
    /// Sends the state of every watched runner, right away. Waiting for the next tick is not enough:
    /// a paused game does not tick, so the editor, which clears its view whenever it switches what
    /// it watches, would stay blank until the game resumes.
    /// </summary>
    void SendCurrentStates() {
        foreach (var tracked in _tracked.Values) {
            tracked.Throttle.Invalidate();
            if (tracked.TickCount == 0) continue;
            if (!GodotObject.IsInstanceValid(tracked.Runner)) continue;
            if (IsStreaming(tracked)) Send(tracked);
        }
    }
}

/// <summary>
/// Runtime half of an addon's live-debug channel: a <see cref="RunnerDebugStream"/> hooked up to the
/// engine's debugger under the addon's prefix. Does nothing outside an editor-launched game, so
/// shipped builds pay no more than a couple of branch predictions. The capture is registered on the
/// first runner, so no addon needs an autoload.
/// </summary>
public sealed class RunnerDebugChannel {
    readonly string _prefix;
    readonly Func<MissRunner, string> _sourcePath;
    readonly Func<MissRunner, int, RunnerDebugState?> _state;

    RunnerDebugStream _stream;
    Callable _capture;

    public RunnerDebugChannel(string prefix, Func<MissRunner, string> sourcePath, Func<MissRunner, int, RunnerDebugState?> state) {
        _prefix = prefix;
        _sourcePath = sourcePath;
        _state = state;
    }

    static bool Active => !Engine.IsEditorHint() && OS.HasFeature("editor") && EngineDebugger.IsActive();

    public void Register(MissRunner runner) {
        if (Active) Stream.Register(runner);
    }

    public void Unregister(MissRunner runner) {
        if (Active) Stream.Unregister(runner);
    }

    public void SendState(MissRunner runner) {
        if (Active) Stream.SendState(runner);
    }

    /// <summary>A stream that sends to wherever it is told, with this channel's idea of a runner's state. For tests.</summary>
    public RunnerDebugStream NewStream(Action<string, Godot.Collections.Array> send, Func<ulong> clock) => new(send, clock, _sourcePath, _state);

    RunnerDebugStream Stream {
        get {
            if (_stream != null) return _stream;

            _stream = NewStream((message, data) => EngineDebugger.SendMessage($"{_prefix}:{message}", data), Time.GetTicksMsec);
            _capture = Callable.From<string, Godot.Collections.Array, bool>(_stream.OnEditorMessage);
            EngineDebugger.RegisterMessageCapture(_prefix, _capture);
            return _stream;
        }
    }
}
