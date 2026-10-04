#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Misscore.Editor;

/// <summary>One running runner the game has announced.</summary>
public sealed class RunnerDebugInfo {
    public long Id { get; init; }

    /// <summary>Path of the resource the runner plays.</summary>
    public string SourcePath { get; init; }

    public string ActorName { get; init; }
}

/// <summary>What shows a running game in an editor: the panel that has the resource open.</summary>
public interface IRunnerDebugView {
    /// <summary>Path of the resource that is open — empty for one without a file, null for none at all.</summary>
    string WatchedPath { get; }

    /// <summary>Shows what the watched runner reported: the state as the game sent it, without the runner's id.</summary>
    void ShowLive(Godot.Collections.Array state);

    void ClearLive();

    /// <summary>The runners the game has announced changed, or the one that is watched.</summary>
    void OnRunnersChanged(IReadOnlyList<RunnerDebugInfo> runners, long selected);
}

/// <summary>
/// Decodes the debug messages coming back from the running game and drives a view with them.
/// <para>
/// Kept out of the debugger plugin on purpose: an EditorDebuggerPlugin cannot be constructed outside
/// the editor, so the protocol handling would otherwise be untestable. It holds no reference to the
/// view — the view is passed in per call — because the plugin recreates this object after an
/// assembly reload and such references would silently be gone.
/// </para>
/// </summary>
public class RunnerDebugRouter(string prefix) {
    readonly string _prefixed = prefix + ":";
    readonly Dictionary<long, RunnerDebugInfo> _runners = [];

    public long Selected { get; private set; } = -1;

    /// <summary>
    /// The two directions of the debugger channel disagree about the prefix — a game-side capture
    /// callback gets the bare "watch_path", while the editor side is handed the full
    /// "prefix:register" — so both forms are accepted everywhere.
    /// </summary>
    string KeyOf(string message) => message.StartsWith(_prefixed) ? message[_prefixed.Length..] : message;

    /// <summary>True for the message a runner sends when it comes up, which is what starts the handshake.</summary>
    public bool IsRegistration(string message) => KeyOf(message) == "register";

    /// <summary>True when runners came or went, so the game has to be told which one is watched now.</summary>
    public bool ChangesRunners(string message) => KeyOf(message) is "register" or "unregister";

    public bool Handle(string message, Godot.Collections.Array data, IRunnerDebugView view) {
        switch (KeyOf(message)) {
            case "register":
                if (data.Count < 3) return false;
                var info = new RunnerDebugInfo { Id = data[0].AsInt64(), SourcePath = data[1].AsString(), ActorName = data[2].AsString() };
                _runners[info.Id] = info;
                MissesRunners = false;
                Retarget(view);
                return true;

            case "unregister":
                if (data.Count < 1) return false;
                _runners.Remove(data[0].AsInt64());
                Retarget(view);
                return true;

            case "state":
                return State(data, view);

            default:
                return false;
        }
    }

    /// <summary>
    /// Makes sure the watched runner is one that exists and plays the open resource, and refreshes
    /// the instance picker. Needed whenever runners come or go or another resource is opened. The
    /// caller tells the game about <see cref="Selected"/> afterwards.
    /// </summary>
    public void Retarget(IRunnerDebugView view) {
        var path = view?.WatchedPath ?? "";
        var previous = Selected;

        if (!Keeps(Selected, path)) Selected = Pick(path);
        if (Selected != previous) view?.ClearLive();
        view?.OnRunnersChanged([.. _runners.Values], Selected);
    }

    bool Keeps(long id, string path) {
        if (!_runners.TryGetValue(id, out var info)) return false;
        return string.IsNullOrEmpty(path) || info.SourcePath == path || !_runners.Values.Any(r => r.SourcePath == path);
    }

    long Pick(string path) {
        var matching = _runners.Values.FirstOrDefault(r => string.IsNullOrEmpty(path) || r.SourcePath == path)
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

    bool State(Godot.Collections.Array data, IRunnerDebugView view) {
        if (data.Count < 2) return false;

        var id = data[0].AsInt64();
        if (!_runners.TryGetValue(id, out var info)) {
            MissesRunners = true;
            return true;
        }
        if (Selected >= 0 && id != Selected) return true;
        // A runner of another resource: nothing of it can be shown in the open one.
        if (view?.WatchedPath is { } open && info.SourcePath != open) return true;

        view?.ShowLive(data.Slice(1));
        return true;
    }

    public void Select(long runnerId, IRunnerDebugView view) {
        Selected = runnerId;
        view?.ClearLive();
    }

    public void Reset(IRunnerDebugView view) {
        _runners.Clear();
        Selected = -1;
        MissesRunners = false;
        view?.ClearLive();
        view?.OnRunnersChanged([], -1);
    }
}
#endif
