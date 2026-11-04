using Godot;
using Godot.Collections;

namespace Misswire;

/// <summary>
/// One signal connection of a binding. It hands the signal on as <c>method(binding, arguments)</c>,
/// whatever the signal's arguments are, so the receiver knows which binding fired.
/// <para>
/// C# has no <c>Callable.Bind</c>, and a method a signal calls by name must take exactly the
/// signal's arguments. So the relay has a handler per argument count and is connected by name, which
/// also keeps the connection intact through an assembly reload in the editor. Every field is a
/// Godot type for the same reason.
/// </para>
/// </summary>
[Tool]
public partial class MissSignalRelay : RefCounted {
    public const int MaxArguments = 6;

    // Kept by instance id: a reload serializes every field, and a reference to a node that is gone by
    // then (a scene reloaded or closed) throws.
    ulong _emitter;
    StringName _signal;
    ulong _receiver;
    StringName _method;
    StringName _handler;
    ulong _context;
    int _binding;

    public int Binding => _binding;
    public StringName Method => _method;
    public GodotObject Emitter => Find(_emitter);
    public StringName Signal => _signal;
    public GodotObject Context => Find(_context);

    /// <summary>The object of an id, or null once it is gone.</summary>
    static GodotObject Find(ulong id) => id == 0 ? null : InstanceFromId(id);

    /// <summary>
    /// Connects <paramref name="signal"/> of <paramref name="emitter"/> to
    /// <c>receiver.method(binding, arguments)</c>, or with a <paramref name="context"/> to
    /// <c>receiver.method(context, binding, arguments)</c>. Explains in <paramref name="problem"/> why not.
    /// </summary>
    public static MissSignalRelay Connect(GodotObject emitter, StringName signal, GodotObject receiver, StringName method,
                                          int binding, out string problem, GodotObject context = null) {
        var arguments = ArgumentCount(emitter, signal);
        problem = arguments switch {
            < 0 => $"{Describe(emitter)} has no signal '{signal}'",
            > MaxArguments => $"signal '{signal}' has {arguments} arguments, at most {MaxArguments} are supported",
            _ => null,
        };
        if (problem != null) return null;

        var relay = new MissSignalRelay {
            _emitter = emitter.GetInstanceId(), _signal = signal, _receiver = receiver.GetInstanceId(), _method = method,
            _handler = HandlerFor(arguments), _binding = binding, _context = context?.GetInstanceId() ?? 0,
        };
        emitter.Connect(signal, new Callable(relay, relay._handler));
        return relay;
    }

    public void Disconnect() {
        if (Emitter is not { } emitter) return;
        var callable = new Callable(this, _handler);
        if (emitter.IsConnected(_signal, callable)) emitter.Disconnect(_signal, callable);
    }

    /// <returns>The number of arguments, or -1 if the object has no such signal.</returns>
    public static int ArgumentCount(GodotObject emitter, StringName signal) {
        foreach (var entry in emitter.GetSignalList()) {
            if (entry["name"].AsStringName() == signal) return entry["args"].AsGodotArray().Count;
        }
        return -1;
    }

    static StringName HandlerFor(int arguments) => arguments switch {
        0 => MethodName.Relay0,
        1 => MethodName.Relay1,
        2 => MethodName.Relay2,
        3 => MethodName.Relay3,
        4 => MethodName.Relay4,
        5 => MethodName.Relay5,
        _ => MethodName.Relay6,
    };

    public void Relay0() => Forward([]);
    public void Relay1(Variant a) => Forward([a]);
    public void Relay2(Variant a, Variant b) => Forward([a, b]);
    public void Relay3(Variant a, Variant b, Variant c) => Forward([a, b, c]);
    public void Relay4(Variant a, Variant b, Variant c, Variant d) => Forward([a, b, c, d]);
    public void Relay5(Variant a, Variant b, Variant c, Variant d, Variant e) => Forward([a, b, c, d, e]);
    public void Relay6(Variant a, Variant b, Variant c, Variant d, Variant e, Variant f) => Forward([a, b, c, d, e, f]);

    void Forward(Array arguments) {
        if (Find(_receiver) is not { } receiver) return;
        if (_context == 0) receiver.Call(_method, _binding, arguments);
        else if (Context is { } context) receiver.Call(_method, context, _binding, arguments);
    }

    internal static string Describe(GodotObject obj) => obj switch {
        Node node => $"node '{node.Name}'",
        Resource resource when !string.IsNullOrEmpty(resource.ResourcePath) => $"resource '{resource.ResourcePath}'",
        _ => obj.GetClass(),
    };
}
