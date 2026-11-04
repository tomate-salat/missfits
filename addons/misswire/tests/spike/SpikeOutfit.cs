using Godot;
using Godot.Collections;

namespace Misswire.Tests;

/// <summary>
/// Like the outfit in Gladiator: a <c>[Tool]</c> resource with one ordinary export and one property
/// made up in <see cref="_GetPropertyList"/>. Neither calls <see cref="Resource.EmitChanged"/>, so
/// the spike sees whether anything else does.
/// </summary>
[Tool]
public partial class SpikeOutfit : Resource {
    string _hair = "h1";

    [Export] public string Body { get; set; } = "b1";

    public override Variant _Get(StringName property) => property == "Hair" ? _hair : default;

    public override bool _Set(StringName property, Variant value) {
        if (property != "Hair") return false;
        _hair = value.AsString();
        return true;
    }

    public override Array<Dictionary> _GetPropertyList() => [
        new Dictionary {
            { "name", "Hair" },
            { "type", (int) Variant.Type.String },
            { "usage", (int) PropertyUsageFlags.Default },
        },
    ];
}
