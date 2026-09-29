using System.Windows;
using System.Windows.Media;

namespace Lupik.Views;

/// <summary>
/// A small country flag drawn with vectors (Windows' emoji font has no flags). Code = language code:
/// pl, en (United Kingdom), de, es, fr, it, ru, uk (Ukraine). Rounded corners, thin outline.
/// </summary>
public class FlagIcon : FrameworkElement
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(FlagIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public string? Code
    {
        get => (string?)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public FlagIcon()
    {
        Width = 24;
        Height = 16;
        SnapsToDevicePixels = true;
    }

    private static readonly Brush Outline = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Brush Hex(string hex) => Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || Code == null) return;
        var bounds = new Rect(0, 0, w, h);
        double r = Math.Min(w, h) * 0.16;

        dc.PushClip(new RectangleGeometry(bounds, r, r));
        switch (Code)
        {
            case "pl": Stripes(dc, w, h, horizontal: true, "#FFFFFF", "#DC143C"); break;
            case "de": Stripes(dc, w, h, horizontal: true, "#000000", "#DD0000", "#FFCE00"); break;
            case "ru": Stripes(dc, w, h, horizontal: true, "#FFFFFF", "#0039A6", "#D52B1E"); break;
            case "uk": Stripes(dc, w, h, horizontal: true, "#0057B7", "#FFD700"); break;
            case "fr": Stripes(dc, w, h, horizontal: false, "#0055A4", "#FFFFFF", "#EF4135"); break;
            case "it": Stripes(dc, w, h, horizontal: false, "#009246", "#FFFFFF", "#CE2B37"); break;
            case "es":
                // Red, yellow twice as tall, red (the coat of arms is too small to see at this size)
                dc.DrawRectangle(Hex("#AA151B"), null, bounds);
                dc.DrawRectangle(Hex("#F1BF00"), null, new Rect(0, h / 4, w, h / 2));
                break;
            case "en": UnionJack(dc, w, h); break;
        }
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(Outline, 1), new Rect(0.5, 0.5, w - 1, h - 1), r, r);
    }

    private static void Stripes(DrawingContext dc, double w, double h, bool horizontal, params string[] colors)
    {
        int n = colors.Length;
        for (int i = 0; i < n; i++)
        {
            var rect = horizontal
                ? new Rect(0, h * i / n, w, h / n + 0.5)
                : new Rect(w * i / n, 0, w / n + 0.5, h);
            dc.DrawRectangle(Hex(colors[i]), null, rect);
        }
    }

    private static void UnionJack(DrawingContext dc, double w, double h)
    {
        var blue = Hex("#012169");
        var white = Brushes.White;
        var red = Hex("#C8102E");
        dc.DrawRectangle(blue, null, new Rect(0, 0, w, h));

        // Proportions of the 60×30 flag, scaled to the height
        double unit = h / 30;
        var whiteDiag = new Pen(white, 6 * unit);
        var redDiag = new Pen(red, 2 * unit);
        dc.DrawLine(whiteDiag, new Point(0, 0), new Point(w, h));
        dc.DrawLine(whiteDiag, new Point(w, 0), new Point(0, h));
        dc.DrawLine(redDiag, new Point(0, 0), new Point(w, h));
        dc.DrawLine(redDiag, new Point(w, 0), new Point(0, h));

        double cw = 10 * unit, rw = 6 * unit;
        dc.DrawRectangle(white, null, new Rect((w - cw) / 2, 0, cw, h));
        dc.DrawRectangle(white, null, new Rect(0, (h - cw) / 2, w, cw));
        dc.DrawRectangle(red, null, new Rect((w - rw) / 2, 0, rw, h));
        dc.DrawRectangle(red, null, new Rect(0, (h - rw) / 2, w, rw));
    }
}
