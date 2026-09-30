using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Lupik.Core;

/// <summary>
/// Lupik's look: the theme (dark, light or as Windows) and the accent ("the gold"). Every gold shade in the app is
/// derived from one color picked in Settings (or Windows' accent color): lighter/darker tones keep the user's hue and
/// shift like the originals did, tinted backgrounds follow the theme, and the text drawn on the accent turns light
/// when the accent is dark. With the default color and the dark theme every tone is exactly the original hand-picked
/// one. Brushes live in the application resources, so {DynamicResource Gold} etc. update live.
/// </summary>
public static class Accent
{
    public static readonly Color Default = Color.FromRgb(0xE3, 0xB3, 0x41);

    /// <summary>The accent in use: the chosen color, lifted if it was too dark to read on Lupik's dark surfaces.</summary>
    public static Color Color { get; private set; } = Default;
    /// <summary>The color picked in Settings (what <see cref="Color"/> is made from).</summary>
    public static Color Chosen { get; private set; } = Default;
    public static Color Hover { get; private set; }
    public static Color Soft { get; private set; }
    public static Color OnAccent { get; private set; }

    public static event Action? Changed;

    // The default accent in HSL: every tone below is described relative to it
    private static readonly (double H, double S, double L) Base = ToHsl(Default);

    /// <summary>Parses "#RRGGBB" (or "RRGGBB"); null if it isn't a color.</summary>
    public static Color? Parse(string? text)
    {
        string s = (text ?? "").Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return null;
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Settings value meaning "Windows' accent color".</summary>
    public const string SystemValue = "system";

    public static bool FollowsSystem => Settings.Current.AccentColor == SystemValue;

    /// <summary>The saved theme and accent, applied to the app's resources.</summary>
    public static void ApplySaved()
    {
        WatchSystem();
        Apply(FollowsSystem ? SystemAccent() ?? Default : Parse(Settings.Current.AccentColor) ?? Default);
    }

    /// <summary>Is the light theme on (chosen, or Windows' app mode when "as Windows")?</summary>
    public static bool WantsLight() => Settings.Current.Theme switch
    {
        "light" => true,
        "system" => SystemUsesLight(),
        _ => false,
    };

    public static bool SystemUsesLight()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
    }

    /// <summary>Windows' accent color (Settings › Personalization › Colors), or null if it can't be read.</summary>
    public static Color? SystemAccent()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        }
        catch (Exception ex) { App.Log($"[Accent] Could not read the Windows accent: {ex.Message}"); }
        return null;
    }

    private static bool _watching;

    /// <summary>Windows' light/dark mode or accent changed: follow it when the settings say so.</summary>
    private static void WatchSystem()
    {
        if (_watching) return;
        _watching = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color or Microsoft.Win32.UserPreferenceCategory.VisualStyle)) return;
            if (Settings.Current.Theme != "system" && !FollowsSystem) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var chosen = FollowsSystem ? SystemAccent() ?? Default : Chosen;
                if (chosen != Chosen || WantsLight() != Palette.IsLight) Apply(chosen);
            });
        };
    }

    // The accent's resources: one dictionary, swapped as a whole so a change costs one resource refresh, not twenty
    private static ResourceDictionary? _resources;

    /// <summary>
    /// The accent is also text (zoom %, hints, labels): on the dark theme a very dark pick gets just enough light to
    /// stay readable, on the light theme a very light one gets just enough darkness. Hue and saturation stay.
    /// </summary>
    public static Color Readable(Color c) => Readable(c, Palette.IsLight);

    public static Color Readable(Color c, bool light)
    {
        const double MinOnDark = 0.2, MaxOnLight = 0.28;
        var (h, s, l) = ToHsl(c);
        if (!light) for (; l < 1 && Luminance(c) < MinOnDark; l += 0.01) c = FromHsl(h, s, l);
        else for (; l > 0 && Luminance(c) > MaxOnLight; l -= 0.01) c = FromHsl(h, s, l);
        return c;
    }

    /// <summary>Rebuilds the theme with <paramref name="chosen"/> as the accent (also after a theme change).</summary>
    public static void Apply(Color chosen)
    {
        Chosen = chosen;
        bool light = WantsLight();
        Palette.SetLight(light);
        var accent = Readable(chosen, light);
        var res = new ResourceDictionary();
        Palette.Fill(res);
        var c = ToHsl(accent);

        // A tone of the original palette, moved to the new hue. `follow` = how much it follows the accent's lightness
        // (1 for the accent's own light/dark variants, 0 for dark backgrounds that must stay dark).
        Color Tone(string original, double follow = 1)
        {
            var o = ToHsl((Color)ColorConverter.ConvertFromString(original));
            double s = Base.S > 0 ? o.S * c.S / Base.S : 0;
            return FromHsl(c.H, Math.Clamp(s, 0, 1), Math.Clamp(o.L + (c.L - Base.L) * follow, 0.03, 0.97));
        }

        // Tinted backgrounds and outlines: dark on the dark theme, pale on the light one
        Color Tint(string original, double follow = 0) => Palette.Map(Tone(original, follow));
        // On a light background "hover" means a bit darker, not lighter
        Color Darker(double by) => FromHsl(c.H, c.S, Math.Clamp(c.L - by, 0.05, 0.95));

        Color = accent;
        Hover = light ? Darker(0.07) : Tone("#F0C95E");
        Soft = Tint("#F9E2AF", 0.3);
        bool darkText = Luminance(accent) > 0.3;
        OnAccent = darkText ? Tone("#1A1407", 0) : Color.FromRgb(0xFB, 0xF8, 0xF2);
        Color onAccentHint = darkText ? Tone("#3A2E12", 0) : Color.FromArgb(0xD0, 0xFB, 0xF8, 0xF2);

        res["GoldColor"] = accent;
        Set(res, "Gold", accent);
        Set(res, "GoldHover", Hover);
        Set(res, "GoldPressed", light ? Darker(0.12) : Tone("#C99A2E"));
        Set(res, "GoldSoft", Soft);
        Set(res, "OnGold", OnAccent);
        Set(res, "OnGoldHint", onAccentHint);
        Set(res, "GoldDeep", Tint("#5C4A20", 0.3));        // outline of a gold chip
        Set(res, "GoldTint", Tint("#2E2618"));             // gold-tinted backgrounds
        Set(res, "GoldTintAlt", Tint("#2C2619"));
        Set(res, "GoldTintHover", Tint("#3D3322"));
        Set(res, "GoldTintInfo", Tint("#3A3322"));
        Set(res, "GoldTintInfoLine", Tint("#5E5230", 0.3));
        res["GoldHairlineColor"] = Tone("#A8843A");

        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        gradient.GradientStops.Add(new GradientStop(Tone("#F5D27A"), 0));
        gradient.GradientStops.Add(new GradientStop(accent, 0.55));
        gradient.GradientStops.Add(new GradientStop(Tone("#B8861F"), 1));
        gradient.Freeze();
        res["GoldGradient"] = gradient;

        var glow = new RadialGradientBrush();
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, accent.R, accent.G, accent.B), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1));
        glow.Freeze();
        res["GoldGlow"] = glow;

        res["AppLogo"] = Logo(gradient, darkText ? Tone("#5A3A05", 0) : Color.FromRgb(0, 0, 0), darkText ? Color.FromRgb(0x3A, 0x36, 0x32) : Color.FromRgb(0xFB, 0xF8, 0xF2));

        // Merged last, so it wins over Theme.xaml's defaults
        var merged = Application.Current.Resources.MergedDictionaries;
        int index = _resources == null ? -1 : merged.IndexOf(_resources);
        if (index >= 0) merged[index] = res; else merged.Add(res);
        _resources = res;

        Changed?.Invoke();
    }

    private static void Set(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }

    /// <summary>The app mark (rounded square + eye), as in Theme.xaml, in the current accent.</summary>
    private static DrawingImage Logo(Brush fill, Color shadow, Color eye)
    {
        var eyeGeometry = Geometry.Parse("M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0 M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0");
        Pen EyePen(Color color, double thickness) =>
            new(new SolidColorBrush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(fill, null,
            Geometry.Parse("M22,0 H78 A22,22 0 0 1 100,22 V78 A22,22 0 0 1 78,100 H22 A22,22 0 0 1 0,78 V22 A22,22 0 0 1 22,0 Z")));
        group.Children.Add(new DrawingGroup
        {
            Opacity = 0.22,
            Transform = new MatrixTransform(2.583, 0, 0, 2.583, 19, 22.5),
            Children = { new GeometryDrawing(null, EyePen(shadow, 2.6), eyeGeometry) },
        });
        group.Children.Add(new DrawingGroup
        {
            Transform = new MatrixTransform(2.583, 0, 0, 2.583, 19, 20),
            Children = { new GeometryDrawing(null, EyePen(eye, 2), eyeGeometry) },
        });
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    // --- Color math

    /// <summary>Relative luminance (WCAG), 0 = black, 1 = white.</summary>
    public static double Luminance(Color c)
    {
        static double Lin(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Channel(double t)
        {
            t = (t % 360 + 360) % 360 / 360;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }
        byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return s == 0 ? Color.FromRgb(B(l), B(l), B(l)) : Color.FromRgb(B(Channel(h + 120)), B(Channel(h)), B(Channel(h - 120)));
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d < 1e-9 ? 0 : max == r ? 60 * (((g - b) / d + 6) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return (h, max < 1e-9 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        byte B(double t) => (byte)Math.Round(Math.Clamp(t + m, 0, 1) * 255);
        return Color.FromRgb(B(r), B(g), B(b));
    }
}
