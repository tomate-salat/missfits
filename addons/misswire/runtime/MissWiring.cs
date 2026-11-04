using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Array = Godot.Collections.Array;

namespace Misswire;

/// <summary>
/// What a binding does, without holding anything: the mediator in the game and the editor plugin
/// both run bindings through here, each with its own connections.
/// <para>
/// A property may lead into a resource the node holds, the way Godot writes it:
/// <c>Outfit:Hair</c> is <c>Hair</c> of the resource in <c>Outfit</c>. A step's target may do the
/// same, <c>../Model:Outfit</c> being the resource in <c>Outfit</c> of the node <c>../Model</c>.
/// </para>
/// </summary>
public static class MissWiring {
    /// <summary>The node a binding reads from, or null if its path leads nowhere.</summary>
    public static Node SourceNode(Node mediator, MissBinding binding) =>
        binding.Source.IsEmpty ? null : mediator.GetNodeOrNull(NodePart(binding.Source));

    /// <summary>Whether the binding's property leads into a resource (<c>Outfit:Hair</c>).</summary>
    public static bool IsNested(MissBinding binding) => binding.Property.ToString().Contains(':');

    /// <summary>The value at a property path of the source: <c>Outfit</c>, or <c>Outfit:Hair</c>.</summary>
    public static Variant Read(GodotObject source, StringName property) => source.GetIndexed(new NodePath(property.ToString()));

    /// <summary>The resource a <see cref="MissSourceKind.ResourceSignal"/> binding listens to right now, if any.</summary>
    public static GodotObject HeldResource(Node source, MissBinding binding) => Read(source, binding.Property).AsGodotObject();

    /// <summary>
    /// The resources a nested property passes through, outermost first: for <c>Outfit:Hair</c> the
    /// outfit. Any of them saying <c>changed</c> may mean a new value.
    /// </summary>
    public static List<Resource> Chain(Node source, MissBinding binding) {
        var chain = new List<Resource>();
        var parts = binding.Property.ToString().Split(':');
        GodotObject at = source;
        for (var i = 0; i < parts.Length - 1 && at != null; i++) {
            at = at.Get(parts[i]).AsGodotObject();
            if (at is Resource resource) chain.Add(resource);
        }
        return chain;
    }

    /// <summary>
    /// The node's signal for a new value of the binding's property: the one the binding names, or
    /// left empty, <c>&lt;Property&gt;Changed</c> or <c>&lt;property&gt;_changed</c> if the node has
    /// it. For a nested property it is the node's own property that counts: <c>OutfitChanged</c> for
    /// <c>Outfit:Hair</c>. Looked up each time, so a signal added to the script later is found
    /// without rewiring.
    /// </summary>
    public static StringName ChangeSignalOf(Node source, MissBinding binding) {
        if (!binding.ChangeSignal.IsEmpty) return binding.ChangeSignal;
        if (source == null || binding.Property.IsEmpty) return new StringName();
        var property = binding.Property.ToString().Split(':')[0];
        foreach (var candidate in new[] { $"{property}Changed", $"{property.ToSnakeCase()}_changed" }) {
            if (source.HasSignal(candidate)) return candidate;
        }
        return new StringName();
    }

    /// <summary>What the steps get: the property's value, or the signal's arguments.</summary>
    public static Array Input(Node source, MissBinding binding, Array signalArguments) =>
        binding.Kind == MissSourceKind.Property ? [Read(source, binding.Property)] : signalArguments;

    /// <summary>The node part of a path, without what leads into its properties.</summary>
    public static NodePath NodePart(NodePath path) {
        if (path.GetSubNameCount() == 0) return path;
        var names = path.GetConcatenatedNames();
        return new NodePath(path.IsAbsolute() ? $"/{names}" : names);
    }

    /// <summary>What a step acts on: the node, or the resource its target path leads into.</summary>
    public static GodotObject TargetOf(Node mediator, MissBindingStep step) {
        if (step.Target.IsEmpty || mediator.GetNodeOrNull(NodePart(step.Target)) is not { } node) return null;
        return step.Target.GetSubNameCount() == 0 ? node : node.GetIndexed(new NodePath(step.Target.GetConcatenatedSubNames())).AsGodotObject();
    }

    /// <summary>
    /// Runs the steps top to bottom. A step that cannot run is reported and skipped. In the editor,
    /// <paramref name="canCall"/> keeps calls away from targets that are only placeholders.
    /// </summary>
    public static void Run(Node mediator, MissBindingGraph graph, MissBinding binding, Array input, Action<string> warn,
                           Func<GodotObject, bool> canCall = null) {
        for (var i = 0; i < binding.Steps.Count; i++) {
            if (binding.Steps[i] is not { } step) continue;
            var where = $"{Describe(binding)}, step {i + 1}";
            var target = TargetOf(mediator, step);
            if (target == null) {
                warn($"{where}: nothing at '{step.Target}'");
                continue;
            }

            var arguments = Arguments(mediator, graph, step, input, out var missing);
            if (missing != null) {
                warn($"{where}: {missing}");
                continue;
            }
            var name = MissSignalRelay.Describe(target);
            switch (step.Kind) {
                case MissStepKind.Call when canCall != null && !canCall(target):
                    warn($"{where}: {name} has no [Tool], so '{step.Member}' runs only in the game");
                    break;
                case MissStepKind.Call:
                    if (ParameterCount(target, step.Member) is not var (required, total)) {
                        warn($"{where}: {name} has no method '{step.Member}'");
                        break;
                    }
                    // Passed on as it came, the input may hold more than the method takes: a value
                    // for ApplyOutfit(), say. The rest is dropped, as Callable.unbind would.
                    if (step.Arguments.Count == 0 && arguments.Count > total) arguments = arguments.Slice(0, total);
                    // A C# method called by name needs every argument; its defaults fill in what is left out.
                    if (arguments.Count >= required && arguments.Count < total) {
                        arguments = arguments.Duplicate();
                        MissDefaults.Fill(target, step.Member, total, arguments);
                    }
                    if (arguments.Count < required) {
                        warn($"{where}: '{step.Member}' needs {required} arguments, gets {arguments.Count}");
                        break;
                    }
                    if (WrongType(target, step, arguments) is { } callProblem) {
                        warn($"{where}: {callProblem}");
                        break;
                    }
                    target.Callv(step.Member, arguments);
                    break;
                case MissStepKind.Set:
                    if (WrongType(target, step, arguments) is { } setProblem) {
                        warn($"{where}: {setProblem}");
                        break;
                    }
                    target.Set(step.Member, arguments.Count > 0 ? arguments[0] : default);
                    break;
            }
        }
    }

    /// <summary>
    /// What is wrong with the type of a step's one value, or null. A value of the wrong type would
    /// only end in an engine error; this says it plainly instead.
    /// </summary>
    static string WrongType(GodotObject target, MissBindingStep step, Array arguments) {
        if (step.Arguments.Count > 0 || arguments.Count == 0) return null;
        var wanted = WantedType(target, step);
        return Fits(arguments[0].VariantType, wanted) ? null : $"'{step.Member}' needs {wanted}, gets {arguments[0].VariantType}";
    }

    /// <summary>How a binding names itself in warnings: its source, as the graph shows it.</summary>
    public static string Describe(MissBinding binding) => binding.Kind switch {
        MissSourceKind.Property => $"'{binding.Source}' {binding.Property}",
        MissSourceKind.Signal => $"'{binding.Source}' {binding.Signal}",
        _ => $"'{binding.Source}' {binding.Property}.{binding.Signal}",
    };

    /// <summary>
    /// Whether a value can go where a type is wanted: anything into a Variant, the same type,
    /// numbers into numbers, text into text, nothing into an object.
    /// </summary>
    public static bool Fits(Variant.Type value, Variant.Type wanted) {
        if (wanted == Variant.Type.Nil || value == wanted) return true;
        if (value == Variant.Type.Nil) return wanted == Variant.Type.Object;
        static bool Number(Variant.Type t) => t is Variant.Type.Int or Variant.Type.Float;
        static bool Text(Variant.Type t) => t is Variant.Type.String or Variant.Type.StringName;
        return (Number(value) && Number(wanted)) || (Text(value) && Text(wanted));
    }

    /// <summary>The type a step's one value has to have: the method's first parameter, or the property; Nil if not known.</summary>
    public static Variant.Type WantedType(GodotObject target, MissBindingStep step) {
        var list = step.Kind == MissStepKind.Call ? target.GetMethodList() : target.GetPropertyList();
        foreach (var entry in list) {
            if (entry["name"].AsStringName() != step.Member) continue;
            if (step.Kind == MissStepKind.Set) return (Variant.Type) entry["type"].AsInt32();
            var args = entry["args"].AsGodotArray();
            return args.Count == 0 ? Variant.Type.Nil : (Variant.Type) args[0].AsGodotDictionary()["type"].AsInt32();
        }
        return Variant.Type.Nil;
    }

    /// <returns>How many arguments the method needs and takes at most, or null if there is no such method.</returns>
    public static (int Required, int Total)? ParameterCount(GodotObject target, StringName method) {
        foreach (var entry in target.GetMethodList()) {
            if (entry["name"].AsStringName() != method) continue;
            var total = entry["args"].AsGodotArray().Count;
            return (MissDefaults.Required(target, method, total, total - entry["default_args"].AsGodotArray().Count), total);
        }
        return null;
    }

    /// <summary>
    /// What a step hands its target: what the binding got, or the one value wired into its port —
    /// read now, from a property or a constant. Explains in <paramref name="missing"/> when the
    /// port has nothing to give.
    /// </summary>
    public static Array Arguments(Node mediator, MissBindingGraph graph, MissBindingStep step, Array input, out string missing) {
        missing = null;
        if (step.Arguments.Count > 0) return Legacy(step, input);
        if (step.ValueFrom == MissStepValue.Binding) return input;

        if (step.ValueConstant != "") {
            if (graph?.Constants.FirstOrDefault(c => c != null && c.Id == step.ValueConstant) is { } constant) {
                if (constant.HoldsResource && constant.Value.AsGodotObject() == null) {
                    missing = "its constant has no resource yet";
                    return [];
                }
                // A constant of type Node holds the path to a node of the scene; the step gets the node.
                if (constant.Value.VariantType != Variant.Type.NodePath) return [constant.Value];
                if (mediator.GetNodeOrNull(constant.Value.AsNodePath()) is { } picked) return [picked];
                missing = $"its constant points to no node at '{constant.Value.AsNodePath()}'";
                return [];
            }
            missing = "its constant is gone";
            return [];
        }
        if (!step.ValueSource.IsEmpty && step.ValueSource.GetSubNameCount() > 0
            && mediator.GetNodeOrNull(NodePart(step.ValueSource)) is { } node) {
            return [node.GetIndexed(new NodePath(step.ValueSource.GetConcatenatedSubNames()))];
        }
        missing = step.ValueSource.IsEmpty ? "nothing is wired into its value port" : $"nothing at '{step.ValueSource}'";
        return [];
    }

    static Array Legacy(MissBindingStep step, Array input) {

        var arguments = new Array();
        foreach (var argument in step.Arguments) {
            arguments.Add(argument switch {
                null => default,
                { Source: MissArgumentSource.Constant } => argument.Constant,
                _ => argument.Index >= 0 && argument.Index < input.Count ? input[argument.Index] : default,
            });
        }
        return arguments;
    }
}
