#if TOOLS
using System.Collections.Generic;
using Godot;
using Misscore.Editor;

namespace Misstate.Editor;

/// <summary>
/// Editor half of the live-debug channel. A thin adapter: session bookkeeping and sending live
/// here, while decoding lives in <see cref="FsmDebugRouter"/>, which is testable.
/// <para>
/// The state a running machine is in lands straight in the authoring graph — the machine you are
/// editing is the machine you watch.
/// </para>
/// </summary>
[Tool]
public partial class MisstateDebuggerPlugin : EditorDebuggerPlugin {
    /// <summary>A GodotObject field so it is restored after an assembly reload — see <see cref="ReloadSafe"/>.</summary>
    GodotObject _panel;

    FsmEditorPanel Panel => ReloadSafe.Get<FsmEditorPanel>(ref _panel);

    public void Attach(FsmEditorPanel panel) => _panel = panel;

    // Neither of these survives a reload — plain C# state never does — so both are rebuilt on
    // demand. Sessions are learnt again from the next message, and runners the router no longer
    // knows are asked to announce themselves again.
    FsmDebugRouter _router;
    FsmDebugRouter Router => _router ??= new FsmDebugRouter();
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

    public override bool _HasCapture(string capture) => capture == MisstateDebug.Prefix;

    public override bool _Capture(string message, Godot.Collections.Array data, int sessionId) {
        // Learn the session from a message that demonstrably arrived, rather than trusting
        // _SetupSession to have run: it is not called again for a session that already existed.
        Remember(sessionId);

        var handled = Router.Handle(message, data, Panel);

        // A runner announcing itself proves the channel is up, so answer with what we want streamed.
        if (handled && FsmDebugRouter.IsRegistration(message)) SendWatchedMachine();

        // Runners coming or going may have moved the selection. The game ignores a repeat of what
        // it already watches.
        if (handled && FsmDebugRouter.ChangesRunners(message)) Send("watch_instance", [Router.Selected]);

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

    /// <summary>Follows the machine opened in the panel: streams that one, from one of its runners.</summary>
    public void WatchMachine() {
        SendWatchedMachine();
        Router.Retarget(Panel);
        Send("watch_instance", [Router.Selected]);
    }

    void SendWatchedMachine() => Send("watch_path", [Panel?.Machine?.ResourcePath ?? ""]);

    public void WatchInstance(long runnerId) {
        Router.Select(runnerId, Panel);
        Send("watch_instance", [runnerId]);
    }

    void Send(string message, Godot.Collections.Array data) {
        foreach (var sessionId in _sessions) {
            var session = GetSession(sessionId);
            if (session != null && session.IsActive()) session.SendMessage($"{MisstateDebug.Prefix}:{message}", data);
        }
    }

    void OnSessionStarted() {
        Router.Reset(Panel);
        SendWatchedMachine();
    }

    void OnSessionStopped() => Router.Reset(Panel);
}
#endif