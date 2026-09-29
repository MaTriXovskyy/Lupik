using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace QuickPeek.Views;

/// <summary>
/// Recolors AvalonEdit's built-in (light-theme) highlighting definitions to a Catppuccin Mocha palette.
/// Colors are matched by name, so every bundled language gets a consistent look.
/// </summary>
public static class CodeTheme
{
    public static readonly Color Text = C("#E9E2D6");
    public static readonly Color Subtext = C("#A6ADC8");
    public static readonly Color Overlay = C("#665C50");
    public static readonly Color Surface = C("#2E2A25");
    public static readonly Color Base = C("#161412");
    public static readonly Color Mantle = C("#121110");

    private static readonly Color Mauve = C("#E3B341");
    private static readonly Color Blue = C("#89B4FA");
    private static readonly Color Sapphire = C("#74C7EC");
    private static readonly Color Teal = C("#94E2D5");
    private static readonly Color Green = C("#A6E3A1");
    private static readonly Color Yellow = C("#F9E2AF");
    private static readonly Color Peach = C("#FAB387");
    private static readonly Color Red = C("#F38BA8");
    private static readonly Color Pink = C("#F5C2E7");

    private static readonly HashSet<IHighlightingDefinition> Themed = new();

    public static void Apply(IHighlightingDefinition? definition)
    {
        if (definition == null || !Themed.Add(definition)) return;

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
