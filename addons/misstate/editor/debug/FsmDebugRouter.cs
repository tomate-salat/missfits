#if TOOLS
using Misscore.Editor;

namespace Misstate.Editor;

/// <summary>The router of Misstate's live-debug channel. All it adds to <see cref="RunnerDebugRouter"/> is the channel's name.</summary>
public sealed class FsmDebugRouter() : RunnerDebugRouter(MisstateDebug.Prefix);
#endif
