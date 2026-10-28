using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Lupik.Core;

internal static class ScreenshotWindowImage
{
    private static Task<BitmapSource?>? _pending;

    internal static async Task<BitmapSource?> ReadAsync(ScreenshotWindow window)
    {
        // PrintWindow can wait on another application. Never stall capture or accumulate
        // blocked workers when an application is hung; the frozen desktop remains a fallback.
        if (_pending is { IsCompleted: false }) return null;
        var reading = _pending = Task.Run(() => Read(window));
        return await Task.WhenAny(reading, Task.Delay(350)) == reading ? await reading : null;
    }

    private static BitmapSource? Read(ScreenshotWindow window)
    {
        try
        {
            if (!ScreenshotWindows.StillExists(window)) return null;
            var bounds = window.NativeBounds;
            if (bounds.Width < 2 || bounds.Height < 2 || (long)bounds.Width * bounds.Height > 100_000_000) return null;
            using var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(image))
            {
                IntPtr dc = graphics.GetHdc();
                try { if (!PrintWindow(window.Handle, dc, 2)) return null; }
                finally { graphics.ReleaseHdc(dc); }
            }
            // Some accelerated windows report success but provide an empty bitmap.
            bool blank = true;
            for (int y = 1; y < 5 && blank; y++) for (int x = 1; x < 5; x++)
            {
                var c = image.GetPixel(x * image.Width / 5, y * image.Height / 5);
                if (c.R + c.G + c.B > 3) { blank = false; break; }
            }
            if (blank) return null;
            IntPtr handle = image.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(handle,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return ScreenshotService.Crop(source,bounds,Rectangle.Intersect(window.Bounds,bounds));
            }
            finally { DeleteObject(handle); }
        }
        catch (Exception ex) { App.Log($"[Screenshot] Window image unavailable: {ex.Message}"); return null; }
    }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
