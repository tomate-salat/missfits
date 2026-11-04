using Godot;
using Misscore;

namespace Misswire;

public enum MissStepKind {
    /// <summary>Calls the method <see cref="MissBindingStep.Member"/>, with at most one value.</summary>
    Call,

    /// <summary>Sets the property <see cref="MissBindingStep.Member"/> to the value.</summary>
    Set,
}

/// <summary>Where a step's value comes from.</summary>
public enum MissStepValue {
    /// <summary>What the binding got: the property's value, or the signal's argument.</summary>
    Binding,

    /// <summary>
    /// What is wired into the step's port: a property (<see cref="MissBindingStep.ValueSource"/>) or
    /// a constant (<see cref="MissBindingStep.ValueConstant"/>). It is read when the step runs and
    /// never sets the binding off by itself.
    /// </summary>
    Port,
}

/// <summary>
/// One thing a binding does when it fires: call a method that takes at most one value, or set a
/// property. The value is what the binding got, unless the step takes it through its port.
/// </summary>
[GlobalClass, Tool]
public partial class MissBindingStep : MissResource {
    [Export]
    public MissStepKind Kind { get; set; } = MissStepKind.Call;

    [Export]
    public NodePath Target { get; set; } = new();

    [Export]
    public StringName Member { get; set; } = new();

    [Export]
    public MissStepValue ValueFrom { get; set; } = MissStepValue.Binding;

    /// <summary>For a value through the port: a node's property, as a path (<c>../Model:Outfit:Hair</c>).</summary>
    [Export]
    public NodePath ValueSource { get; set; } = new();

    /// <summary>For a value through the port: the id of one of the graph's <see cref="MissBindingGraph.Constants"/>.</summary>
    [Export]
    public string ValueConstant { get; set; } = "";

    /// <summary>
    /// Arguments set one by one, from before steps took one value. Still honoured when present;
    /// the graph editor no longer makes them.
    /// </summary>
    [Export]
    public Godot.Collections.Array<MissStepArgument> Arguments { get; set; } = [];

    /// <summary>Which box of the target node the wire ends at in the graph editor; empty for its main box.</summary>
    [Export]
    public string TargetBox { get; set; } = "";

    /// <summary>Which box of the value's node the value wire leaves in the graph editor; empty for its main box.</summary>
    [Export]
    public string ValueBox { get; set; } = "";

    public override void _ValidateProperty(Godot.Collections.Dictionary property) {
        if (property["name"].AsString() is nameof(TargetBox) or nameof(ValueBox) or nameof(ValueConstant) or nameof(Arguments)) {
            property["usage"] = (int) PropertyUsageFlags.Storage;
        }
    }
}
