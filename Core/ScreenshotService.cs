using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Lupik.Localization;
using Lupik.Views;

namespace Lupik.Core;

internal sealed class ScreenshotService(MainWindow preview)
{
    private ScreenshotSession? _session;
    private ScreenshotNotification? _notification;
    private ScrollCaptureSession? _scroll;
    private (string Path, IntPtr Origin, bool Copied)? _last;
    private bool _busy, _disposed;

    internal void PresentCapture(string path, BitmapSource image, IntPtr origin, bool copied)
    {
        if (_disposed) return;
        _notification?.Dismiss();
        _last = (path,origin,copied);
        _notification = new ScreenshotNotification(image,copied,()=>OpenAsync(path,origin,copied));
        _notification.Show();
    }

    internal bool HasLast => _last is { } last && System.IO.File.Exists(last.Path);
    internal void PresentText(string text,bool copied)
    {
        if(_disposed) return;
        _notification?.Dismiss();
        _notification=new ScreenshotNotification(text,copied,async()=>{await CopyTextAsync(text);});
        _notification.Show();
    }
    internal Task OpenLastAsync()
    {
        _notification?.Dismiss();
        return _last is { } last ? OpenAsync(last.Path,last.Origin,last.Copied) : Task.CompletedTask;
    }
    private async Task OpenAsync(string path,IntPtr origin,bool copied)
    {
        try { if (!_disposed && System.IO.File.Exists(path)) await preview.ShowScreenshotAsync(path,origin,copied); }
        catch(Exception ex) { App.Log($"[Screenshot] Open failed: {ex}"); }
    }

    internal async Task CaptureAsync()
    {
        if(_scroll!=null) {_scroll.Complete();return;}
        if (_busy || _disposed) return;
        _busy = true;
        IntPtr origin = NativeMethods.GetForegroundWindow();
        try
        {
            _notification?.Dismiss(); _notification = null;
            DwmFlush();
            var monitors = System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds).ToArray();
            var windows = ScreenshotWindows.Snapshot();
            var desktop = monitors.Aggregate(Rectangle.Union);
            var bitmap = await Task.Run(() => CaptureDesktop(desktop));
            if (_disposed) return;
            var mode = ScreenshotModes.Restore(Settings.Current.ScreenCaptureMode);
            var session = _session = new ScreenshotSession(bitmap, desktop, monitors, windows, mode);
            var selected = await session.SelectAsync();
            _session = null;
            if (_disposed) return;
            if (selected == null)
            {
                if (session.RestoreFocusOnCancel && origin != IntPtr.Zero) NativeMethods.SetForegroundWindow(origin);
                return;
            }
            if (origin != IntPtr.Zero) NativeMethods.SetForegroundWindow(origin);
            BitmapSource? selection;
            if(selected.Mode==ScreenshotMode.Scroll)
            {
                var center=new System.Drawing.Point(selected.Area.Left+selected.Area.Width/2,selected.Area.Top+selected.Area.Height/2);
                var target=ScreenshotWindows.At(windows,center);
                if(target==null)
                {MessageBox.Show(Loc.T("capture.scrollNeedsWindow"),"Lupik",MessageBoxButton.OK,MessageBoxImage.Information);return;}
                origin=target.Handle; NativeMethods.SetForegroundWindow(origin); DwmFlush();
                _scroll=new ScrollCaptureSession(selected.Area,target,CaptureDesktop);
                selection=await _scroll.RunAsync();
                _scroll.Dispose(); _scroll=null;
                if(selection==null || _disposed) return;
            }
            else
            {
                selection = selected.Window is { } window ? await ScreenshotWindowImage.ReadAsync(window) : null;
                selection ??= selected.Outline is { Count: >= 3 } outline
                    ? ScreenshotFreeform.Crop(bitmap,desktop,selected.Area,outline) : Crop(bitmap, desktop, selected.Area);
            }
            if(selected.TextOnly)
            {
                string text=await ScreenshotText.ReadAsync(selection);
                if(_disposed) return;
                bool textCopied=text.Length>0 && await CopyTextAsync(text);
                PresentText(text,textCopied);
                return;
            }
            bool copied = await CopyAsync(selection,selected.Mode==ScreenshotMode.Freeform);
            string path = await ScreenshotStore.SaveAsync(selection);
            if (_disposed) return;
            App.Log($"[Screenshot] Captured {selection.PixelWidth}x{selection.PixelHeight}, clipboard={copied}");
            PresentCapture(path,selection,origin,copied);
        }
        catch (Exception ex)
        {
            App.Log($"[Screenshot] Capture failed: {ex}");
            if (!_disposed) MessageBox.Show(Loc.T("capture.failed"), "Lupik", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _session?.Cancel(); _session = null; _scroll?.Dispose();_scroll=null; _busy = false; }
    }

    internal void Dispose() { _disposed = true; _session?.Cancel(); _scroll?.Dispose();_notification?.Dismiss(); }

    internal static BitmapSource CaptureDesktop(Rectangle bounds)
    {
        using var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(System.Drawing.Color.Black);
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                IntPtr target = graphics.GetHdc();
                try
                {
                    // Native BitBlt accepts SRCCOPY | CAPTUREBLT, including layered windows.
                    // Graphics.CopyFromScreen rejects that combined raster-operation value.
                    if (!BitBlt(target, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x40CC0020))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { graphics.ReleaseHdc(target); }
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }
        IntPtr handle = image.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally { DeleteObject(handle); }
    }

    internal static BitmapSource Crop(BitmapSource desktop, Rectangle bounds, Rectangle selection)
    {
        selection.Intersect(bounds);
        if (selection.Width < 1 || selection.Height < 1) throw new ArgumentOutOfRangeException(nameof(selection));
        var rect = new Int32Rect(selection.X - bounds.X, selection.Y - bounds.Y, selection.Width, selection.Height);
        int stride = checked((rect.Width * desktop.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[checked(stride * rect.Height)];
        desktop.CopyPixels(rect, pixels, stride, 0);
        // Copy the pixels: a small selection must not retain a full multi-monitor screenshot.
        var result = BitmapSource.Create(rect.Width, rect.Height, 96, 96, desktop.Format, desktop.Palette, pixels, stride);
        result.Freeze();
        return result;
    }

    private static async Task<bool> CopyAsync(BitmapSource bitmap,bool preserveAlpha=false)
    {
        var png=preserveAlpha ? await Task.Run(()=>ScreenshotFreeform.Png(bitmap)) : null;
        var data=ScreenshotFreeform.ClipboardData(bitmap,png);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(data,copy:true); return true; }
            catch (ExternalException) { await Task.Delay(30); }
        }
        return false;
    }

    private static async Task<bool> CopyTextAsync(string text)
    {
        for(int attempt=0;attempt<5;attempt++)
        {
            try {Clipboard.SetText(text);return true;}
            catch(ExternalException) {await Task.Delay(30);}
        }
        return false;
    }

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint operation);
}
