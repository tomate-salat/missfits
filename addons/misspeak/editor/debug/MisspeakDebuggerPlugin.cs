#if TOOLS
using Godot;
using Misscore.Editor;

namespace Misspeak.Editor;

/// <summary>
/// Editor half of Misspeak's live-debug channel: where a running dialogue is lands straight in the
/// authoring graph. Everything it does is <see cref="RunnerDebuggerPlugin"/>'s.
/// </summary>
[Tool]
public partial class MisspeakDebuggerPlugin : RunnerDebuggerPlugin {
    protected override string Prefix => MisspeakDebug.Prefix;
}
#endif
