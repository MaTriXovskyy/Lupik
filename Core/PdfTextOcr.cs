using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Lupik.Core;

/// <summary>On-device fallback for PDFs made of images; one page at a time to bound memory and CPU use.</summary>
public static class PdfTextOcr
{
    private static readonly SemaphoreSlim Reader = new(1);

    public static async Task<PdfTextPage> ReadAsync(PdfDocument document, int page, CancellationToken cancellation = default)
    {
        await Reader.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (!TextRecognition.IsAvailable) return PdfTextPage.Empty;
                using var bitmap = document.RenderPageForPrint(page, 2400);
                cancellation.ThrowIfCancellationRequested();
                var lines = (await TextRecognition.RecognizeAsync(bitmap).ConfigureAwait(false)).ToList();
                // Whole-page OCR misses small, isolated labels. Read nearby glyphs together at
                // their original resolution, with enough white margin for the recognizer.
                foreach (var region in LabelRegions(bitmap, lines.SelectMany(l => l.Words).Select(w => w.Bounds).ToArray(), cancellation))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var extra = await TextRecognition.RecognizeAsync(bitmap, region).ConfigureAwait(false);
                    Merge(lines, extra);
                }
                // Windows OCR only reads horizontal text. Turn a copy, then map every word
                // back to the unrotated page so selection follows vertical captions as well.
                var vertical = new List<TextRecognition.Line>();
                foreach (int turn in new[] { 1, 3 })
                {
                    cancellation.ThrowIfCancellationRequested();
                    using var rotated = (Bitmap)bitmap.Clone();
                    rotated.RotateFlip((RotateFlipType)turn);
                    var extra = await TextRecognition.RecognizeAsync(rotated).ConfigureAwait(false);
                    foreach (var line in extra.OrderByDescending(ReadingScore))
                    {
                        TextRecognition.Rect Map(TextRecognition.Rect r) => turn == 1
                            ? new(r.Y, bitmap.Height - r.Right, r.Height, r.Width)
                            : new(bitmap.Width - r.Bottom, r.X, r.Height, r.Width);
                        var words = line.Words.Select(w => new TextRecognition.Word(w.Text, Map(w.Bounds))).ToArray();
                        vertical.Add(new(line.Text, Map(line.Bounds), words));
                    }
                }
                Merge(lines, vertical.OrderByDescending(ReadingScore));
                cancellation.ThrowIfCancellationRequested();
                return PdfTextPage.FromOcr(lines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X).ToArray(), bitmap.Width, bitmap.Height);
            }, cancellation).ConfigureAwait(false);
        }
        finally { Reader.Release(); }
    }

    private static double ReadingScore(TextRecognition.Line line) =>
        line.Text.Sum(c => char.IsDigit(c) ? 2.0 : char.IsLower(c) ? 1.1 : char.IsLetter(c) ? 1.0 : char.IsWhiteSpace(c) ? 0 : -1);

    private static void Merge(List<TextRecognition.Line> destination, IEnumerable<TextRecognition.Line> extra)
    {
        foreach (var line in extra)
        {
            var words = line.Words.Where(w => !destination.SelectMany(l => l.Words).Any(existing =>
            {
                var a = existing.Bounds; var b = w.Bounds;
                double overlap = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X)) *
                    Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
                return overlap > Math.Min(a.Width * a.Height, b.Width * b.Height) * 0.5;
            })).ToArray();
            if (words.Length == 0) continue;
            destination.Add(new(string.Join(" ", words.Select(w => w.Text)),
                words.Select(w => w.Bounds).Aggregate((a, b) => a.Union(b)), words));
        }
    }

    // Connected ink on a light background finds labels without sending photographs through
    // dozens of expensive OCR crops. Limits also bound work on noisy scanned pages.
    private static IEnumerable<Rectangle> LabelRegions(Bitmap bitmap, TextRecognition.Rect[] known, CancellationToken cancellation)
    {
        int width = bitmap.Width, height = bitmap.Height;
        using var rgb = bitmap.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppArgb);
        var data = rgb.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * height];
        try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
        finally { rgb.UnlockBits(data); }
        int stride = pixels.Length / height;
        bool Dark(int x, int y)
        {
            int p = y * stride + x * 4;
            return pixels[p] * 114 + pixels[p + 1] * 587 + pixels[p + 2] * 299 < 200000;
        }
        var seen = new bool[width * height];
        var queue = new int[width * height];
        var glyphs = new List<Rectangle>();
        for (int y = 0; y < height; y++)
        {
            if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int start = y * width + x;
                if (seen[start] || !Dark(x, y)) continue;
                int head = 0, tail = 1, left = x, right = x, top = y, bottom = y;
                queue[0] = start; seen[start] = true;
                while (head < tail)
                {
                    int at = queue[head++], cx = at % width, cy = at / width;
                    left = Math.Min(left, cx); right = Math.Max(right, cx); top = Math.Min(top, cy); bottom = Math.Max(bottom, cy);
                    void Add(int nx, int ny)
                    {
                        if (nx < 0 || nx >= width || ny < 0 || ny >= height) return;
                        int n = ny * width + nx;
                        if (seen[n] || !Dark(nx, ny)) return;
                        seen[n] = true; queue[tail++] = n;
                    }
                    Add(cx - 1, cy); Add(cx + 1, cy); Add(cx, cy - 1); Add(cx, cy + 1);
                }
                int w = right - left + 1, h = bottom - top + 1;
                if (tail < 8 || w < 2 || h < 6 || w > 100 || h > 100 || w > h * 6) continue;
                int light = 0, samples = 0;
                for (int sy = Math.Max(0, top - 5); sy <= Math.Min(height - 1, bottom + 5); sy += 2)
                    for (int sx = Math.Max(0, left - 5); sx <= Math.Min(width - 1, right + 5); sx += 2)
                        if (sx < left || sx > right || sy < top || sy > bottom) { samples++; if (!Dark(sx, sy)) light++; }
                if (light < samples * 0.85) continue;
                glyphs.Add(new(left, top, w, h));
                if (glyphs.Count > 800) yield break;
            }
        }
        var groups = new List<Rectangle>();
        bool Covered(Rectangle g) => known.Any(r =>
            Math.Max(0, Math.Min(r.Right, g.Right) - Math.Max(r.X, g.X)) *
            Math.Max(0, Math.Min(r.Bottom, g.Bottom) - Math.Max(r.Y, g.Y)) > g.Width * g.Height * 0.7);
        while (glyphs.Count > 0)
        {
            var group = glyphs[^1]; glyphs.RemoveAt(glyphs.Count - 1);
            bool needsRead = !Covered(group);
            bool changed;
            do
            {
                changed = false;
                for (int i = glyphs.Count - 1; i >= 0; i--)
                {
                    var g = glyphs[i];
                    int gapX = Math.Max(0, Math.Max(group.Left - g.Right, g.Left - group.Right));
                    int gapY = Math.Max(0, Math.Max(group.Top - g.Bottom, g.Top - group.Bottom));
                    int size = Math.Min(40, Math.Max(g.Height, Math.Min(group.Height, 40)));
                    var union = Rectangle.Union(group, g);
                    if (gapX > size || gapY > size * 0.8 || union.Width > 600 || union.Height > 160) continue;
                    needsRead |= !Covered(g);
                    group = union; glyphs.RemoveAt(i); changed = true;
                }
            } while (changed);
            if (!needsRead) continue;
            int pad = Math.Clamp(group.Height / 5, 8, 16);
            group.Inflate(pad, pad);
            group.Intersect(new Rectangle(0, 0, width, height));
            groups.Add(group);
        }
        foreach (var group in groups.OrderBy(r => r.Top).ThenBy(r => r.Left).Take(64)) yield return group;
    }
}
