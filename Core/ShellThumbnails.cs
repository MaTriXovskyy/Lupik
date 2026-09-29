using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lupik.Core;

/// <summary>
/// Thumbnails the way Explorer shows them (same shell cache: fast, and icons for folders/other files).
/// Shell thumbnail APIs need an STA thread, so requests run on one dedicated worker thread, in order.
/// Formats Windows can't thumbnail (PSD, HEIC, AVIF) are decoded with Magick.NET afterwards.
/// </summary>
public static class ShellThumbnails
{
    private static readonly string[] MagickOnly = { ".psd", ".heic", ".heif", ".avif" };

    private sealed record ThumbnailJob(string Path, int Size, int Generation, Action<BitmapSource> Done, bool UseMagick);

    private static readonly BlockingCollection<ThumbnailJob> Queue = new();
    private static int _generation;

    static ShellThumbnails()
    {
        var worker = new Thread(Work) { IsBackground = true, Name = "Lupik thumbnails" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static int CurrentGeneration => Volatile.Read(ref _generation);

    /// <summary>Drops all pending requests (e.g. the user left the folder).</summary>
    public static int NewGeneration() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Queues a thumbnail. <paramref name="done"/> is called on the worker thread with a frozen bitmap
    /// (possibly twice: shell icon first, then the real picture for PSD/HEIC).
    /// </summary>
    public static void Request(string path, int size, int generation, Action<BitmapSource> done)
    {
        Queue.Add(new ThumbnailJob(path, size, generation, done, UseMagick: false));
    }

    private static void Work()
    {
        foreach (var request in Queue.GetConsumingEnumerable())
        {
            if (request.Generation != Volatile.Read(ref _generation)) continue; // stale: user moved on

            try
            {
                var bitmap = request.UseMagick ? FromMagick(request.Path, request.Size) : FromShell(request.Path, request.Size);
                if (bitmap != null && request.Generation == Volatile.Read(ref _generation)) request.Done(bitmap);
            }
            catch (Exception ex)
            {
                App.Log($"[ShellThumbnails] {Path.GetFileName(request.Path)}: {ex.Message}");
            }

            // Explorer only has an app icon for these; follow up with the actual picture (slower, so queued last)
            if (!request.UseMagick && Array.IndexOf(MagickOnly, Path.GetExtension(request.Path).ToLowerInvariant()) >= 0)
                Queue.Add(request with { UseMagick = true });
        }
    }

    private static BitmapSource? FromMagick(string path, int size)
    {
        using var image = new ImageMagick.MagickImage(path, Views.ImageViewer.FirstImageOnly);
        image.AutoOrient();
        image.Thumbnail(new ImageMagick.MagickGeometry((uint)size, (uint)size));
        return Views.ImageViewer.ToBitmapSource(image);
    }

    /// <summary>
    /// Explorer's thumbnail only if it's already in the shell cache (fast), else null. Runs on its own STA thread,
    /// so it doesn't wait behind a folder's worth of queued requests.
    /// </summary>
    public static System.Threading.Tasks.Task<BitmapSource?> FromCacheAsync(string path, int size)
    {
        var result = new System.Threading.Tasks.TaskCompletionSource<BitmapSource?>();
        var thread = new Thread(() =>
        {
            try { result.SetResult(FromShell(path, size, cacheOnly: true)); }
            catch (Exception ex) { App.Log($"[ShellThumbnails] Cache lookup failed: {ex.Message}"); result.SetResult(null); }
        }) { IsBackground = true, Name = "Lupik cached thumbnail" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    // ---------- Shell interop ----------

    private static BitmapSource? FromShell(string path, int size, bool cacheOnly = false)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        if (hr != 0 || factory == null) return null;

        try
        {
            // The shell hands back real thumbnails top-down but icons bottom-up, and the bitmap header can't tell
            // them apart. So ask for each kind explicitly: a thumbnail if the file has one, otherwise its icon.
            const int ThumbnailOnly = 0x8, IconOnly = 0x4, InCacheOnly = 0x10, BiggerSizeOk = 0x1;
            var request = new NativeSize { Width = size, Height = size };

            if (cacheOnly)
                return factory.GetImage(request, ThumbnailOnly | InCacheOnly | BiggerSizeOk, out IntPtr cached) == 0
                    ? Convert(cached, flipRows: false) : null;

            if (factory.GetImage(request, ThumbnailOnly, out IntPtr thumbnail) == 0)
                return Convert(thumbnail, flipRows: false);
            if (factory.GetImage(request, IconOnly, out IntPtr icon) == 0)
                return Convert(icon, flipRows: true);
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Copies the shell's 32-bit DIB with its alpha channel (Imaging.CreateBitmapSourceFromHBitmap drops alpha,
    /// which puts black boxes behind folder and file icons).
    /// </summary>
    private static BitmapSource? Convert(IntPtr hBitmap, bool flipRows)
    {
        try
        {
            if (GetObject(hBitmap, Marshal.SizeOf<DIBSECTION>(), out DIBSECTION dib) == 0) return null;
            var info = dib.dsBm;
            if (info.bmBits == IntPtr.Zero || info.bmBitsPixel != 32) return null;

            int width = info.bmWidth, height = Math.Abs(info.bmHeight), stride = info.bmWidthBytes;
            var pixels = new byte[stride * height];
            Marshal.Copy(info.bmBits, pixels, 0, pixels.Length);

            if (flipRows)
            {
                var flipped = new byte[pixels.Length];
                for (int y = 0; y < height; y++)
                    Buffer.BlockCopy(pixels, y * stride, flipped, (height - 1 - y) * stride, stride);
                pixels = flipped;
            }

            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfield0, dsBitfield1, dsBitfield2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr hObject, int size, out DIBSECTION section);
}
