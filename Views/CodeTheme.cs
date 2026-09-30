using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace Lupik.Views;

/// <summary>
/// Recolors AvalonEdit's built-in (light-theme) highlighting definitions to a Catppuccin palette: Mocha on the dark
/// theme, Latte on the light one. Colors are matched by name, so every bundled language gets a consistent look.
/// </summary>
public static class CodeTheme
{
    private static bool Light => Core.Palette.IsLight;

    public static Color Text => Light ? C("#3A342D") : C("#E9E2D6");
    public static Color Subtext => Light ? C("#6C6F85") : C("#A6ADC8");
    public static Color Overlay => Light ? C("#9A9086") : C("#665C50");

    private static Color Mauve => Core.Accent.Color; // keywords in the accent color
    private static Color Blue => Light ? C("#1E66F5") : C("#89B4FA");
    private static Color Sapphire => Light ? C("#1B8AA8") : C("#74C7EC");
    private static Color Teal => Light ? C("#148A86") : C("#94E2D5");
    private static Color Green => Light ? C("#3C8F2A") : C("#A6E3A1");
    private static Color Yellow => Light ? C("#B8740F") : C("#F9E2AF");
    private static Color Peach => Light ? C("#D9570A") : C("#FAB387");
    private static Color Red => Light ? C("#D20F39") : C("#F38BA8");
    private static Color Pink => Light ? C("#C4459F") : C("#F5C2E7");

    // Definition -> the accent and theme it was colored with (recolored after either changes)
    private static readonly Dictionary<IHighlightingDefinition, (Color, bool)> Themed = new();

    public static void Apply(IHighlightingDefinition? definition)
    {
        if (definition == null || (Themed.TryGetValue(definition, out var done) && done == (Mauve, Light))) return;
        Themed[definition] = (Mauve, Light);

        foreach (var color in definition.NamedHighlightingColors)
        {
            var (fg, weight, style) = Map(color.Name ?? "");
            color.Foreground = new SimpleHighlightingBrush(fg);
            color.Background = null;
            color.FontSize = null; // Markdown headings use oversized fonts by default
            color.FontFamily = null; // Markdown inline code switches to a proportional font
            color.FontWeight = weight;
            color.FontStyle = style;
            color.Underline = null;
        }
    }

    private static (Color, FontWeight?, FontStyle?) Map(string name)
    {
        string n = name.ToLowerInvariant();

        if (n.Contains("comment") || n == "doctype") return (Overlay, null, FontStyles.Italic);
        if (n.StartsWith("heading1")) return (Red, FontWeights.Bold, null);
        if (n.StartsWith("heading2")) return (Peach, FontWeights.Bold, null);
        if (n.StartsWith("heading3")) return (Yellow, FontWeights.Bold, null);
        if (n.StartsWith("heading")) return (Green, FontWeights.Bold, null);
        if (n == "strongemphasis") return (Peach, FontWeights.Bold, null);
        if (n == "emphasis") return (Pink, null, FontStyles.Italic);
        if (n == "blockquote") return (Subtext, null, FontStyles.Italic);
        if (n is "link" or "image") return (Sapphire, null, null);
        if (n == "code" || n == "cdata") return (Green, null, null);
        if (n.Contains("attributevalue")) return (Green, null, null);
        if (n.Contains("attributename")) return (Yellow, null, null);
        if (n.Contains("tag") || n.Contains("declaration")) return (Blue, null, null);
        if (n.Contains("entity")) return (Peach, null, null);
        if (n.Contains("interpolation")) return (Pink, null, null);
        if (n.Contains("string") || n.Contains("char") || n.Contains("regex")) return (Green, null, null);
        if (n.Contains("number") || n.Contains("digit") || n.Contains("truefalse") ||
            n.Contains("nullorvalue") || n.Contains("literal")) return (Peach, null, null);
        if (n.Contains("preprocessor") || n.Contains("directive")) return (Pink, null, null);
        if (n.Contains("method") || n.Contains("function") || n.Contains("intrinsic")) return (Blue, null, null);
        if (n.Contains("valuetype") || n.Contains("referencetype") || n == "typekeywords") return (Yellow, null, null);
        if (n.Contains("punctuation") || n.Contains("operator")) return (Sapphire, null, null);
        if (n.Contains("keyword") || n.Contains("modifier") || n.Contains("visibility") ||
            n.Contains("thisorbase") || n.Contains("getset")) return (Mauve, null, null);

        return (Teal, null, null);
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
