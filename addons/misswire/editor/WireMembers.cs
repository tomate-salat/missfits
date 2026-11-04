#if TOOLS
using System.Collections.Generic;
using System.Linq;
using Godot;
using Godot.Collections;

namespace Misswire.Editor;

/// <summary>A property, signal or method that a box in the graph can show as a row.</summary>
public readonly record struct WireMember(string Kind, string Name, int Arguments, bool HoldsResource, string Parameters = "", string Type = "") {
    public const string Property = "property";
    public const string Signal = "signal";
    public const string Method = "method";

    /// <summary>A wire can start here: a value to read or a signal to listen to.</summary>
    public bool IsOutput => Kind is Property or Signal;

    /// <summary>A wire can end here: a method to call or a property to set.</summary>
    public bool IsInput => Kind is Property or Method;

    public string Label => Kind switch {
        Signal => Parameters == "" ? $"» {Name}" : $"» {Name}({Parameters})",
        Method => $"{Name}({Parameters})",
        _ => $"{Name}{(Type == "" ? "" : $": {Type}")}{(HoldsResource ? " ▸" : "")}",
    };

    /// <summary>The label with BBCode: a property's type set off, quieter than its name.</summary>
    public string RichLabel => Kind == Property && Type != ""
        ? $"{Escape(Name)}  [color=#ffffff70]{Escape(Type)}[/color]{(HoldsResource ? " ▸" : "")}"
        : Escape(Label);

    public static string Escape(string text) => text.Replace("[", "[lb]");
}

/// <summary>
/// What an object has to offer to the graph. It reads the lists through Godot's property system,
/// which works for placeholders too: a script without <c>[Tool]</c> still lists its members.
/// </summary>
public static class WireMembers {
    const PropertyUsageFlags NotAProperty = PropertyUsageFlags.Category | PropertyUsageFlags.Group | PropertyUsageFlags.Subgroup;

    /// <summary>What the object's script declares: shown in its box from the start.</summary>
    public static List<WireMember> Declared(GodotObject obj) {
        if (obj?.GetScript().As<Script>() is not { } script) return [];
        return [
            .. Properties(script.GetScriptPropertyList()),
            .. Signals(script.GetScriptSignalList()),
            .. Methods(script.GetScriptMethodList(), MissDefaults.ClassOf(script)),
        ];
    }

    /// <summary>Everything the object has, its engine class included: offered to add by hand.</summary>
    public static List<WireMember> All(GodotObject obj) {
        if (obj == null) return [];
        return [
            .. Properties(obj.GetPropertyList()).DistinctBy(m => m.Name),
            .. Signals(obj.GetSignalList()).DistinctBy(m => m.Name),
            .. Methods(obj.GetMethodList(), MissDefaults.ClassOf(obj)).DistinctBy(m => m.Name),
        ];
    }

    /// <summary>
    /// What a resource box shows from the start: what the resource's script declares — properties,
    /// signals, methods — and <c>changed</c>. The engine's other members are offered to add by hand.
    /// </summary>
    public static List<WireMember> ResourceOffer(GodotObject resource) {
        var members = Declared(resource);
        if (!members.Any(m => m.Kind == WireMember.Signal && m.Name == "changed")) members.Add(new WireMember(WireMember.Signal, "changed", 0, false));
        return members;
    }

    /// <summary>
    /// Everything the object has, by the class that brings it, most derived first: its script (and
    /// the scripts it extends), then the engine classes down to <c>Object</c>. Each member appears
    /// once, under the class that declares it.
    /// </summary>
    public static List<(string Class, List<WireMember> Members)> ByClass(GodotObject obj) {
        if (obj == null) return [];
        var chain = new List<(string Title, List<WireMember> Members)>();
        void Add(string title, IEnumerable<WireMember> members) => chain.Add((title, members.ToList()));

        for (var script = obj.GetScript().As<Script>(); script != null; script = script.GetBaseScript()) {
            var name = script.GetGlobalName().ToString();
            Add(string.IsNullOrEmpty(name) ? script.ResourcePath.GetFile().GetBaseName() : name, [
                .. Properties(script.GetScriptPropertyList()),
                .. Signals(script.GetScriptSignalList()),
                .. Methods(script.GetScriptMethodList(), MissDefaults.ClassOf(script)),
            ]);
        }
        for (StringName type = obj.GetClass(); !type.IsEmpty; type = ClassDB.GetParentClass(type)) {
            Add(type, [
                .. Properties(ClassDB.ClassGetPropertyList(type, true)),
                .. Signals(ClassDB.ClassGetSignalList(type, true)),
                .. Methods(ClassDB.ClassGetMethodList(type, true)),
            ]);
        }

        // A script's lists may repeat what the classes below it declare; a member belongs to the
        // lowest class that has it, so the chain is worked through from Object upwards.
        var seen = new HashSet<string>();
        var groups = new List<(string, List<WireMember>)>();
        for (var i = chain.Count - 1; i >= 0; i--) {
            var own = chain[i].Members.Where(m => seen.Add($"{m.Kind}:{m.Name}")).OrderBy(m => Order(m.Kind)).ThenBy(m => m.Name).ToList();
            if (own.Count > 0) groups.Insert(0, (chain[i].Title, own));
        }
        return groups;
    }

    static int Order(string kind) => kind switch { WireMember.Property => 0, WireMember.Signal => 1, _ => 2 };

    public static WireMember? Find(GodotObject obj, string kind, string name) {
        foreach (var member in All(obj)) {
            if (member.Kind == kind && member.Name == name) return member;
        }
        return null;
    }

    static IEnumerable<WireMember> Properties(Array<Dictionary> list) {
        foreach (var entry in list) {
            var usage = (PropertyUsageFlags) entry["usage"].AsInt64();
            if ((usage & NotAProperty) != 0 || (usage & PropertyUsageFlags.Editor) == 0) continue;
            var name = entry["name"].AsString();
            if (name.StartsWith('_') || name == "script") continue;
            var holdsResource = entry["type"].AsInt32() == (int) Variant.Type.Object
                                && entry["hint"].AsInt32() == (int) PropertyHint.ResourceType;
            // An exported resource names its class in the hint, not always in class_name.
            var className = entry["class_name"].AsString();
            if (className == "" && holdsResource) className = entry["hint_string"].AsString();
            yield return new WireMember(WireMember.Property, name, 1, holdsResource, "", TypeName((Variant.Type) entry["type"].AsInt32(), className));
        }
    }

    static IEnumerable<WireMember> Signals(Array<Dictionary> list) {
        foreach (var entry in list) {
            var args = entry["args"].AsGodotArray();
            yield return new WireMember(WireMember.Signal, entry["name"].AsString(), args.Count, false, ParametersOf(args));
        }
    }

    /// <summary>A method's or signal's parameters as Godot writes them: <c>name: String, amount: int</c>.</summary>
    static string ParametersOf(Godot.Collections.Array args, List<Variant?> defaults = null) => string.Join(", ", args.Select(a => a.AsGodotDictionary()).Select((arg, i) => {
        var name = arg["name"].AsString();
        var value = defaults != null && i < defaults.Count && defaults[i] is { } given ? $" = {GD.VarToStr(given)}" : "";
        return $"{name}: {TypeName(arg)}{value}";
    }));

    static string TypeName(Dictionary arg) => TypeName((Variant.Type) arg["type"].AsInt32(), arg["class_name"].AsString());

    public static string TypeName(Variant.Type type, string className) => type switch {
        Variant.Type.Nil => "Variant",
        Variant.Type.Bool => "bool",
        Variant.Type.Int => "int",
        Variant.Type.Float => "float",
        Variant.Type.Object => className is { Length: > 0 } ? className : "Object",
        _ => type.ToString(),
    };

    /// <summary>
    /// The parameters of what a step does: those of the method it calls, or the one value of the
    /// property it sets. Empty if the target or member is not there.
    /// </summary>
    public static List<WireParameter> ParametersOf(GodotObject target, MissBindingStep step) {
        if (target == null) return [];
        if (step.Kind == MissStepKind.Set) {
            foreach (var entry in target.GetPropertyList()) {
                if (entry["name"].AsString() != step.Member) continue;
                return [new WireParameter(step.Member, (Variant.Type) entry["type"].AsInt32(), entry["class_name"].AsString(), (Variant?) null)];
            }
            return [];
        }
        foreach (var entry in target.GetMethodList()) {
            if (entry["name"].AsStringName() != step.Member) continue;
            var args = entry["args"].AsGodotArray();
            var defaults = Defaults(entry, MissDefaults.ClassOf(target));
            return [.. args.Select((a, i) => {
                var arg = a.AsGodotDictionary();
                return new WireParameter(arg["name"].AsString(), (Variant.Type) arg["type"].AsInt32(), arg["class_name"].AsString(), defaults[i]);
            })];
        }
        return [];
    }

    /// <summary>
    /// What a binding hands its steps, by name: the property for a property source, the signal's
    /// arguments for a signal. Empty while that is not known.
    /// </summary>
    public static List<string> InputNames(Node mediator, MissBinding binding) {
        if (binding.Kind == MissSourceKind.Property) return [binding.Property.ToString().Split(':')[^1]];
        if (mediator == null || MissWiring.SourceNode(mediator, binding) is not { } source) return [];
        var emitter = binding.Kind == MissSourceKind.ResourceSignal ? MissWiring.HeldResource(source, binding) : source;
        if (emitter == null) return [];
        foreach (var entry in emitter.GetSignalList()) {
            if (entry["name"].AsStringName() == binding.Signal) return [.. entry["args"].AsGodotArray().Select(a => a.AsGodotDictionary()["name"].AsString())];
        }
        return [];
    }

    /// <param name="csharp">The C# class the methods belong to, if any: its defaults count too.</param>
    static IEnumerable<WireMember> Methods(Array<Dictionary> list, System.Type csharp = null) {
        foreach (var entry in list) {
            var name = entry["name"].AsString();
            var flags = (MethodFlags) entry["flags"].AsInt64();
            if (name.StartsWith('_') || (flags & MethodFlags.Virtual) != 0) continue;
            var args = entry["args"].AsGodotArray();
            var defaults = Defaults(entry, csharp);
            // A mediator passes one value at most; a method that needs more is not wired here.
            if (defaults.Count(d => d == null) > 1) continue;
            yield return new WireMember(WireMember.Method, name, args.Count, false, ParametersOf(args, defaults));
        }
    }

    /// <summary>
    /// The defaults of a method's parameters, null where there is none: Godot's, and for a C#
    /// method the ones only C# knows (see <see cref="MissDefaults"/>).
    /// </summary>
    static List<Variant?> Defaults(Dictionary entry, System.Type csharp) {
        var args = entry["args"].AsGodotArray();
        var godot = entry["default_args"].AsGodotArray();
        var first = args.Count - godot.Count;
        var defaults = Enumerable.Range(0, args.Count).Select(i => i >= first ? godot[i - first] : (Variant?) null).ToList();
        if (godot.Count > 0 || csharp == null) return defaults;
        var known = MissDefaults.DefaultsOf(MissDefaults.MethodOf(csharp, entry["name"].AsString(), args.Count));
        return known.Count == args.Count ? known : defaults;
    }
}
#endif
