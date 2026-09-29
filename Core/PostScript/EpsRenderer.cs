using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lupik.Core.PostScript;

/// <summary>
/// Renders EPS / PS files with Lupik's own PostScript interpreter (no Ghostscript needed).
/// Binary "DOS EPS" files (Photoshop, older Illustrator) carry the PostScript plus a TIFF preview;
/// the preview is used only if the PostScript itself draws nothing.
/// </summary>
internal static class EpsRenderer
{
    public sealed record Result(BitmapSource Bitmap, int PixelWidth, int PixelHeight, bool FromPreview, int Errors);

    public static Result Render(byte[] file, int minSize, int maxSize)
    {
        var (ps, tiff) = SplitDosEps(file);
        var box = BoundingBox(ps) ?? new Rect(0, 0, 612, 792);
        if (box.Width < 1 || box.Height < 1) box = new Rect(box.X, box.Y, Math.Max(box.Width, 1), Math.Max(box.Height, 1));

        // Points → pixels: at least minSize on the long side (vectors stay sharp when zoomed), at most maxSize
        double longest = Math.Max(box.Width, box.Height);
        double scale = Math.Min(Math.Max(minSize / longest, 96.0 / 72), maxSize / longest);
        int pw = Math.Max(1, (int)Math.Ceiling(box.Width * scale));
        int ph = Math.Max(1, (int)Math.Ceiling(box.Height * scale));

        Result? result = null;
        Exception? failure = null;
        // WPF drawing objects need an STA thread
        var thread = new Thread(() =>
        {
            try { result = RenderOnSta(ps, box, scale, pw, ph); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (result != null && !IsBlank(result)) return result;

        if (tiff != null)
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(tiff), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return new Result(frame, frame.PixelWidth, frame.PixelHeight, true, result?.Errors ?? 0);
        }
        if (result != null) return result;
        throw failure ?? new InvalidOperationException("EPS could not be rendered");
    }

    private static bool IsBlank(Result r) => r.Errors < 0;

    private static Result RenderOnSta(byte[] ps, Rect box, double scale, int pw, int ph)
    {
        var interpreter = new PsInterpreter(TimeSpan.FromSeconds(20));
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pw, ph));
            // PostScript: origin bottom-left, y up, units of 1/72"; device: pixels, y down
            var device = new Matrix(scale, 0, 0, -scale, -box.X * scale, (box.Y + box.Height) * scale);
            interpreter.PageDeviceRect = new Rect(0, 0, pw, ph);
            interpreter.BeginPage(dc, device);
            try
            {
                interpreter.RunSource(new BytesSource(ps), topLevel: true);
            }
            catch (PsQuit q)
            {
                App.Log($"[EpsRenderer] Stopped: {q.Message}");
            }
            catch (Exception ex) when (ex is PsError or PsExit or PsStop)
            {
                App.Log($"[EpsRenderer] Aborted: {ex.Message}");
            }
            interpreter.EndPage();
        }

        foreach (var e in interpreter.ErrorLog) App.Log("[EpsRenderer]   " + e);
        App.Log($"[EpsRenderer] {interpreter.PaintCount} paint ops, {interpreter.ErrorCount} errors (first: {interpreter.FirstError ?? "none"})");

        var visual = new DrawingVisual();
        using (var vdc = visual.RenderOpen()) vdc.DrawDrawing(drawing);
        var bmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return new Result(bmp, pw, ph, false, interpreter.PaintCount == 0 ? -1 : interpreter.ErrorCount);
    }

    /// <summary>DOS EPS binary header: C5D0D3C6, then offset/length of the PostScript, WMF and TIFF sections.</summary>
    private static (byte[] PostScript, byte[]? Tiff) SplitDosEps(byte[] file)
    {
        if (file.Length < 30 || file[0] != 0xC5 || file[1] != 0xD0 || file[2] != 0xD3 || file[3] != 0xC6)
            return (file, null);
        int U32(int at) => BitConverter.ToInt32(file, at);
        int psStart = U32(4), psLen = U32(8), tiffStart = U32(20), tiffLen = U32(24);
        byte[] ps = psStart > 0 && psStart + psLen <= file.Length ? file[psStart..(psStart + psLen)] : file;
        byte[]? tiff = tiffStart > 0 && tiffLen > 0 && tiffStart + tiffLen <= file.Length ? file[tiffStart..(tiffStart + tiffLen)] : null;
        return (ps, tiff);
    }

    /// <summary>%%HiResBoundingBox (preferred) or %%BoundingBox; "(atend)" means it's in the trailer.</summary>
    internal static Rect? BoundingBox(byte[] ps)
    {
        string head = Encoding.Latin1.GetString(ps, 0, Math.Min(ps.Length, 64 * 1024));
        string tail = ps.Length > 64 * 1024 ? Encoding.Latin1.GetString(ps, ps.Length - 16 * 1024, 16 * 1024) : "";
        foreach (var key in new[] { "%%HiResBoundingBox:", "%%BoundingBox:" })
        {
            foreach (var text in new[] { head, tail })
            {
                var m = Regex.Match(text, Regex.Escape(key) + @"\s*([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)");
                if (m.Success)
                {
                    double P(int i) => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
                    double x0 = P(1), y0 = P(2), x1 = P(3), y1 = P(4);
                    if (x1 > x0 && y1 > y0) return new Rect(x0, y0, x1 - x0, y1 - y0);
                }
            }
        }
        return null;
    }
}
