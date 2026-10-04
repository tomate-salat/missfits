using System.Collections.Generic;
using Godot;

namespace Misscore;

/// <summary>
/// Scratch memory for one running instance — of a behavior tree, say. Deliberately a plain C# class
/// rather than a Resource or a Node: it has no lifecycle of its own.
/// <para>
/// Values the source declares (<see cref="IBlackboardSource.Blackboard"/>) are stored under their
/// entry's id, which is what parameters use through <see cref="BbParam{T}"/>. Code outside usually
/// knows a value by name instead, so every by-name call first looks for a declared entry of that name
/// and only otherwise keeps an ad-hoc value under the name itself.
/// </para>
/// </summary>
public sealed class Blackboard {
    /// <summary>Declared values under their entry id, ad-hoc values under their name.</summary>
    readonly Dictionary<string, Variant> _values = [];
    readonly Dictionary<string, string> _idByName = [];
    readonly Dictionary<string, string> _nameById = [];

    /// <summary>
    /// Registers the source's entries and sets each to its default. Array and dictionary defaults are
    /// copied, so instances sharing a source never write into one shared collection.
    /// </summary>
    public void Declare(IEnumerable<BlackboardEntry> entries) {
        if (entries == null) return;
        foreach (var entry in entries) {
            if (entry == null || string.IsNullOrEmpty(entry.Id)) continue;

            _nameById[entry.Id] = entry.Name;
            if (!string.IsNullOrEmpty(entry.Name)) _idByName.TryAdd(entry.Name, entry.Id);
            _values[entry.Id] = Copy(entry.Default);
        }
    }

    static Variant Copy(Variant value) => value.VariantType switch {
        Variant.Type.Array => value.AsGodotArray().Duplicate(true),
        Variant.Type.Dictionary => value.AsGodotDictionary().Duplicate(true),
        _ => value,
    };

    /// <summary>The storage key for a name: the id of the declared entry, or the name itself.</summary>
    string KeyOf(string name) {
        name ??= "";
        return _idByName.GetValueOrDefault(name, name);
    }

    /// <summary>Whether <paramref name="name"/> belongs to a declared entry.</summary>
    public bool IsDeclared(string name) => _idByName.ContainsKey(name ?? "");

    // ---- by name -----------------------------------------------------------------------------
    // Plain strings rather than StringName: turning a string into a StringName is a call into the
    // engine, and it made every by-name access several times slower than the lookup itself.

    public void Set<[MustBeVariant] T>(string name, T value) => _values[KeyOf(name)] = Variant.From(value);

    public void SetVariant(string name, Variant value) => _values[KeyOf(name)] = value;

    public T Get<[MustBeVariant] T>(string name, T fallback = default)
        => _values.TryGetValue(KeyOf(name), out var value) && BbTypes.TryConvert<T>(value, out var typed) ? typed : fallback;

    public Variant GetVariant(string name) => _values.GetValueOrDefault(KeyOf(name));

    public bool TryGetVariant(string name, out Variant value) => _values.TryGetValue(KeyOf(name), out value);

    /// <summary>True when a value is present, whatever it is. A stored null still counts.</summary>
    public bool Has(string name) => _values.ContainsKey(KeyOf(name));

    /// <summary>Removes the value outright. A declared entry stays declared and can be set again.</summary>
    public bool Erase(string name) => _values.Remove(KeyOf(name));

    // ---- by entry id -------------------------------------------------------------------------

    public void SetById(string entryId, Variant value) => _values[entryId] = value;

    public bool TryGetById(string entryId, out Variant value) => _values.TryGetValue(entryId, out value);

    public bool HasId(string entryId) => _values.ContainsKey(entryId);

    public bool EraseById(string entryId) => _values.Remove(entryId);

    public void Clear() => _values.Clear();

    // ---- debugging ---------------------------------------------------------------------------

    /// <summary>
    /// A copy by name, safe to send across the debugger channel. Raw Objects cannot be marshalled
    /// there, so they are flattened to a readable string and recursion is depth-limited.
    /// </summary>
    public Godot.Collections.Dictionary GetDebugData(int maxDepth = 2) {
        var result = new Godot.Collections.Dictionary();
        foreach (var (key, value) in _values) {
            result[_nameById.GetValueOrDefault(key, key)] = Sanitize(value, maxDepth);
        }
        return result;
    }

    static Variant Sanitize(Variant value, int depth) {
        switch (value.VariantType) {
            case Variant.Type.Object:
                var obj = value.AsGodotObject();
                if (obj == null) return "<null>";
                if (!GodotObject.IsInstanceValid(obj)) return "<freed>";
                return obj is Node node ? $"{obj.GetClass()}:{node.Name}" : $"<{obj.GetClass()}>";

            case Variant.Type.Array: {
                if (depth <= 0) return "[...]";
                var source = value.AsGodotArray();
                var copy = new Godot.Collections.Array();
                foreach (var item in source) copy.Add(Sanitize(item, depth - 1));
                return copy;
            }

            case Variant.Type.Dictionary: {
                if (depth <= 0) return "{...}";
                var source = value.AsGodotDictionary();
                var copy = new Godot.Collections.Dictionary();
                foreach (var key in source.Keys) copy[Sanitize(key, depth - 1)] = Sanitize(source[key], depth - 1);
                return copy;
            }

            default:
                return value;
        }
    }
}
