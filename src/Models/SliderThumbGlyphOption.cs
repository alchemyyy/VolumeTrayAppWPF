using System.Xml.Serialization;
using VolumeTrayAppWPF.Visuals;

namespace VolumeTrayAppWPF.Models;

/// <summary>
/// A selectable volume-slider thumb glyph, stored with its own display properties
/// (font family, font size, width, height, horizontal scale) so that differently-proportioned glyphs
/// render correctly both in the dropdown preview and on the slider itself.
/// Defaults target Segoe Fluent Icons at 18px.
/// </summary>
public class SliderThumbGlyphOption
{
    [XmlAttribute] public string Name { get; set; } = "Circle";
    [XmlAttribute] public string Glyph { get; set; } = GlyphCatalog.CIRCLE;
    [XmlAttribute] public string FontFamily { get; set; } = "Segoe Fluent Icons";
    [XmlAttribute] public double FontSize { get; set; } = 18;
    [XmlAttribute] public double Width { get; set; } = 18;
    [XmlAttribute] public double Height { get; set; } = 18;

    // Horizontal layout-scale applied to the rendered glyph.
    // Lets a single glyph (e.g. Square) be repurposed as a narrower variant
    // without authoring a new font character.
    [XmlAttribute] public double XScale { get; set; } = 1.0;

    // Glyph (default) draws a TextBlock from the Glyph string.
    // Capsule draws a rounded-rectangle Border using Width/Height with a fully rounded corner radius,
    // matching the OS toggle-switch pill aesthetic that can't be reproduced cleanly with a font character.
    [XmlAttribute] public SliderThumbShape Shape { get; set; } = SliderThumbShape.Glyph;

    [XmlIgnore] public bool IsGlyph => Shape == SliderThumbShape.Glyph;
    [XmlIgnore] public bool IsCapsule => Shape == SliderThumbShape.Capsule;

    public static List<SliderThumbGlyphOption> CreateDefaults() =>
    [
        new() { Name = "Capsule",  Shape = SliderThumbShape.Capsule, Width = 10, Height = 22 },
        new() { Name = "Circle",   Glyph = GlyphCatalog.CIRCLE,  FontSize = 18 },
        new() { Name = "Diamond",  Glyph = GlyphCatalog.DIAMOND, FontSize = 16 },
        new() { Name = "Star",     Glyph = GlyphCatalog.STAR,    FontSize = 18 },
        new() { Name = "Square",   Glyph = GlyphCatalog.SQUARE,  FontSize = 16 },
        new() { Name = "Heart",    Glyph = GlyphCatalog.HEART,   FontSize = 16 },
    ];
}
