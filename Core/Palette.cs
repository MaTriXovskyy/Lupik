using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Lupik.Core;

/// <summary>
/// Lupik's neutral colors (backgrounds, lines, text) for the dark and the light theme. The app is drawn with the dark
/// ("Obsidian") values; XAML refers to each one as {DynamicResource cRRGGBB} (a brush) or cRRGGBBColor (a Color),
/// keyed by its dark value, and the light theme remaps them: dark surfaces become light ones, light text becomes dark
/// text, bright colored text gets dark enough to read on white. A color added to the XAML must be listed in
/// <see cref="Keys"/>, or it won't show.
/// </summary>
public static class Palette
{
    /// <summary>Every neutral used by the XAML, as its dark-theme value (AARRGGBB when it has alpha).</summary>
    private static readonly string[] Keys =
    {
        "0E0D0C", "121110", "131211", "161412", "171513", "181614", "1B1917", "1E1B18", "1E2F45", "1F1C19", "1F1C1A",
        "1F3A2B", "211E1B", "221F1C", "24211E", "27231F", "2A2621", "2A2622", "2C2721", "2E2A25", "2E2A26", "2F2B27",
        "2F4A6E", "2F5E43", "302B26", "352F27", "35302A", "3A1F1C", "3A1F1D", "3B3631", "3F3A33", "4A433B", "51493F",
        "5A534B", "665C50", "6B6259", "6E3430", "6F665B", "7F849C", "7FD58F", "8A8074", "8CB8F0", "A6E3A1", "B3131211",
        "B5AB9D", "BAC2DE", "CC131211", "D9433A", "DDD6CB", "E5534B", "E61B1917", "E9E2D6", "ECE6DC", "F06A62", "F07A72",
        "F21B1917", "F38BA8", "F6F1E9", "FBF8F2", "FFE9E7",
    };

    /// <summary>Theme.xaml's named brushes and the dark value each one stands for.</summary>
    private static readonly (string Name, string Hex)[] Named =
    {
        ("Base", "131211"), ("Raised", "1B1917"), ("Surface", "24211E"), ("Line", "2F2B27"), ("LineStrong", "3B3631"),
        ("TextPrimary", "FBF8F2"), ("TextSecondary", "B5AB9D"), ("TextMuted", "8A8074"), ("Danger", "E5534B"), ("DangerHover", "F06A62"),
    };

    public static bool IsLight { get; private set; }

    /// <summary>Bumped on every theme change: caches of brushes made in code compare it.</summary>
    public static int Version { get; private set; }

    private static readonly Dictionary<(uint, byte), Brush> _brushes = new();

    internal static void SetLight(bool light)
    {
        if (light == IsLight && Version > 0) return;
        IsLight = light;
        Version++;
        _brushes.Clear();
    }

    /// <summary>Adds every neutral (in the current theme) to <paramref name="res"/>.</summary>
    internal static void Fill(ResourceDictionary res)
    {
        foreach (string key in Keys)
        {
            var c = Map(Parse(key));
            res["c" + key] = Frozen(c);
            res["c" + key + "Color"] = c;
        }
        foreach (var (name, hex) in Named) res[name] = Frozen(Map(Parse(hex)));
    }

    /// <summary>A dark-theme color (0xRRGGBB) as it looks in the current theme — for brushes made in code.</summary>
    public static Color Color(uint rgb, byte alpha = 0xFF) =>
        Map(System.Windows.Media.Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    public static Brush Brush(uint rgb, byte alpha = 0xFF)
    {
        if (!_brushes.TryGetValue((rgb, alpha), out var brush))
            _brushes[(rgb, alpha)] = brush = Frozen(Color(rgb, alpha));
        return brush;
    }

    public static Color Map(Color c) => IsLight ? Light(c) : c;

    /// <summary>The light-theme counterpart of a dark-theme color (alpha kept).</summary>
    public static Color Light(Color c)
    {
        var (h, s, l) = Accent.ToHsl(c);
        // Chroma, not HSL saturation: near-white text like #FBF8F2 has a high saturation but almost no color
        double chroma = (Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B))) / 255.0;
        double nl, ns;
        if (l < 0.33)
        {
            // Surfaces and lines: the darkest background becomes the lightest one
            nl = Math.Clamp(0.985 - (l - 0.06) * 0.62, 0.55, 0.99);
            ns = s * 0.7;
        }
        else if (chroma >= 0.15)
        {
            // Bright colored text / icons (green "copied", red errors, blue links): dark enough for white
            nl = Math.Min(l, 0.42);
            ns = s;
        }
        else
        {
            // Neutral text: light text becomes dark text, muted stays muted
            nl = Math.Clamp(0.93 - l * 0.9, 0.05, 0.62);
            ns = s * 0.8;
        }
        var m = Accent.FromHsl(h, ns, nl);
        return System.Windows.Media.Color.FromArgb(c.A, m.R, m.G, m.B);
    }

    private static Color Parse(string hex)
    {
        uint v = Convert.ToUInt32(hex, 16);
        return hex.Length == 8
            ? System.Windows.Media.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : System.Windows.Media.Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
