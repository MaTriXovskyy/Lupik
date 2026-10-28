using System.Text;
using System.Windows;

namespace Lupik.Core;

public sealed partial class PdfDocument
{
    /// <summary>Managed text and glyph geometry; no native page or file handles survive this call.</summary>
    public PdfTextPage ReadTextPage(int index, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (Lock)
        {
            EnsureOpen();
            IntPtr page = Native.FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) return PdfTextPage.Empty;
            IntPtr text = IntPtr.Zero;
            try
            {
                text = Native.FPDFText_LoadPage(page);
                if (text == IntPtr.Zero) return PdfTextPage.Empty;
                int count = Math.Max(0, Native.FPDFText_CountChars(text));
                var characters = new string[count];
                var bounds = new Rect[count];
                const int scale = 100000;
                for (int i = 0; i < count; i++)
                {
                    if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
                    uint code = Native.FPDFText_GetUnicode(text, i);
                    characters[i] = Rune.IsValid((int)code) && code != 0 ? char.ConvertFromUtf32((int)code) : "";
                    bounds[i] = Rect.Empty;
                    if (Native.FPDFText_GetCharBox(text, i, out double left, out double right, out double bottom, out double top) == 0) continue;
                    // PageToDevice includes the PDF's own rotation, just like RenderPage(rotate=0).
                    if (Native.FPDF_PageToDevice(page, 0, 0, scale, scale, 0, left, top, out int x1, out int y1) == 0 ||
                        Native.FPDF_PageToDevice(page, 0, 0, scale, scale, 0, right, bottom, out int x2, out int y2) == 0) continue;
                    if (x1 == x2 || y1 == y2) continue;
                    bounds[i] = new Rect(Math.Min(x1, x2) / (double)scale, Math.Min(y1, y2) / (double)scale,
                        Math.Abs(x2 - x1) / (double)scale, Math.Abs(y2 - y1) / (double)scale);
                }
                return new PdfTextPage(characters, bounds);
            }
            finally
            {
                if (text != IntPtr.Zero) Native.FPDFText_ClosePage(text);
                Native.FPDF_ClosePage(page);
            }
        }
    }
}

public sealed class PdfTextPage
{
    public static readonly PdfTextPage Empty = new(Array.Empty<string>(), Array.Empty<Rect>());
    private readonly string[] _characters;
    public IReadOnlyList<Rect> Bounds { get; }
    public int Count => _characters.Length;
    internal PdfTextPage(string[] characters, Rect[] bounds) { _characters = characters; Bounds = bounds; }

    public static PdfTextPage FromOcr(IReadOnlyList<TextRecognition.Line> lines, int width, int height)
    {
        var words = new List<string>();
        var bounds = new List<Rect>();
        foreach (var line in lines)
        {
            if (words.Count > 0) { words.Add("\r"); bounds.Add(Rect.Empty); words.Add("\n"); bounds.Add(Rect.Empty); }
            foreach (var word in line.Words)
            {
                if (words.Count > 0 && words[^1] != "\n") { words.Add(" "); bounds.Add(Rect.Empty); }
                words.Add(word.Text);
                bounds.Add(new Rect(word.Bounds.X / width, word.Bounds.Y / height, word.Bounds.Width / width, word.Bounds.Height / height));
            }
        }
        return new PdfTextPage(words.ToArray(), bounds.ToArray());
    }

    public string GetText(int start, int end) => Count == 0 ? "" :
        string.Concat(_characters.Skip(Math.Clamp(start, 0, Count - 1)).Take(Math.Clamp(end, 0, Count - 1) - Math.Clamp(start, 0, Count - 1) + 1));

    public int HitTest(Point point, double toleranceX, double toleranceY, bool nearest)
    {
        int hit = -1;
        double best = double.MaxValue;
        for (int i = 0; i < Bounds.Count; i++)
        {
            var r = Bounds[i];
            if (r.IsEmpty || string.IsNullOrWhiteSpace(_characters[i])) continue;
            double dx = Math.Max(0, Math.Max(r.Left - point.X, point.X - r.Right));
            double dy = Math.Max(0, Math.Max(r.Top - point.Y, point.Y - r.Bottom));
            if (!nearest && (dx > toleranceX || dy > toleranceY)) continue;
            double distance = dx * dx + dy * dy * 16;
            if (distance >= best) continue;
            best = distance; hit = i;
        }
        return hit;
    }

    public (int Start, int End) WordRange(int index)
    {
        if (Count == 0) return (0, -1);
        index = Math.Clamp(index, 0, Count - 1);
        int Group(int i) => string.IsNullOrWhiteSpace(_characters[i]) ? 0 :
            Rune.IsLetterOrDigit(Rune.GetRuneAt(_characters[i], 0)) || _characters[i] == "_" ? 1 : 2;
        int group = Group(index), start = index, end = index;
        while (start > 0 && Group(start - 1) == group) start--;
        while (end + 1 < Count && Group(end + 1) == group) end++;
        return (start, end);
    }

    public (int Start, int End) LineRange(int index)
    {
        int start = index, end = index;
        bool Break(int i) => _characters[i] is "\r" or "\n";
        while (start > 0 && !Break(start - 1)) start--;
        while (end + 1 < Count && !Break(end + 1)) end++;
        return (start, end);
    }

    public List<Rect> Rectangles(int start, int end)
    {
        var result = new List<Rect>();
        for (int i = Math.Max(0, start); i <= end && i < Count; i++)
        {
            var rect = Bounds[i];
            if (rect.IsEmpty) continue;
            if (result.Count > 0)
            {
                var last = result[^1];
                double height = Math.Max(last.Height, rect.Height);
                if (Math.Abs(last.Top + last.Height / 2 - rect.Top - rect.Height / 2) < height * 0.3 &&
                    rect.Left <= last.Right + height && rect.Right >= last.Left - height)
                {
                    last.Union(rect); result[^1] = last; continue;
                }
            }
            result.Add(rect);
        }
        return result;
    }
}
