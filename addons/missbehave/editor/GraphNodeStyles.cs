#if TOOLS
using System.Collections.Generic;
using Godot;
using Misscore.Editor;
using Misscore;

namespace Missbehave.Editor;

/// <summary>
/// How a box in the graph looks. The box's own colour is reserved for the live tick status, so it
/// stays meaningful while debugging; the node's category is told apart by the box's shape —
/// composites slightly rounded, decorators chamfered, actions square, conditions round — and by the
/// colour of its icon.
/// <para>
/// The title bar is not used at all: the name sits in the body row beside the ports, which keeps
/// every box a single line instead of a header over an empty strip.
/// </para>
/// </summary>
public static class GraphNodeStyles {
    public static readonly Color Success = new("#3fb950");
    public static readonly Color Failure = new("#f85149");
    public static readonly Color Running = new("#e3b341");

    /// <summary>Opacity of a box that the running tree did not reach this tick.</summary>
    public const float DimmedAlpha = 0.35f;

    static readonly Color Edge = new("#3b3f46");
    static readonly Color SelectedEdge = new("#8ab4f8");

    /// <summary>
    /// Constant, so a status appearing or clearing never changes the box's size — at up to 30 frames
    /// a second that would make the whole graph twitch.
    /// </summary>
    const int BorderWidth = 2;

    static Shader _iconShader;
    static readonly Dictionary<string, ShaderMaterial> IconMaterials = [];

    /// <summary>Icon colour per category. Kept clear of the green, red and amber of the tick status.</summary>
    public static Color IconColor(string group) => group switch {
        NodeTypeRegistry.GroupComposite => new Color("#5b9ee6"),
        NodeTypeRegistry.GroupDecorator => new Color("#e58fb4"),
        NodeTypeRegistry.GroupAction => new Color("#5dcaa5"),
        NodeTypeRegistry.GroupCondition => new Color("#b3adf0"),        NodeTypeRegistry.GroupOther => new Color("#b4b2a9"),
        _ => new Color("#d0d3d8"),
    };

    /// <summary>
    /// Draws an icon in its category's colour, keeping only the shape from the file. The files carry
    /// colours of their own, which do not always match the category — the blackboard icon serves
    /// actions and conditions alike — and modulate can only darken, so recolouring takes a shader.
    /// </summary>
    /// <param name="group">The node's category, or null for the tree's root entry.</param>
    public static ShaderMaterial IconMaterialFor(string group) {
        var key = group ?? "";
        if (IconMaterials.TryGetValue(key, out var cached)) return cached;

        _iconShader ??= new Shader {
            Code = "shader_type canvas_item;\n" +
                   "uniform vec4 tint : source_color = vec4(1.0);\n" +
                   "void fragment() { COLOR = vec4(tint.rgb, texture(TEXTURE, UV).a * tint.a); }\n",
        };

        var material = new ShaderMaterial { Shader = _iconShader };
        material.SetShaderParameter("tint", IconColor(group));
        IconMaterials[key] = material;
        return material;
    }

    public static Color ColorFor(MissStatus status) => status switch {
        MissStatus.Success => Success,
        MissStatus.Failure => Failure,
        _ => Running,
    };

    /// <param name="group">The node's category, or null for the tree's root entry.</param>
    /// <param name="status">Live status to tint the box with, or null when there is none.</param>
    public static void Apply(GraphNode box, string group, MissStatus? status) {
        var (radius, detail) = ShapeOf(group);

        box.AddThemeStyleboxOverride("panel", Body(box, "panel", radius, detail, status, selected: false));
        box.AddThemeStyleboxOverride("panel_selected", Body(box, "panel_selected", radius, detail, status, selected: true));

        var noTitlebar = new StyleBoxEmpty();
        box.AddThemeStyleboxOverride("titlebar", noTitlebar);
        box.AddThemeStyleboxOverride("titlebar_selected", noTitlebar);
    }

    /// <summary>
    /// One entry inside a list box: a faint strip normally, tinted with its live status like a box,
    /// outlined while it is the entry being inspected.
    /// </summary>
    public static StyleBoxFlat EntryRow(MissStatus? status, bool picked) {
        var style = new StyleBoxFlat {
            BgColor = new Color(1, 1, 1, 0.04f),
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 1,
            ContentMarginBottom = 1,
        };
        style.SetCornerRadiusAll(4);
        // Constant width for the same reason as the box border: a status must not resize the row.
        style.SetBorderWidthAll(1);
        style.BorderColor = new Color(1, 1, 1, 0f);

        if (status is { } live) {
            var color = ColorFor(live);
            style.BgColor = new Color(color, 0.25f);
            style.BorderColor = color;
        }
        if (picked) style.BorderColor = SelectedEdge;
        return style;
    }

    /// <summary>Corner radius and detail; a detail of 1 turns rounded corners into chamfers.</summary>
    public static (int Radius, int Detail) ShapeOf(string group) => group switch {
        NodeTypeRegistry.GroupCondition => (14, 8),
        NodeTypeRegistry.GroupAction => (0, 1),
        NodeTypeRegistry.GroupDecorator => (8, 1),
        _ => (5, 4),
    };

    static StyleBoxFlat Body(GraphNode box, string name, int radius, int detail, MissStatus? status, bool selected) {
        box.RemoveThemeStyleboxOverride(name);
        var style = box.GetThemeStylebox(name) is StyleBoxFlat flat
            ? (StyleBoxFlat) flat.Duplicate()
            : new StyleBoxFlat { BgColor = new Color("#2a2d32") };

        var themedSelection = style.BorderWidthLeft > 0 ? style.BorderColor : SelectedEdge;
        var fill = style.BgColor;
        if (selected) fill = fill.Lightened(0.08f);

        style.CornerDetail = detail;
        style.SetCornerRadiusAll(radius);
        style.SetBorderWidthAll(BorderWidth);

        if (status is { } live) {
            var color = ColorFor(live);
            style.BgColor = fill.Lerp(color, 0.3f);
            style.BorderColor = color;
        }
        else {
            style.BgColor = fill;
            style.BorderColor = selected ? themedSelection : Edge;
        }

        return style;
    }
}
#endif
