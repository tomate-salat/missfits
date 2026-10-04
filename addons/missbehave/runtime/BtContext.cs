using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Everything a node needs while ticking. Passed by value; it is a small readonly struct.
/// <para>
/// Note that <see cref="Delta"/> is handed down explicitly rather than being read from the engine
/// clock, so that time-based decorators behave identically on the idle and physics threads and can
/// be driven with an artificial delta in tests.
/// </para>
/// </summary>
public readonly record struct BtContext {
    /// <summary>The node the tree acts upon — usually the runner's parent.</summary>
    public Node Actor { get; init; }

    /// <summary>Per-runner scratch memory shared by all nodes of one tree instance.</summary>
    public Blackboard Blackboard { get; init; }

    /// <summary>Seconds elapsed since the previous tick of this tree.</summary>
    public double Delta { get; init; }

    /// <summary>The runtime instance being ticked. Null when a node is ticked outside a tree.</summary>
    public BehaviorTreeInstance Instance { get; init; }

    /// <summary>
    /// The runner driving this tick, e.g. to <see cref="BehaviorTreeRunner.Stop"/> the tree from a
    /// leaf. Null when an instance is ticked without a runner.
    /// </summary>
    public BehaviorTreeRunner Runner { get; init; }
    
    public T GetActor<T>() where T : Node => Actor as T;
}

/// <summary>Lets a node hand its context to a parameter, which itself only knows the blackboard.</summary>
public static class BbParamContextExtensions {
    /// <inheritdoc cref="BbParam{T}.Get(Blackboard)"/>
    public static T Get<[MustBeVariant] T>(this BbParam<T> param, BtContext ctx) => param.Get(ctx.Blackboard);

    /// <inheritdoc cref="BbParam{T}.Set(Blackboard, T)"/>
    public static void Set<[MustBeVariant] T>(this BbParam<T> param, BtContext ctx, T value) => param.Set(ctx.Blackboard, value);
}
