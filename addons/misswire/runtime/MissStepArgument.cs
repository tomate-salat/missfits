using Godot;
using Misscore;

namespace Misswire;

public enum MissArgumentSource {
    /// <summary>One of the values the binding got, by <see cref="MissStepArgument.Index"/>.</summary>
    Input,

    /// <summary><see cref="MissStepArgument.Constant"/>, the same every time.</summary>
    Constant,
}

/// <summary>One argument of a step, when the step does not simply pass on what the binding got.</summary>
[GlobalClass, Tool]
public partial class MissStepArgument : MissResource {
    [Export]
    public MissArgumentSource Source { get; set; } = MissArgumentSource.Input;

    [Export]
    public int Index { get; set; }

    [Export]
    public Variant Constant { get; set; }
}
