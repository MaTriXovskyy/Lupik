using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lupik.Core;

using Lupik.Localization;

namespace Lupik;

/// <summary>Where the window goes and how big it is for each kind of file.</summary>
public partial class MainWindow
{
    /// <summary>Work area (in DIPs) of the monitor under the cursor, plus its DPI scale.</summary>
    private bool TryGetWorkArea(out Rect work, out double scaleX, out double scaleY)
    {
        NativeMethods.GetCursorPos(out var pt);
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO();
        mi.cbSize = Marshal.SizeOf(mi);

        var dpi = VisualTreeHelper.GetDpi(this);
        scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
        {
            work = Rect.Empty;
            return false;
        }

        work = new Rect(mi.rcWork.Left / scaleX, mi.rcWork.Top / scaleY,
                        mi.rcWork.Width / scaleX, mi.rcWork.Height / scaleY);
        return true;
    }

    private Rect ComputeDefaultBounds(string ext)
    {
        if (!TryGetWorkArea(out var work, out _, out _)) return new Rect(Left, Top, Width, Height);

        double w = Math.Min(1104, work.Width * 0.92);
        double h = Math.Min(810, work.Height * 0.92);

        if (ext == ".pdf" || Views.DocxViewer.CanOpen(ext))
        {
            // Pages: portrait (a .docx page is 816 px wide plus the dark margin around it)
            w = Math.Min(ext == ".pdf" ? 1012 : 920, work.Width * 0.90);
            h = Math.Min(1035, work.Height * 0.95);
        }

        double scale = Settings.Current.WindowScale; // "Window size" in Settings
        return Centered(work, Math.Max(w * scale, 552), Math.Max(h * scale, 414));
    }

    private Rect ComputeImageBounds(double imgWidth, double imgHeight)
    {
        if (imgWidth <= 0 || imgHeight <= 0) return ComputeDefaultBounds("");
        if (!TryGetWorkArea(out var work, out _, out _)) return new Rect(Left, Top, Width, Height);

        double maxW = work.Width * 0.95 * Settings.Current.WindowScale;
        double maxH = work.Height * 0.95 * Settings.Current.WindowScale;

        // Title bar + image footer (the key hints live in it) + the filmstrip, if shown
        double chrome = 38 + 36 + (ImageViewerControl.FilmstripShown ? Views.ImageViewer.FilmstripHeight : 0);
        double w = imgWidth;
        double h = imgHeight + chrome;

        if (w > maxW || h > maxH)
        {
            double scaleFactor = Math.Min(maxW / w, (maxH - chrome) / imgHeight);
            w = imgWidth * scaleFactor;
            h = (imgHeight * scaleFactor) + chrome;
        }

        return Centered(work, Math.Max(w, 552), Math.Max(h, 414));
    }

    // --- Remembered size and place, per kind of file

    /// <summary>The kind of the file shown (null: nothing, e.g. the welcome screen).</summary>
    private string? _boundsKind;

    private static string KindOf(string path)
    {
        if (Directory.Exists(path)) return "folder";
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(ImageExtensions, ext) >= 0) return "image";
        if (Array.IndexOf(Views.MediaViewer.VideoExtensions, ext) >= 0) return "video";
        if (Array.IndexOf(Views.MediaViewer.AudioExtensions, ext) >= 0) return "audio";
        if (ext is ".pdf" or ".ai") return "pdf";
        if (Views.DocxViewer.CanOpen(ext)) return "document";
        if (Views.CsvViewer.IsWorkbook(ext) || Array.IndexOf(Views.CsvViewer.Extensions, ext) >= 0) return "table";
        if (Array.IndexOf(Views.ArchiveViewer.Extensions, ext) >= 0) return "archive";
        if (Array.IndexOf(CodeExtensions, ext) >= 0) return "text";
        return "other";
    }

    /// <summary>The user moved or resized the preview: that's where this kind of file opens from now on.</summary>
    private void RememberBounds()
    {
        if (_pinned || _isFullScreen || _boundsKind == null || !Settings.Current.RememberBounds || WindowState != WindowState.Normal) return;
        var saved = new SavedBounds(Math.Round(Left), Math.Round(Top), Math.Round(Width), Math.Round(Height));
        string kind = _boundsKind;
        Settings.Update(s => s.WindowBounds[kind] = saved);
        App.Log($"[MainWindow] Remembered bounds for {kind}: {saved}");
    }

    /// <summary>The remembered place for the current kind of file, if there is one and it's still on a screen.</summary>
    private Rect? RememberedBounds()
    {
        if (_pinned || _boundsKind == null || !Settings.Current.RememberBounds ||
            !Settings.Current.WindowBounds.TryGetValue(_boundsKind, out var b) || b.Width < 200 || b.Height < 150) return null;
        // At least the middle of the title bar must be on some screen (monitors can be unplugged)
        var dpi = VisualTreeHelper.GetDpi(this);
        var probe = new System.Drawing.Point((int)((b.Left + b.Width / 2) * dpi.DpiScaleX), (int)((b.Top + 16) * dpi.DpiScaleY));
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            if (screen.WorkingArea.Contains(probe)) return new Rect(b.Left, b.Top, b.Width, b.Height);
        return null;
    }

    private static Rect Centered(Rect work, double w, double h) =>
        new(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h);

    /// <summary>
    /// Moves and resizes the window in a single native call. Setting Left/Top/Width/Height one by one
    /// makes the window visibly jump through intermediate states.
    /// </summary>
    private void SetBounds(Rect bounds)
    {
        if (_isFullScreen) return; // full screen keeps covering the monitor while switching files
        if (_pinned && IsVisible) return; // a pinned window stays where it was put
        if (RememberedBounds() is Rect remembered) bounds = remembered;

        if (Math.Abs(Left - bounds.Left) < 0.5 && Math.Abs(Top - bounds.Top) < 0.5 &&
            Math.Abs(Width - bounds.Width) < 0.5 && Math.Abs(Height - bounds.Height) < 0.5)
            return;

        if (!IsVisible || Hwnd == IntPtr.Zero)
        {
            Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        SetWindowPos(Hwnd, IntPtr.Zero,
            (int)Math.Round(bounds.Left * dpi.DpiScaleX), (int)Math.Round(bounds.Top * dpi.DpiScaleY),
            (int)Math.Round(bounds.Width * dpi.DpiScaleX), (int)Math.Round(bounds.Height * dpi.DpiScaleY),
            SWP_NOZORDER | SWP_NOACTIVATE);

        // Keep WPF's own properties in sync with the native size
        Width = bounds.Width;
        Height = bounds.Height;
    }

    /// <summary>Audio: just the cover and the transport bar, no need for a big window.</summary>
    private Rect ComputeAudioBounds()
    {
        if (!TryGetWorkArea(out var work, out _, out _)) return new Rect(Left, Top, Width, Height);
        return Centered(work, Math.Min(644, work.Width * 0.9), Math.Min(483, work.Height * 0.9));
    }
}
