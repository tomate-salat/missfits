using Godot;
using Misscore;

namespace Missbehave;

/// <summary>
/// Base of composites that visit their children in a shuffled order. The order is drawn once per
/// run (on BeforeRun) and then held, so a Running child is resumed rather than reshuffled away.
/// </summary>
[GlobalClass, Tool]
public abstract partial class ARandomizedCompositeNode : ACompositeNode {
    /// <summary>Fixed seed for reproducible runs. 0 means a fresh random sequence per instance.</summary>
    [Export]
    public int RandomSeed { get; set; }

    /// <summary>
    /// Optional per-child weights, aligned with <see cref="MissNode.Children"/>. A child with
    /// twice the weight is roughly twice as likely to come up first. Ignored unless the array has
    /// exactly one entry per child.
    /// </summary>
    [Export]
    public Godot.Collections.Array<int> Weights { get; set; } = [];

    /// <summary>Child indices in the order this run visits them.</summary>
    protected int[] Order = [];

    /// <summary>Position within <see cref="Order"/> to resume from.</summary>
    protected int Slot;

    RandomNumberGenerator _rng;

    public override void BeforeRun(MissContext ctx) {
        Shuffle();
        Slot = 0;
    }

    protected override void OnCloned() {
        base.OnCloned();
        _rng = null;
        Order = [];
        Slot = 0;
    }

    public override void Interrupt(MissContext ctx) {
        Slot = 0;
        base.Interrupt(ctx);
    }

    /// <summary>Makes sure an order exists, e.g. when the node is ticked without a BeforeRun.</summary>
    protected void EnsureOrder() {
        if (Order.Length != Children.Count) Shuffle();
    }

    protected void Shuffle() {
        var count = Children.Count;
        var order = new int[count];
        for (var i = 0; i < count; i++) order[i] = i;

        var rng = Rng();
        if (Weights.Count == count && count > 1) {
            // Weighted random sampling (Efraimidis & Spirakis): give each item the key
            // random^(1/weight) and sort descending. Heavier items tend to sort to the front.
            var keys = new double[count];
            for (var i = 0; i < count; i++) {
                var weight = Mathf.Max(1, Weights[i]);
                keys[i] = Mathf.Pow(rng.Randf(), 1.0f / weight);
            }
            System.Array.Sort(keys, order);
            System.Array.Reverse(order);
        }
        else {
            for (var i = count - 1; i > 0; i--) {
                var j = rng.RandiRange(0, i);
                (order[i], order[j]) = (order[j], order[i]);
            }
        }

        Order = order;
    }

    RandomNumberGenerator Rng() {
        if (_rng != null) return _rng;
        _rng = new RandomNumberGenerator();
        if (RandomSeed != 0) _rng.Seed = (ulong) RandomSeed;
        else _rng.Randomize();
        return _rng;
    }

    public override string GetSummary() {
        if (Weights.Count == Children.Count && Children.Count > 0) return "weighted random";
        return RandomSeed != 0 ? $"random (seed {RandomSeed})" : "random";
    }
}
