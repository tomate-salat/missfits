#if TOOLS
using System.Collections.Generic;
using Godot;

namespace Misscore.Editor;

/// <summary>
/// Fonts for what sits in a GraphEdit. Zooming a graph scales its boxes as they are, glyphs
/// included: an ordinary font is rasterised once at its size, so zoomed in it is a blown-up bitmap
/// and looks washed out. A font drawn from a distance field stays sharp at any scale.
/// </summary>
public static class ZoomFonts {
    /// <summary>The sharp twin of each font asked for, by the original's instance id.</summary>
    static readonly Dictionary<ulong, Font> Twins = [];

    /// <summary>
    /// A copy of <paramref name="font"/> that is drawn from a distance field, fallbacks included.
    /// The original is left alone — the rest of the editor keeps using it.
    /// </summary>
    public static Font Sharp(Font font) {
        if (font == null) return null;
        if (Twins.TryGetValue(font.GetInstanceId(), out var known) && GodotObject.IsInstanceValid(known)) return known;

        var twin = (Font) font.Duplicate();
        switch (twin) {
            case FontFile file:
                file.MultichannelSignedDistanceField = true;
                break;
            case SystemFont system:
                system.MultichannelSignedDistanceField = true;
                break;
            case FontVariation variation:
                variation.BaseFont = Sharp(((FontVariation) font).BaseFont);
                break;
        }

        var fallbacks = new Godot.Collections.Array<Font>();
        foreach (var fallback in font.Fallbacks) fallbacks.Add(Sharp(fallback));
        twin.Fallbacks = fallbacks;

        Twins[font.GetInstanceId()] = twin;
        return twin;
    }

    /// <summary>
    /// A theme that makes everything below a control use the sharp twin of the font it would use
    /// anyway. Explicit font overrides on a control still win and need <see cref="Sharp"/> themselves.
    /// </summary>
    public static Theme ThemeFor(Control control) => new() { DefaultFont = Sharp(control.GetThemeDefaultFont()) };
}
#endif
