using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Lupik.Core;

/// <summary>
/// Text in pictures, read by Windows' own OCR engine, prepared the way PowerToys' Text Extractor does it (which is
/// why it catches far more than the engine on its own): the picture is enlarged 1.5× with high-quality bicubic
/// scaling, and tiny cut-outs get a margin in their own background colour so the engine has room to find the
/// letters. Lupik adds a second pass at 3× for very small print (lines under ~14 px tall), where 1.5× still misreads
/// letters, and keeps whichever pass reads cleaner.
/// </summary>
public static class TextRecognition
{
    public sealed record Word(string Text, Rect Bounds);

    public sealed record Line(string Text, Rect Bounds, IReadOnlyList<Word> Words);

    /// <summary>A rectangle in the source picture's pixels.</summary>
    public readonly record struct Rect(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;
        public bool IntersectsWith(Rect o) => o.X < Right && o.Right > X && o.Y < Bottom && o.Bottom > Y;
        public Rect Union(Rect o)
        {
            double x = Math.Min(X, o.X), y = Math.Min(Y, o.Y);
            return new Rect(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
        }
    }

    private const double EnhancedScale = 1.5, SmallPrintScale = 3.0, SmallPrintHeight = 14;
    private const int MinimumDimension = 64, Padding = 8;

    /// <summary>Is any OCR language installed? (Windows ships the one of its display language.)</summary>
    public static bool IsAvailable => CreateEngine() != null;

    private static OcrEngine? CreateEngine() =>
        OcrEngine.TryCreateFromUserProfileLanguages()
        ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e != null);

    /// <summary>
    /// Reads <paramref name="region"/> of <paramref name="source"/> (the whole picture when null). Returned positions
    /// are in the source's pixels.
    /// </summary>
    public static async Task<IReadOnlyList<Line>> RecognizeAsync(Bitmap source, Rectangle? region = null)
    {
        var engine = CreateEngine() ?? throw new InvalidOperationException("No OCR language is installed.");
        var area = region ?? new Rectangle(0, 0, source.Width, source.Height);
        area.Intersect(new Rectangle(0, 0, source.Width, source.Height));
        if (area.Width < 2 || area.Height < 2) return Array.Empty<Line>();

        using var cut = source.Clone(area, PixelFormat.Format32bppArgb);
        var lines = await RecognizeScaledAsync(engine, cut, area, ScaleFor(cut, EnhancedScale));

        // Small print (or nothing found): read it again bigger, keep the cleaner result
        bool small = lines.Count == 0 || Median(lines.Select(l => l.Bounds.Height)) < SmallPrintHeight;
        if (small && ScaleFor(cut, SmallPrintScale) > EnhancedScale)
        {
            var bigger = await RecognizeScaledAsync(engine, cut, area, ScaleFor(cut, SmallPrintScale));
            if (Quality(bigger) > Quality(lines)) lines = bigger;
        }
        return lines;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    /// <summary>
    /// How clean a reading looks: letters and digits count for it, stray symbols (what misread letters turn into:
    /// •, ŕ, ', :) count against it.
    /// </summary>
    private static double Quality(IReadOnlyList<Line> lines)
    {
        double score = 0;
        foreach (char c in lines.SelectMany(l => l.Text))
        {
            if (char.IsLetterOrDigit(c)) score += 1;
            else if (c is ' ' or '.' or ',' or '-' or '–' or '—' or '/' or '(' or ')' or '%' or '?' or '!' or ';' or '"' or '@' or '#' or '&' or '+' or '=') { }
            else score -= 3;
        }
        return score;
    }

    /// <summary>The wanted scale, reduced if the result would be bigger than the engine accepts.</summary>
    private static double ScaleFor(Bitmap bmp, double wanted)
    {
        double max = OcrEngine.MaxImageDimension - Padding * 2;
        return Math.Min(wanted, Math.Min(max / bmp.Width, max / bmp.Height));
    }

    private static async Task<IReadOnlyList<Line>> RecognizeScaledAsync(OcrEngine engine, Bitmap cut, Rectangle area, double scale)
    {
        // Enlarge (and pad tiny cut-outs with their background), like PowerToys' BitmapPreprocessor
        int w = Math.Max(1, (int)Math.Round(cut.Width * scale)), h = Math.Max(1, (int)Math.Round(cut.Height * scale));
        int offsetX = w < MinimumDimension ? Padding : 0, offsetY = h < MinimumDimension ? Padding : 0;
        int outW = offsetX > 0 ? Math.Max(w, MinimumDimension) + Padding * 2 : w;
        int outH = offsetY > 0 ? Math.Max(h, MinimumDimension) + Padding * 2 : h;
        using var prepared = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(prepared))
        {
            g.Clear(cut.GetPixel(0, 0));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(cut, new Rectangle(offsetX, offsetY, w, h), new Rectangle(0, 0, cut.Width, cut.Height), GraphicsUnit.Pixel);
        }

        using var stream = new MemoryStream();
        prepared.Save(stream, ImageFormat.Bmp);
        stream.Position = 0;
        using var ras = stream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var software = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(software);

        // Back to the source picture's pixels
        double sx = (double)w / cut.Width, sy = (double)h / cut.Height;
        Rect ToSource(Windows.Foundation.Rect r) =>
            new(area.X + (r.X - offsetX) / sx, area.Y + (r.Y - offsetY) / sy, r.Width / sx, r.Height / sy);

        var lines = new List<Line>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Select(word => new Word(word.Text, ToSource(word.BoundingRect))).ToList();
            if (words.Count == 0) continue;
            var bounds = words.Select(x => x.Bounds).Aggregate((a, b) => a.Union(b));
            lines.Add(new Line(line.Text, bounds, words));
        }
        return lines;
    }

    /// <summary>Lines as text, one per line (languages without spaces between words are joined without them).</summary>
    public static string ToText(IEnumerable<Line> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(line.Text);
        }
        return sb.ToString().Trim();
    }
}
