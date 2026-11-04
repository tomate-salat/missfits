#if TOOLS
using System.Collections.Generic;
using Godot;

namespace Misswire.Editor;

/// <summary>
/// A node of the scene, or the resource one of its properties holds, with the members it offers.
/// A node may have several of these boxes; all but its main box carry an <see cref="InfoId"/>.
/// </summary>
[Tool]
public partial class WireNodeBox : WireBox {
    /// <summary>The node, relative to the mediator.</summary>
    public string Path { get; private set; } = "";

    /// <summary>Empty for a node; for a resource box, the property path leading to the resource.</summary>
    public string Property { get; private set; } = "";

    /// <summary>Empty for the main box of its node or resource, else the id of this further box.</summary>
    public string InfoId { get; private set; } = "";

    public void Build(string path, string property, string infoId, string title, string subtitle, string warning,
                      IEnumerable<(WireMember Member, string Warning)> rows) {
        Path = path;
        Property = property;
        InfoId = infoId;
        BuildFrame(title, subtitle, warning, PropertyColor);
        SetSlot(0, false, 0, Colors.White, false, 0, Colors.White);
        foreach (var (member, rowWarning) in rows) {
            var color = member.Kind switch {
                WireMember.Signal => SignalColor,
                WireMember.Method => MethodColor,
                _ => PropertyColor,
            };
            AddRow(member.Kind, member.Name, member.Label, rowWarning, member.IsInput, color, member.IsOutput, color, member.RichLabel);
        }
    }
}
#endif
