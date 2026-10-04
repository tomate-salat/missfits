using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;

namespace Misscore;

/// <summary>
/// Marks a <see cref="BbParam{T}"/> that only makes sense linked to a blackboard entry — the editor
/// offers no fixed value for it and warns while it is unlinked. Implied for node types, since a scene
/// node can never be stored in a resource.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class BbEntryOnlyAttribute : Attribute;

/// <summary>One <see cref="BbParam{T}"/> member of a host class.</summary>
public sealed class BbParamMember {
    public string Name { get; init; }
    public Type ValueType { get; init; }
    public bool EntryOnly { get; init; }

    internal Func<object, object> Getter { get; init; }
    internal Action<object, object> Setter { get; init; }
    internal Godot.Collections.Dictionary DefaultStorage { get; set; }

    /// <summary>The parameter on <paramref name="host"/>, created on the spot if the member is still null.</summary>
    public IBbParam On(object host) {
        if (Getter(host) is IBbParam param) return param;
        if (Setter == null) return null;

        param = (IBbParam) Activator.CreateInstance(typeof(BbParam<>).MakeGenericType(ValueType));
        Setter(host, param);
        return param;
    }
}

/// <summary>
/// Finds the <see cref="BbParam{T}"/> members of host classes and converts them to and from what is
/// stored in a resource: a small dictionary holding the fixed value and the entry link.
/// </summary>
public static class BbParams {
    /// <summary>Hint string on a parameter's property, which is how the editor recognises one.</summary>
    public const string HintString = "misscore_param";

    /// <summary>
    /// Suffix of a hidden property that reads and writes just a parameter's fixed value. It is not
    /// listed or saved; the editor points a stock value editor at it.
    /// </summary>
    public const string LiteralSuffix = "__literal";

    const string KeyValue = "value";
    const string KeyEntry = "entry";
    const string KeyName = "name";

    static readonly Dictionary<Type, BbParamMember[]> Members = [];

    public static IReadOnlyList<BbParamMember> Of(Type hostType) {
        if (Members.TryGetValue(hostType, out var cached)) return cached;

        var found = new List<BbParamMember>();
        // Base classes first, so inherited parameters come before the subclass's own ones.
        var chain = new List<Type>();
        for (var type = hostType; type != null && type != typeof(Resource); type = type.BaseType) chain.Insert(0, type);

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var type in chain) {
            foreach (var property in type.GetProperties(flags)) {
                if (property.GetMethod == null || property.GetIndexParameters().Length > 0) continue;
                if (ValueTypeOf(property.PropertyType) is not { } valueType) continue;

                // A get-only auto-property can still be filled in through its backing field.
                Action<object, object> set = property.SetMethod != null ? property.SetValue
                    : type.GetField($"<{property.Name}>k__BackingField", flags) is { } backing ? backing.SetValue
                    : null;
                found.Add(Member(property.Name, valueType, property, property.GetValue, set));
            }
            foreach (var field in type.GetFields(flags)) {
                // Auto-property backing fields were already covered through their property.
                if (field.Name.StartsWith('<') || ValueTypeOf(field.FieldType) is not { } valueType) continue;
                found.Add(Member(field.Name, valueType, field, field.GetValue, field.SetValue));
            }
        }

        var members = found.ToArray();
        Members[hostType] = members;
        return members;
    }

    public static BbParamMember Find(Type hostType, string name) => Of(hostType).FirstOrDefault(m => m.Name == name);

    /// <summary>Every <see cref="BbParam{T}"/> member of <paramref name="host"/>, with its current parameter.</summary>
    public static IEnumerable<(BbParamMember Member, IBbParam Param)> On(object host) {
        if (host == null) yield break;
        foreach (var member in Of(host.GetType())) {
            if (member.On(host) is { } param) yield return (member, param);
        }
    }

    static Type ValueTypeOf(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(BbParam<>) ? type.GetGenericArguments()[0] : null;

    static BbParamMember Member(string name, Type valueType, MemberInfo info, Func<object, object> get, Action<object, object> set) {
        var entryOnly = info.GetCustomAttribute<BbEntryOnlyAttribute>() != null
                        || typeof(Node).IsAssignableFrom(valueType);
        return new BbParamMember { Name = name, ValueType = valueType, EntryOnly = entryOnly, Getter = get, Setter = set };
    }

    public static Godot.Collections.Dictionary ToStorage(IBbParam param) {
        var data = new Godot.Collections.Dictionary { [KeyValue] = param.Literal };
        if (param.IsLinked) {
            data[KeyEntry] = param.EntryId;
            data[KeyName] = param.EntryName;
        }
        return data;
    }

    /// <summary>
    /// Also accepts a bare value, which is what a resource saved before the member became a parameter
    /// holds — a plain <c>float RequiredDistance</c> turned into <c>BbParam&lt;float&gt;</c> keeps its value.
    /// </summary>
    public static void FromStorage(IBbParam param, Variant stored) {
        if (stored.VariantType == Variant.Type.Dictionary) {
            var data = stored.AsGodotDictionary();
            if (data.ContainsKey(KeyValue) || data.ContainsKey(KeyEntry)) {
                if (data.TryGetValue(KeyValue, out var literal)) param.Literal = literal;
                param.EntryId = data.TryGetValue(KeyEntry, out var id) ? id.AsString() : "";
                param.EntryName = data.TryGetValue(KeyName, out var name) ? name.AsString() : "";
                return;
            }
        }

        param.Literal = stored;
        param.EntryId = "";
        param.EntryName = "";
    }

    /// <summary>What the member holds on a freshly constructed host — the revert value in the Inspector.</summary>
    public static Godot.Collections.Dictionary DefaultStorage(Type hostType, BbParamMember member) {
        if (member.DefaultStorage != null) return member.DefaultStorage;
        if (hostType.IsAbstract || hostType.GetConstructor(Type.EmptyTypes) == null) return null;

        var pristine = Activator.CreateInstance(hostType);
        foreach (var each in Of(hostType)) {
            var param = each.On(pristine);
            each.DefaultStorage = param == null ? null : ToStorage(param);
        }
        return member.DefaultStorage;
    }
}
