#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misstate.Editor;

/// <summary>One running <see cref="FsmRunner"/> the game has announced.</summary>
public sealed class FsmRunnerInfo {
    public long Id { get; init; }
    public string MachinePath { get; init; }
    public string ActorName { get; init; }
}

/// <summary>
/// Decodes the debug messages coming back from the running game and drives the panel with them.
/// <para>
/// Kept out of <see cref="MisstateDebuggerPlugin"/> on purpose: an EditorDebuggerPlugin cannot be
/// constructed outside the editor, so the protocol handling would otherwise be untestable. It holds
/// no reference to the panel — the panel is passed in per call — because the plugin recreates this
/// object after an assembly reload and such references would silently be gone.
/// </para>
/// </summary>
public sealed class FsmDebugRouter {
    public const string Prefixed = MisstateDebug.Prefix + ":";

    readonly Dictionary<long, FsmRunnerInfo> _runners = [];

    public long Selected { get; private set; } = -1;

    /// <summary>
    /// The two directions of the debugger channel disagree about the prefix — a game-side capture
    /// callback gets the bare "watch_path", while the editor side is handed the full
    /// "misstate:register" — so both forms are accepted everywhere.
    /// </summary>
    static string KeyOf(string message) => message.StartsWith(Prefixed) ? message[Prefixed.Length..] : message;

    /// <summary>True for the message a runner sends when it comes up, which is what starts the handshake.</summary>
    public static bool IsRegistration(string message) => KeyOf(message) == "register";

    /// <summary>True when runners came or went, so the game has to be told which one is watched now.</summary>
    public static bool ChangesRunners(string message) => KeyOf(message) is "register" or "unregister";

    public bool Handle(string message, Godot.Collections.Array data, FsmEditorPanel panel) {
        switch (KeyOf(message)) {
            case "register":
                if (data.Count < 3) return false;
                var info = new FsmRunnerInfo { Id = data[0].AsInt64(), MachinePath = data[1].AsString(), ActorName = data[2].AsString() };
                _runners[info.Id] = info;
                MissesRunners = false;
                Retarget(panel);
                return true;

            case "unregister":
                if (data.Count < 1) return false;
                _runners.Remove(data[0].AsInt64());
                Retarget(panel);
                return true;

            case "state":
                return State(data, panel);

            default:
                return false;
        }
    }

    /// <summary>
    /// Makes sure the watched runner is one that exists and runs the open machine, and refreshes
    /// the instance picker. Needed whenever runners come or go or another machine is opened. The
    /// caller tells the game about <see cref="Selected"/> afterwards.
    /// </summary>
    public void Retarget(FsmEditorPanel panel) {
        var path = panel?.Machine?.ResourcePath ?? "";
        var previous = Selected;

        if (!Keeps(Selected, path)) Selected = Pick(path);
        if (Selected != previous) panel?.ClearLive();
        panel?.OnRunnersChanged([.. _runners.Values], Selected);
    }

    bool Keeps(long id, string path) {
        if (!_runners.TryGetValue(id, out var info)) return false;
        return string.IsNullOrEmpty(path) || info.MachinePath == path || !_runners.Values.Any(r => r.MachinePath == path);
    }

    long Pick(string path) {
        var matching = _runners.Values.FirstOrDefault(r => string.IsNullOrEmpty(path) || r.MachinePath == path)
                       ?? _runners.Values.FirstOrDefault();
        return matching?.Id ?? -1;
    }

    /// <summary>
    /// True once a state arrived from a runner this router has never been told about. The game only
    /// announces its runners when they start, so this is how a router that lost them finds out —
    /// the editor reloads its assembly on its own whenever a newer build appears, mid-game included.
    /// The caller asks the game to announce its runners again.
    /// </summary>
    public bool MissesRunners { get; private set; }

    bool State(Godot.Collections.Array data, FsmEditorPanel panel) {
        if (data.Count < 3) return false;

        var id = data[0].AsInt64();
        if (!_runners.TryGetValue(id, out var info)) {
            MissesRunners = true;
            return true;
        }
        if (Selected >= 0 && id != Selected) return true;
        // A runner of another machine: nothing of it can be shown in the open one.
        if (panel?.Machine != null && info.MachinePath != panel.Machine.ResourcePath) return true;

        panel?.ShowLive(data[1].AsString(), data[2].AsByteArray(), data.Count > 4 ? data[4].AsString() : "");
        return true;
    }

    public void Select(long runnerId, FsmEditorPanel panel) {
        Selected = runnerId;
        panel?.ClearLive();
    }

    public void Reset(FsmEditorPanel panel) {
        _runners.Clear();
        Selected = -1;
        MissesRunners = false;
        panel?.ClearLive();
        panel?.OnRunnersChanged([], -1);
    }
}
#endif