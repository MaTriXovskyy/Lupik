using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Lupik.Core;

/// <summary>Something printable: a PDF (many pages) or an image (one page).</summary>
public interface IPrintSource : IDisposable
{
    string Name { get; }
    int PageCount { get; }

    /// <summary>Page size in inches (used for orientation, "actual size" and the preview's paper shape).</summary>
    SizeF PageSizeInches(int index);

    /// <summary>Renders a page to a bitmap roughly <paramref name="pixelWidth"/> wide. Thread-safe.</summary>
    Bitmap RenderPage(int index, int pixelWidth);
}

public sealed class PdfPrintSource : IPrintSource
{
    private readonly PdfDocument _document;
    private readonly object _lock = new(); // PdfDocument isn't safe to render from several threads at once

    public string Name { get; }
    public int PageCount => (int)_document.PageCount;

    private PdfPrintSource(string name, PdfDocument document)
    {
        Name = name;
        _document = document;
    }

    public static async Task<PdfPrintSource> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var document = await PdfDocument.LoadFromFileAsync(file);
        return new PdfPrintSource(Path.GetFileName(path), document);
    }

    public SizeF PageSizeInches(int index)
    {
        lock (_lock)
        {
            using var page = _document.GetPage((uint)index);
            // PdfPage.Size is in DIPs (1/96 inch)
            return new SizeF((float)(page.Size.Width / 96), (float)(page.Size.Height / 96));
        }
    }

    public Bitmap RenderPage(int index, int pixelWidth)
    {
        lock (_lock)
        {
            using var page = _document.GetPage((uint)index);
            using var stream = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(64, pixelWidth),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };
            page.RenderToStreamAsync(stream, options).AsTask().GetAwaiter().GetResult();
            using var managed = stream.AsStream();
            using var image = Image.FromStream(managed);
            return PrintService.ToOpaque(image);
        }
    }

    public void Dispose() { /* PdfDocument has no Dispose; released with the object */ }
}

public sealed class ImagePrintSource : IPrintSource
{
    private readonly Bitmap _bitmap;
    private readonly float _dpiX, _dpiY;

    public string Name { get; }
    public int PageCount => 1;

    /// <summary>Loads any format Lupik can show (incl. HEIC/PSD), with the preview's rotation applied.</summary>
    public ImagePrintSource(string path, int rotationDegrees)
    {
        Name = Path.GetFileName(path);
        using var image = Views.ImageViewer.OpenForExport(path);
        image.AutoOrient();
        if (rotationDegrees != 0) image.Rotate(rotationDegrees);

        // Photos without a sensible DPI are treated as 96 dpi for "actual size"
        _dpiX = image.Density.X is > 30 and < 2400 ? (float)image.Density.X : 96;
        _dpiY = image.Density.Y is > 30 and < 2400 ? (float)image.Density.Y : 96;

        using var buffer = new MemoryStream();
        image.Write(buffer, ImageMagick.MagickFormat.Png32);
        buffer.Position = 0;
        using var loaded = Image.FromStream(buffer);
        _bitmap = PrintService.ToOpaque(loaded); // transparent areas print as white paper
    }

    public SizeF PageSizeInches(int index) => new(_bitmap.Width / _dpiX, _bitmap.Height / _dpiY);

    public Bitmap RenderPage(int index, int pixelWidth)
    {
        lock (_bitmap)
        {
            if (pixelWidth >= _bitmap.Width) return (Bitmap)_bitmap.Clone();
            int height = Math.Max(1, (int)((long)_bitmap.Height * pixelWidth / _bitmap.Width));
            var scaled = new Bitmap(pixelWidth, height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(_bitmap, 0, 0, pixelWidth, height);
            return scaled;
        }
    }

    public void Dispose() => _bitmap.Dispose();
}

public enum PrintScale { FitToPage, ActualSize }

public enum PrintOrientation { Auto, Portrait, Landscape }

public sealed class PrintOptions
{
    public string PrinterName { get; set; } = "";
    public int Copies { get; set; } = 1;
    public bool Collate { get; set; } = true;
    /// <summary>Zero-based page indexes, in print order.</summary>
    public IReadOnlyList<int> Pages { get; set; } = Array.Empty<int>();
    public PrintOrientation Orientation { get; set; } = PrintOrientation.Auto;
    public bool Color { get; set; } = true;
    public Duplex Duplex { get; set; } = Duplex.Simplex;
    public string? PaperSizeName { get; set; }
    public PrintScale Scale { get; set; } = PrintScale.FitToPage;
    /// <summary>For tests: print into this file (works with "Microsoft Print to PDF") instead of a device.</summary>
    public string? PrintToFile { get; set; }
}

public static class PrintService
{
    /// <summary>Resolution pages are rendered at for printing: sharp text without huge spool files.</summary>
    private const int PrintDpi = 300;

    /// <summary>
    /// Copies an image into an opaque 24-bit bitmap on white. Printing 32-bit ARGB bitmaps makes GDI+
    /// go through a very slow transparency-flattening path (seconds per page); paper has no alpha anyway.
    /// </summary>
    public static Bitmap ToOpaque(Image image)
    {
        var opaque = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        opaque.SetResolution(96, 96);
        using var g = Graphics.FromImage(opaque);
        g.Clear(System.Drawing.Color.White);
        g.DrawImage(image, 0, 0, image.Width, image.Height);
        return opaque;
    }

    public static IReadOnlyList<string> InstalledPrinters() =>
        PrinterSettings.InstalledPrinters.Cast<string>().ToList();

    public static string DefaultPrinter() => new PrinterSettings().PrinterName;

    /// <summary>
    /// Parses a Chrome-style page range ("1-3, 5, 8-") into zero-based indexes.
    /// Returns null when the text is invalid or outside 1..pageCount.
    /// </summary>
    public static List<int>? ParsePageRange(string text, int pageCount)
    {
        var pages = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (string raw in text.Split(',', ';'))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;

            int from, to;
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, out from)) return null;
                to = from;
            }
            else
            {
                string left = part[..dash].Trim(), right = part[(dash + 1)..].Trim();
                from = left.Length == 0 ? 1 : int.TryParse(left, out var l) ? l : -1;
                to = right.Length == 0 ? pageCount : int.TryParse(right, out var r) ? r : -1;
            }

            if (from < 1 || to < from || to > pageCount) return null;
            for (int p = from; p <= to; p++) pages.Add(p - 1);
        }
        return pages.Count > 0 ? pages : null;
    }

    /// <summary>Whether a page should be printed landscape (Auto follows the page's own shape).</summary>
    public static bool IsLandscape(PrintOptions options, SizeF pageInches) => options.Orientation switch
    {
        PrintOrientation.Landscape => true,
        PrintOrientation.Portrait => false,
        _ => pageInches.Width > pageInches.Height,
    };

    /// <summary>Sends the job to the printer. Runs on a background thread; throws on printer errors.</summary>
    public static Task PrintAsync(IPrintSource source, PrintOptions options) => Task.Run(() =>
    {
        using var document = new PrintDocument
        {
            DocumentName = source.Name,
            PrintController = new StandardPrintController(), // no "Printing page x" popup
        };

        var settings = document.PrinterSettings;
        settings.PrinterName = options.PrinterName;
        if (!settings.IsValid) throw new InvalidOperationException(Lupik.Localization.Loc.T("print.printerUnavailableNamed", options.PrinterName));

        if (options.PrintToFile != null)
        {
            settings.PrintToFile = true;
            settings.PrintFileName = options.PrintToFile;
        }

        // Copies: let the printer do them when it can, otherwise repeat the pages ourselves
        var queue = new List<int>();
        if (options.Copies <= settings.MaximumCopies && options.Copies > 1)
        {
            settings.Copies = (short)options.Copies;
            settings.Collate = options.Collate;
            queue.AddRange(options.Pages);
        }
        else if (options.Collate)
        {
            for (int c = 0; c < options.Copies; c++) queue.AddRange(options.Pages);
        }
        else
        {
            foreach (int p in options.Pages) queue.AddRange(Enumerable.Repeat(p, options.Copies));
        }

        if (settings.CanDuplex) settings.Duplex = options.Duplex;

        var pageSettings = document.DefaultPageSettings;
        pageSettings.Color = options.Color && settings.SupportsColor;
        if (options.PaperSizeName != null)
        {
            var paper = settings.PaperSizes.Cast<PaperSize>().FirstOrDefault(p => p.PaperName == options.PaperSizeName);
            if (paper != null) pageSettings.PaperSize = paper;
        }

        int next = 0;
        document.QueryPageSettings += (_, e) =>
        {
            // Orientation can differ per page (Auto on a mixed PDF)
            if (next < queue.Count) e.PageSettings.Landscape = IsLandscape(options, source.PageSizeInches(queue[next]));
        };

        document.PrintPage += (_, e) =>
        {
            int index = queue[next];
            DrawPage(e, source, index, options);
            next++;
            e.HasMorePages = next < queue.Count;
        };

        App.Log($"[PrintService] Printing '{source.Name}' on '{options.PrinterName}': {options.Pages.Count} page(s) × {options.Copies}, " +
                $"duplex={options.Duplex}, color={options.Color}, scale={options.Scale}, orientation={options.Orientation}");
        document.Print();
    });

    private static void DrawPage(PrintPageEventArgs e, IPrintSource source, int index, PrintOptions options)
    {
        var g = e.Graphics!;
        g.PageUnit = GraphicsUnit.Display; // 1/100 inch

        // Printable area (inside the printer's hard margins); the origin is already at its top-left corner
        var area = e.PageSettings.PrintableArea;
        float areaW = e.PageSettings.Landscape ? area.Height : area.Width;
        float areaH = e.PageSettings.Landscape ? area.Width : area.Height;

        var inches = source.PageSizeInches(index);
        float pageW = inches.Width * 100, pageH = inches.Height * 100;

        float drawW, drawH;
        if (options.Scale == PrintScale.ActualSize)
        {
            drawW = pageW;
            drawH = pageH;
        }
        else
        {
            float scale = Math.Min(areaW / pageW, areaH / pageH);
            drawW = pageW * scale;
            drawH = pageH * scale;
        }
        var target = new RectangleF((areaW - drawW) / 2, (areaH - drawH) / 2, drawW, drawH);

        int pixelWidth = (int)Math.Min(4000, drawW / 100 * PrintDpi);
        using var bitmap = source.RenderPage(index, pixelWidth);

        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.SetClip(new RectangleF(0, 0, areaW, areaH));

        if (options.Color)
        {
            g.DrawImage(bitmap, target);
        }
        else
        {
            // Grayscale ourselves too: some drivers ignore the "black & white" page setting
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix(new[]
            {
                new[] { 0.299f, 0.299f, 0.299f, 0, 0 },
                new[] { 0.587f, 0.587f, 0.587f, 0, 0 },
                new[] { 0.114f, 0.114f, 0.114f, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { 0, 0, 0, 0, 1 },
            }));
            g.DrawImage(bitmap, Rectangle.Round(target), 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
        }
    }
}
