#if TOOLS
using Godot;
using Misscore.Editor;

namespace Misstate.Editor;

/// <summary>
/// Editor half of Misstate's live-debug channel: the state a running machine is in lands straight
/// in the authoring graph. Everything it does is <see cref="RunnerDebuggerPlugin"/>'s.
/// </summary>
[Tool]
public partial class MisstateDebuggerPlugin : RunnerDebuggerPlugin {
    protected override string Prefix => MisstateDebug.Prefix;
}
#endif
