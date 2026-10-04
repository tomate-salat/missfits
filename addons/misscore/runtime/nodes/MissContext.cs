using Godot;

namespace Misscore;

/// <summary>
/// Everything a node needs while ticking, whatever runs it — a behavior tree, a state machine.
/// Passed by value; it is a small readonly struct.
/// <para>
/// Note that <see cref="Delta"/> is handed down explicitly rather than being read from the engine
/// clock, so that time-based nodes behave identically on the idle and physics threads and can be
/// driven with an artificial delta in tests.
/// </para>
/// </summary>
public readonly record struct MissContext {
    /// <summary>The node being acted upon — usually the runner's parent.</summary>
    public Node Actor { get; init; }

    /// <summary>Scratch memory shared by all nodes of one running instance.</summary>
    public Blackboard Blackboard { get; init; }

    /// <summary>Seconds elapsed since the previous tick.</summary>
    public double Delta { get; init; }

    /// <summary>
    /// What drives this tick — a behavior tree's runner, say — e.g. to <see cref="IMissRunner.Stop"/>
    /// it from a leaf: <c>ctx.Runner?.Stop()</c>. Null when nodes are ticked without one.
    /// </summary>
    public IMissRunner Runner { get; init; }

    /// <summary>Told the status of every node as it is ticked, for live debugging. May be null.</summary>
    public INodeObserver Observer { get; init; }

    public T GetActor<T>() where T : Node => Actor as T;

    /// <summary>
    /// The runner as one particular kind, for what <see cref="IMissRunner"/> does not offer — null
    /// when something else drives the tick.
    /// </summary>
    public T GetRunner<T>() where T : class, IMissRunner => Runner as T;
}

/// <summary>Follows a running instance node by node, e.g. to stream statuses to the editor.</summary>
public interface INodeObserver {
    /// <param name="runtimeIndex">The ticked node's <see cref="MissNode.RuntimeIndex"/>.</param>
    void Report(int runtimeIndex, MissStatus status);
}