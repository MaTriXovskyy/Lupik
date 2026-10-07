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
    public int PageCount { get; private set; }

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

    // ---------- Editing pages (edit mode) ----------

    /// <summary>Turns a page by quarter turns (+1 = 90° clockwise).</summary>
    public void RotatePage(int index, int quarterTurns)
    {
        lock (Lock)
        {
            EnsureOpen();
            IntPtr page = Native.FPDF_LoadPage(_doc, index);
            if (page == IntPtr.Zero) throw new PdfException((PdfError)Native.FPDF_GetLastError());
            try { Native.FPDFPage_SetRotation(page, ((Native.FPDFPage_GetRotation(page) + quarterTurns) % 4 + 4) % 4); }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    public void DeletePage(int index)
    {
        lock (Lock)
        {
            EnsureOpen();
            Native.FPDFPage_Delete(_doc, index);
            PageCount = Native.FPDF_GetPageCount(_doc);
        }
    }

    /// <summary>Adds all pages of another PDF at the end. Returns how many.</summary>
    public int AppendDocument(string otherPath)
    {
        lock (Lock)
        {
            EnsureOpen();
            IntPtr other = Native.FPDF_LoadDocument(otherPath, null);
            if (other == IntPtr.Zero) throw new PdfException((PdfError)Native.FPDF_GetLastError());
            try
            {
                int count = Native.FPDF_GetPageCount(other);
                if (Native.FPDF_ImportPages(_doc, other, null, PageCount) == 0) throw new PdfException(PdfError.Format);
                PageCount = Native.FPDF_GetPageCount(_doc);
                return count;
            }
            finally
            {
                Native.FPDF_CloseDocument(other);
            }
        }
    }

    /// <summary>Saves the pages in <paramref name="pageRange"/> ("1,3,5-7", 1-based) as a new PDF.</summary>
    public void ExtractPages(string pageRange, string targetPath)
    {
        lock (Lock)
        {
            EnsureOpen();
            IntPtr created = Native.FPDF_CreateNewDocument();
            try
            {
                if (Native.FPDF_ImportPages(created, _doc, pageRange, 0) == 0) throw new PdfException(PdfError.Page);
                Write(created, targetPath);
            }
            finally
            {
                Native.FPDF_CloseDocument(created);
            }
        }
    }

    /// <summary>Saves the document as it is now (with the edits) to <paramref name="targetPath"/>.</summary>
    public void SaveAs(string targetPath)
    {
        lock (Lock)
        {
            EnsureOpen();
            Write(_doc, targetPath);
        }
    }

    private static void Write(IntPtr doc, string targetPath)
    {
        using var output = new System.IO.FileStream(targetPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None);
        Exception? error = null;
        Native.WriteBlockProc write = (_, data, size) =>
        {
            try
            {
                var chunk = new byte[size];
                Marshal.Copy(data, chunk, 0, (int)size);
                output.Write(chunk, 0, chunk.Length);
                return 1;
            }
            catch (Exception ex)
            {
                error = ex;
                return 0;
            }
        };
        var writer = new Native.FPDF_FILEWRITE { version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(write) };
        // No incremental update: a clean, complete file (deleted pages are really gone)
        bool ok = Native.FPDF_SaveAsCopy(doc, ref writer, Native.FPDF_NO_INCREMENTAL) != 0;
        GC.KeepAlive(write);
        if (error != null) throw new System.IO.IOException(error.Message, error);
        if (!ok) throw new System.IO.IOException("PDFium couldn't save the document.");
    }

    /// <summary>
    /// Opens a PDF (or a PDF-compatible .ai). Throws <see cref="PdfException"/> when it can't.
    /// PDFium reads through Lupik's own handle, opened with delete/write sharing: FPDF_LoadDocument keeps the file
    /// open without it for as long as the document lives, so Explorer couldn't delete, move or rename a previewed PDF
    /// ("file is open in Lupik") until the idle cleanup or until Lupik quit.
    /// </summary>
    public static PdfDocument Open(string path, string? password = null)
    {
        var file = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete, 1 << 16);
        if (file.Length > uint.MaxValue) { file.Dispose(); throw new PdfException(PdfError.File); } // FPDF_FILEACCESS: 32-bit length
        var reader = new BlockReader(file);
        IntPtr access = Marshal.AllocHGlobal(Marshal.SizeOf<Native.FPDF_FILEACCESS>());
        Marshal.StructureToPtr(new Native.FPDF_FILEACCESS
        {
            m_FileLen = (uint)file.Length,
            m_GetBlock = Marshal.GetFunctionPointerForDelegate(reader.Proc),
            m_Param = IntPtr.Zero,
        }, access, false);
        lock (Lock)
        {
            IntPtr doc = Native.FPDF_LoadCustomDocument(access, password);
            if (doc == IntPtr.Zero)
            {
                var error = (PdfError)Native.FPDF_GetLastError();
                Marshal.FreeHGlobal(access);
                reader.Dispose();
                throw new PdfException(error);
            }
            return new PdfDocument(path, doc) { _access = access, _reader = reader };
        }
    }

    // PDFium reads the file through these for as long as the document is open
    private IntPtr _access;
    private BlockReader? _reader;

    private sealed class BlockReader : IDisposable
    {
        public readonly Native.GetBlockProc Proc; // kept alive: PDFium calls into it
        private readonly System.IO.FileStream _file;
        private byte[] _buffer = Array.Empty<byte>();

        public BlockReader(System.IO.FileStream file)
        {
            _file = file;
            Proc = Read;
        }

        /// <summary>PDFium wants <paramref name="size"/> bytes from <paramref name="position"/>; 1 = done, 0 = failed.</summary>
        private int Read(IntPtr param, uint position, IntPtr target, uint size)
        {
            try
            {
                if (_buffer.Length < size) _buffer = new byte[size];
                _file.Position = position;
                int done = 0;
                while (done < size)
                {
                    int read = _file.Read(_buffer, done, (int)size - done);
                    if (read == 0) return 0;
                    done += read;
                }
                Marshal.Copy(_buffer, 0, target, (int)size);
                return 1;
            }
            catch (Exception ex)
            {
                App.Log($"[Pdfium] Reading the file failed: {ex.Message}");
                return 0;
            }
        }

        public void Dispose() => _file.Dispose();
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
            if (_access != IntPtr.Zero) Marshal.FreeHGlobal(_access);
            _access = IntPtr.Zero;
            _reader?.Dispose();
            _reader = null;
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
        // FPDF_FILEACCESS: the length is a C "unsigned long", 32 bits on Windows
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int GetBlockProc(IntPtr param, uint position, IntPtr buffer, uint size);
        [StructLayout(LayoutKind.Sequential)]
        public struct FPDF_FILEACCESS { public uint m_FileLen; public IntPtr m_GetBlock; public IntPtr m_Param; }
        [DllImport(Dll)] public static extern IntPtr FPDF_LoadCustomDocument(IntPtr fileAccess, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
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

        // Editing and saving
        public const uint FPDF_NO_INCREMENTAL = 2;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int WriteBlockProc(IntPtr self, IntPtr data, uint size);
        [StructLayout(LayoutKind.Sequential)]
        public struct FPDF_FILEWRITE { public int version; public IntPtr WriteBlock; }
        [DllImport(Dll)] public static extern int FPDFPage_GetRotation(IntPtr page);
        [DllImport(Dll)] public static extern void FPDFPage_SetRotation(IntPtr page, int rotate);
        [DllImport(Dll)] public static extern void FPDFPage_Delete(IntPtr document, int index);
        [DllImport(Dll)] public static extern IntPtr FPDF_CreateNewDocument();
        [DllImport(Dll)] public static extern int FPDF_ImportPages(IntPtr dest, IntPtr src, [MarshalAs(UnmanagedType.LPStr)] string? pageRange, int index);
        [DllImport(Dll)] public static extern int FPDF_SaveAsCopy(IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags);

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
