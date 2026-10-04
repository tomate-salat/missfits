#if TOOLS
using Godot;

namespace Misscore.Editor;

/// <summary>
/// How the blackboard panel looks: entries as cards with an accent in their type's colour, and the
/// type shown as a small chip with the editor's own icon for it.
/// <para>
/// The colours stay clear of the green, red and amber the graph uses for live statuses.
/// </para>
/// </summary>
public static class BlackboardStyles {
    const string EditorIcons = "EditorIcons";

    /// <summary>Colour of an entry's name where a parameter is linked to it.</summary>
    public static readonly Color Linked = new("#b3adf0");

    /// <summary>Accent colour of an entry, grouped by what kind of value it holds.</summary>
    public static Color TypeColor(Variant.Type type) => type switch {
        Variant.Type.Nil => new Color("#a3a8b1"),
        Variant.Type.Bool => new Color("#e58fb4"),
        Variant.Type.Int or Variant.Type.Float => new Color("#5b9ee6"),
        Variant.Type.String or Variant.Type.StringName or Variant.Type.NodePath => new Color("#b3adf0"),
        Variant.Type.Color => new Color("#e0e0e0"),
        Variant.Type.Object => new Color("#5dcaa5"),
        Variant.Type.Array or Variant.Type.Dictionary => new Color("#c9a98a"),
        _ when type >= Variant.Type.PackedByteArray => new Color("#c9a98a"),
        _ => new Color("#7fc4d8"),
    };

    /// <summary>The card an entry sits on.</summary>
    public static StyleBoxFlat Card(Variant.Type type) {
        var style = new StyleBoxFlat {
            BgColor = new Color(1, 1, 1, 0.045f),
            BorderColor = TypeColor(type),
            BorderWidthLeft = 3,
            ContentMarginLeft = 10,
            ContentMarginRight = 4,
            ContentMarginTop = 4,
            ContentMarginBottom = 6,
        };
        style.SetCornerRadiusAll(5);
        return style;
    }

    /// <summary>A card being hovered, a shade lighter so the row under the mouse is obvious.</summary>
    public static StyleBoxFlat HoveredCard(Variant.Type type) {
        var style = Card(type);
        style.BgColor = new Color(1, 1, 1, 0.085f);
        return style;
    }

    /// <summary>The chip behind an entry's type.</summary>
    public static StyleBoxFlat Chip(Variant.Type type, bool hovered) {
        var color = TypeColor(type);
        var style = new StyleBoxFlat {
            BgColor = new Color(color, hovered ? 0.28f : 0.16f),
            ContentMarginLeft = 5,
            ContentMarginRight = 7,
            ContentMarginTop = 1,
            ContentMarginBottom = 1,
        };
        style.SetCornerRadiusAll(9);
        return style;
    }

    /// <summary>
    /// The editor's icon for a type — the one the Inspector shows — or null outside the editor. Script
    /// classes use their registered icon, or the nearest engine base class's.
    /// </summary>
    public static Texture2D TypeIcon(Variant.Type type, string className) {
        if (!Engine.IsEditorHint()) return null;
        var theme = EditorInterface.Singleton?.GetEditorTheme();
        if (theme == null) return null;

        if (type != Variant.Type.Object) {
            // The editor names these icons like GDScript names the types.
            var name = type switch {
                Variant.Type.Nil => "Variant",
                Variant.Type.Bool => "bool",
                Variant.Type.Int => "int",
                Variant.Type.Float => "float",
                Variant.Type.Aabb => "AABB",
                Variant.Type.Rid => "RID",
                _ => type.ToString(),
            };
            return theme.HasIcon(name, EditorIcons) ? theme.GetIcon(name, EditorIcons) : null;
        }

        foreach (var entry in ProjectSettings.GetGlobalClassList()) {
            if (entry["class"].AsString() != className) continue;
            var path = entry["icon"].AsString();
            if (!string.IsNullOrEmpty(path) && ResourceLoader.Exists(path)) return ResourceLoader.Load<Texture2D>(path);
        }

        for (var current = BbTypes.ResolveClass(className); current != null; current = current.BaseType) {
            if (theme.HasIcon(current.Name, EditorIcons)) return theme.GetIcon(current.Name, EditorIcons);
        }
        return theme.HasIcon("Object", EditorIcons) ? theme.GetIcon("Object", EditorIcons) : null;
    }

    /// <summary>An editor icon by name, or null outside the editor.</summary>
    public static Texture2D EditorIcon(string name) {
        if (!Engine.IsEditorHint()) return null;
        var theme = EditorInterface.Singleton?.GetEditorTheme();
        return theme != null && theme.HasIcon(name, EditorIcons) ? theme.GetIcon(name, EditorIcons) : null;
    }

    /// <summary>The editor's bold font, or null outside the editor.</summary>
    public static Font BoldFont() {
        if (!Engine.IsEditorHint()) return null;
        var theme = EditorInterface.Singleton?.GetEditorTheme();
        return theme != null && theme.HasFont("bold", "EditorFonts") ? theme.GetFont("bold", "EditorFonts") : null;
    }
}
#endif
