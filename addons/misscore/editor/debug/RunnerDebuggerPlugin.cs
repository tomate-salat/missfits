#if TOOLS
using System.Collections.Generic;
using Godot;

namespace Misscore.Editor;

/// <summary>
/// Editor half of an addon's live-debug channel. A thin adapter: session bookkeeping and sending
/// live here, while decoding lives in <see cref="RunnerDebugRouter"/>, which is testable.
/// <para>
/// What a running game is doing lands straight in the authoring graph — the resource you are
/// editing is the one you watch. An addon subclasses this to name its channel.
/// </para>
/// </summary>
[Tool]
public abstract partial class RunnerDebuggerPlugin : EditorDebuggerPlugin {
    /// <summary>The channel's name, which every message of it starts with.</summary>
    protected abstract string Prefix { get; }

    /// <summary>A GodotObject field so it is restored after an assembly reload — see <see cref="ReloadSafe"/>.</summary>
    GodotObject _view;

    IRunnerDebugView View => ReloadSafe.Get<Node>(ref _view) as IRunnerDebugView;

    /// <summary>Says which panel shows the running game. It has to be an <see cref="IRunnerDebugView"/>.</summary>
    public void Attach(Node view) => _view = view;

    // Neither of these survives a reload — plain C# state never does — so both are rebuilt on
    // demand. Sessions are learnt again from the next message, and runners the router no longer
    // knows are asked to announce themselves again.
    RunnerDebugRouter _router;
    RunnerDebugRouter Router => _router ??= new RunnerDebugRouter(Prefix);
    readonly List<int> _sessions = [];

    /// <summary>When the game was last asked to announce its runners, so states arriving meanwhile do not repeat the request.</summary>
    ulong _announceRequestedMsec;

    const ulong AnnounceRetryMsec = 1000;

    public override void _SetupSession(int sessionId) {
        Remember(sessionId);

        var session = GetSession(sessionId);
        ConnectOnce(session, EditorDebuggerSession.SignalName.Started, MethodName.OnSessionStarted);
        ConnectOnce(session, EditorDebuggerSession.SignalName.Stopped, MethodName.OnSessionStopped);
    }

    void ConnectOnce(GodotObject source, StringName signal, StringName method) {
        var callable = new Callable(this, method);
        if (!source.IsConnected(signal, callable)) source.Connect(signal, callable);
    }

    public override bool _HasCapture(string capture) => capture == Prefix;

    public override bool _Capture(string message, Godot.Collections.Array data, int sessionId) {
        // Learn the session from a message that demonstrably arrived, rather than trusting
        // _SetupSession to have run: it is not called again for a session that already existed.
        Remember(sessionId);

        var handled = Router.Handle(message, data, View);

        // A runner announcing itself proves the channel is up, so answer with what we want streamed.
        if (handled && Router.IsRegistration(message)) SendWatchedPath();

        // Runners coming or going may have moved the selection. The game ignores a repeat of what
        // it already watches.
        if (handled && Router.ChangesRunners(message)) Send("watch_instance", [Router.Selected]);

        // States from runners we were never told about: this router was rebuilt after a reload.
        if (Router.MissesRunners) RequestAnnouncement();

        return handled;
    }

    void RequestAnnouncement() {
        var now = Time.GetTicksMsec();
        if (_announceRequestedMsec != 0 && now - _announceRequestedMsec < AnnounceRetryMsec) return;
        _announceRequestedMsec = now;
        Send("announce", []);
    }

    void Remember(int sessionId) {
        if (!_sessions.Contains(sessionId)) _sessions.Add(sessionId);
    }

    /// <summary>Follows the resource opened in the view: streams that one, from one of its runners.</summary>
    public void WatchSource() {
        SendWatchedPath();
        Router.Retarget(View);
        Send("watch_instance", [Router.Selected]);
    }

    void SendWatchedPath() => Send("watch_path", [View?.WatchedPath ?? ""]);

    public void WatchInstance(long runnerId) {
        Router.Select(runnerId, View);
        Send("watch_instance", [runnerId]);
    }

    void Send(string message, Godot.Collections.Array data) {
        foreach (var sessionId in _sessions) {
            var session = GetSession(sessionId);
            if (session != null && session.IsActive()) session.SendMessage($"{Prefix}:{message}", data);
        }
    }

    void OnSessionStarted() {
        Router.Reset(View);
        SendWatchedPath();
    }

    void OnSessionStopped() => Router.Reset(View);
}
#endif
