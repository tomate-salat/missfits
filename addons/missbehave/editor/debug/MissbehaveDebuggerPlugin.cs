#if TOOLS
using System.Collections.Generic;
using Godot;
using Misscore.Editor;

namespace Missbehave.Editor;

/// <summary>
/// Editor half of the live-debug channel. A thin adapter: session bookkeeping and sending live
/// here, while decoding lives in <see cref="MissbehaveDebugRouter"/>, which is testable.
/// <para>
/// Statuses land straight in the authoring graph — the tree you are editing is the tree you watch,
/// so there is no separate debugger view to keep in sync.
/// </para>
/// </summary>
[Tool]
public partial class MissbehaveDebuggerPlugin : EditorDebuggerPlugin {
    /// <summary>A GodotObject field so it is restored after an assembly reload — see <see cref="ReloadSafe"/>.</summary>
    GodotObject _panel;

    BehaviorTreeEditorPanel Panel => ReloadSafe.Get<BehaviorTreeEditorPanel>(ref _panel);

    public void Attach(BehaviorTreeEditorPanel panel) => _panel = panel;

    // Neither of these survives a reload — plain C# state never does — so both are rebuilt on
    // demand. That includes reloads in the middle of a running game: the editor reloads by itself
    // whenever a newer build appears. Sessions are learnt again from the next message, and runners
    // the router no longer knows are asked to announce themselves again.
    MissbehaveDebugRouter _router;
    MissbehaveDebugRouter Router => _router ??= new MissbehaveDebugRouter();
    readonly List<int> _sessions = [];

    /// <summary>When the game was last asked to announce its runners, so frames arriving meanwhile do not repeat the request.</summary>
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

    public override bool _HasCapture(string capture) => capture == MissbehaveDebug.Prefix;

    public override bool _Capture(string message, Godot.Collections.Array data, int sessionId) {
        // Learn the session from a message that demonstrably arrived, rather than trusting
        // _SetupSession to have run: it is not called again for a session that already existed.
        Remember(sessionId);

        var handled = Router.Handle(message, data, Panel);

        // A runner announcing itself proves the channel is up, so answer with what we want streamed
        // rather than relying on having pushed it at exactly the right moment.
        if (handled && MissbehaveDebugRouter.IsRegistration(message)) SendWatchedTree();

        // Runners coming or going may have moved the selection, e.g. to the next enemy when the
        // watched one died. The game ignores a repeat of what it already watches.
        if (handled && MissbehaveDebugRouter.ChangesRunners(message)) Send("watch_instance", [Router.Selected]);

        // Frames from runners we were never told about: this router was rebuilt after a reload.
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

    /// <summary>Follows the tree opened in the panel: streams that one, from one of its runners.</summary>
    public void WatchTree() {
        SendWatchedTree();
        Router.Retarget(Panel);
        Send("watch_instance", [Router.Selected]);
    }

    void SendWatchedTree() => Send("watch_path", [Panel?.Tree?.ResourcePath ?? ""]);

    public void WatchInstance(long runnerId) {
        Router.Select(runnerId, Panel);
        Send("watch_instance", [runnerId]);
    }

    void Send(string message, Godot.Collections.Array data) {
        foreach (var sessionId in _sessions) {
            var session = GetSession(sessionId);
            if (session != null && session.IsActive()) {
                session.SendMessage($"{MissbehaveDebug.Prefix}:{message}", data);
            }
        }
    }

    void OnSessionStarted() {
        Router.Reset(Panel);
        SendWatchedTree();
    }

    void OnSessionStopped() => Router.Reset(Panel);
}
#endif
