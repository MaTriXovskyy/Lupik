using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lupik.Core;

/// <summary>
/// PDF documents through PDFium (Chrome's PDF engine, BSD licence; pdfium.dll comes from the
/// bblanchon.PDFium.Win32 package). PDFium isn't thread-safe, so every call goes through one lock;
/// rendering from background threads is fine, just one page at a time.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private static readonly object Lock = new();
    private IntPtr _doc;

    public string Path { get; }
    public int PageCount { get; }

    static PdfDocument()
    {
        lock (Lock) Native.FPDF_InitLibrary();
    }

    private PdfDocument(string path, IntPtr doc)
    {
        Path = path;
        _doc = doc;
        PageCount = Native.FPDF_GetPageCount(doc);
    }

    /// <summary>Opens a PDF (or a PDF-compatible .ai). Throws <see cref="PdfException"/> when it can't.</summary>
    public static PdfDocument Open(string path, string? password = null)
    {
        lock (Lock)
        {
            IntPtr doc = Native.FPDF_LoadDocument(path, password);
            if (doc == IntPtr.Zero) throw new PdfException((PdfError)Native.FPDF_GetLastError());
            return new PdfDocument(path, doc);
        }
    }

    public static Task<PdfDocument> OpenAsync(string path, string? password = null) => Task.Run(() => Open(path, password));

    /// <summary>Page size in points (1/72 inch).</summary>
    public (double Width, double Height) PageSizePoints(int index)
    {
        lock (Lock)
        {
            EnsureOpen();
            if (Native.FPDF_GetPageSizeByIndexF(_doc, index, out var size) == 0) return (612, 792); // Letter, if PDFium can't tell
            return (size.Width, size.Height);
        }
    }

    /// <summary>
    /// Renders a page <paramref name="pixelWidth"/> pixels wide (height follows the page's proportions),
    /// on white. <paramref name="forPrinting"/> uses PDFium's print mode (print-only content, no LCD text).
    /// The pixels are written straight into <paramref name="target"/>, a 32-bit BGR buffer.
    /// </summary>
    private void RenderInto(int index, int width, int height, IntPtr target, int stride, bool forPrinting)
    {
        lock (Lock)
        {
            EnsureOpen();
            IntPtr page = Native.FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) throw new PdfException((PdfError)Native.FPDF_GetLastError());
            IntPtr bitmap = Native.FPDFBitmap_CreateEx(width, height, Native.FPDFBitmap_BGRx, target, stride);
            try
            {
                Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                int flags = Native.FPDF_ANNOT | (forPrinting ? Native.FPDF_PRINTING : 0);
                Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, flags);
            }
            finally
            {
                Native.FPDFBitmap_Destroy(bitmap);
                Native.FPDF_ClosePage(page);
            }
        }
    }

    private (int Width, int Height) PixelSize(int index, int pixelWidth, int? pixelHeight = null)
    {
        if (pixelHeight is int exact) return (Math.Max(16, pixelWidth), Math.Max(16, exact));
        var (w, h) = PageSizePoints(index);
        int width = Math.Max(16, pixelWidth);
        int height = Math.Max(16, (int)Math.Round(width * h / Math.Max(1, w)));
        return (width, height);
    }

    /// <summary>
    /// The page as a frozen WPF bitmap (usable from any thread). Pass <paramref name="pixelHeight"/> to get exactly
    /// the size it will be shown at (otherwise it follows the page's proportions).
    /// </summary>
    public BitmapSource RenderPage(int index, int pixelWidth, int? pixelHeight = null)
    {
        var (width, height) = PixelSize(index, pixelWidth, pixelHeight);
        int stride = width * 4;
        var pixels = new byte[stride * height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            RenderInto(index, width, height, handle.AddrOfPinnedObject(), stride, forPrinting: false);
        }
        finally
        {
            handle.Free();
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The page as a GDI+ bitmap, for printing.</summary>
    public System.Drawing.Bitmap RenderPageForPrint(int index, int pixelWidth)
    {
        var (width, height) = PixelSize(index, pixelWidth);
        var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            RenderInto(index, width, height, data.Scan0, data.Stride, forPrinting: true);
        }
        catch
        {
            bitmap.UnlockBits(data);
            bitmap.Dispose();
            throw;
        }
        bitmap.UnlockBits(data);
        return bitmap;
    }

    private void EnsureOpen()
    {
        if (_doc == IntPtr.Zero) throw new ObjectDisposedException(nameof(PdfDocument));
    }

    /// <summary>
    /// Where <paramref name="query"/> occurs on a page (case-insensitive): one entry per match, each a list of
    /// rectangles as fractions of the page (0..1, from the top left), since a match can wrap onto the next line.
    /// </summary>
    public List<List<System.Windows.Rect>> FindText(int index, string query)
    {
        var matches = new List<List<System.Windows.Rect>>();
        if (query.Length == 0) return matches;
        lock (Lock)
        {
            EnsureOpen();
            if (Native.FPDF_GetPageSizeByIndexF(_doc, index, out var size) == 0 || size.Width <= 0 || size.Height <= 0) return matches;
            IntPtr page = Native.FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) return matches;
            IntPtr text = Native.FPDFText_LoadPage(page);
            try
            {
                if (text == IntPtr.Zero) return matches;
                IntPtr find = Native.FPDFText_FindStart(text, query, 0, 0);
                if (find == IntPtr.Zero) return matches;
                try
                {
                    while (Native.FPDFText_FindNext(find) != 0 && matches.Count < 5000)
                    {
                        int start = Native.FPDFText_GetSchResultIndex(find), count = Native.FPDFText_GetSchCount(find);
                        int rects = Native.FPDFText_CountRects(text, start, count);
                        var list = new List<System.Windows.Rect>(rects);
                        for (int r = 0; r < rects; r++)
                        {
                            if (Native.FPDFText_GetRect(text, r, out double left, out double top, out double right, out double bottom) == 0) continue;
                            // PDF coordinates start at the bottom left, in points
                            list.Add(new System.Windows.Rect(left / size.Width, (size.Height - top) / size.Height,
                                Math.Max(0, right - left) / size.Width, Math.Max(0, top - bottom) / size.Height));
                        }
                        if (list.Count > 0) matches.Add(list);
                    }
                }
                finally
                {
                    Native.FPDFText_FindClose(find);
                }
            }
            finally
            {
                if (text != IntPtr.Zero) Native.FPDFText_ClosePage(text);
                Native.FPDF_ClosePage(page);
            }
        }
        return matches;
    }

    public void Dispose()
    {
        lock (Lock)
        {
            if (_doc == IntPtr.Zero) return;
            Native.FPDF_CloseDocument(_doc);
            _doc = IntPtr.Zero;
        }
    }

    private static class Native
    {
        private const string Dll = "pdfium.dll";

        public const int FPDF_ANNOT = 0x01, FPDF_PRINTING = 0x800;
        public const int FPDFBitmap_BGRx = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct FS_SIZEF { public float Width, Height; }

        [DllImport(Dll)] public static extern void FPDF_InitLibrary();
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport(Dll)] public static extern uint FPDF_GetLastError();
        [DllImport(Dll)] public static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Dll)] public static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport(Dll)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr document, int index, out FS_SIZEF size);
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Dll)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Dll)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
        [DllImport(Dll)] public static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Dll)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
        [DllImport(Dll)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

        // Text (search)
        [DllImport(Dll)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport(Dll)] public static extern void FPDFText_ClosePage(IntPtr textPage);
        [DllImport(Dll)] public static extern IntPtr FPDFText_FindStart(IntPtr textPage, [MarshalAs(UnmanagedType.LPWStr)] string findWhat, uint flags, int startIndex);
        [DllImport(Dll)] public static extern int FPDFText_FindNext(IntPtr handle);
        [DllImport(Dll)] public static extern int FPDFText_GetSchResultIndex(IntPtr handle);
        [DllImport(Dll)] public static extern int FPDFText_GetSchCount(IntPtr handle);
        [DllImport(Dll)] public static extern void FPDFText_FindClose(IntPtr handle);
        [DllImport(Dll)] public static extern int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);
        [DllImport(Dll)] public static extern int FPDFText_GetRect(IntPtr textPage, int rectIndex, out double left, out double top, out double right, out double bottom);
    }
}

public enum PdfError : uint
{
    Success = 0, Unknown = 1, File = 2, Format = 3, Password = 4, Security = 5, Page = 6,
}

public sealed class PdfException(PdfError error) : Exception(error switch
{
    PdfError.File => "file not found or couldn't be opened",
    PdfError.Format => "not a PDF, or damaged",
    PdfError.Password => "password protected",
    PdfError.Security => "unsupported security scheme",
    PdfError.Page => "page not found or damaged",
    _ => "unknown error",
})
{
    public PdfError Error { get; } = error;
}
