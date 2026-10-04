using Godot;

namespace Misstate;

/// <summary>
/// Runtime half of the live-debug channel. No-ops outside an editor-launched game, so shipped
/// builds pay nothing but a couple of branch predictions.
/// <para>
/// Only runners whose machine is the one currently open in the editor send anything, and only when
/// something changed. The bookkeeping itself lives in <see cref="FsmDebugStream"/>.
/// </para>
/// </summary>
public static class MisstateDebug {
    public const string Prefix = "misstate";

    static FsmDebugStream _stream;
    static Callable _capture;

    static bool Active => !Engine.IsEditorHint() && OS.HasFeature("editor") && EngineDebugger.IsActive();

    public static void Register(FsmRunner runner) {
        if (!Active) return;
        Stream.Register(runner);
    }

    public static void Unregister(FsmRunner runner) {
        if (!Active) return;
        Stream.Unregister(runner);
    }

    public static void SendState(FsmRunner runner) {
        if (!Active) return;
        Stream.SendState(runner);
    }

    static FsmDebugStream Stream {
        get {
            if (_stream != null) return _stream;

            _stream = new FsmDebugStream(
                (message, data) => EngineDebugger.SendMessage($"{Prefix}:{message}", data),
                Time.GetTicksMsec);

            // Registered lazily on the first runner, so the addon needs no autoload.
            _capture = Callable.From<string, Godot.Collections.Array, bool>(_stream.OnEditorMessage);
            EngineDebugger.RegisterMessageCapture(Prefix, _capture);
            return _stream;
        }
    }
}