using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Lupik.Core;

namespace Lupik;

/// <summary>
/// The preview window's frame: light or dark title-bar hints for Windows, and the Mica / acrylic backdrop (Windows 11),
/// for which the window's own surfaces turn see-through.
/// </summary>
public partial class MainWindow
{
    /// <summary>System backdrops need Windows 11 22H2 (build 22621) or newer.</summary>
    public static bool BackdropSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>
    /// The window's surfaces (keyed like the palette, by their dark value) and how opaque they stay over the backdrop:
    /// the page itself fully clear, bars a little tinted, text areas mostly solid so text stays easy to read.
    /// </summary>
    private static readonly (string Key, uint Rgb, byte Alpha)[] Translucent =
    {
        ("c131211", 0x131211, 0x00), ("Base", 0x131211, 0x00),
        ("c1B1917", 0x1B1917, 0x70), ("Raised", 0x1B1917, 0x70),
        ("c171513", 0x171513, 0x70), ("c181614", 0x181614, 0x80),
        ("c121110", 0x121110, 0x80), ("c0E0D0C", 0x0E0D0C, 0x80),
        ("c161412", 0x161412, 0xC8),
    };

    private bool _backdropOn;

    private void WatchAppearance()
    {
        Accent.Changed += ApplyAppearance; // theme switch: dark/light hints and the see-through surfaces' colors
        Settings.Changed += OnSettingsChangedForBackdrop;
        Closed += (_, _) =>
        {
            Accent.Changed -= ApplyAppearance;
            Settings.Changed -= OnSettingsChangedForBackdrop;
        };
    }

    private string _appliedBackdrop = "";

    private void OnSettingsChangedForBackdrop()
    {
        if (Settings.Current.Backdrop != _appliedBackdrop) Dispatcher.BeginInvoke(ApplyAppearance);
    }

    private void ApplyAppearance()
    {
        if (Hwnd == IntPtr.Zero) return;
        _appliedBackdrop = Settings.Current.Backdrop;
        string backdrop = BackdropSupported ? _appliedBackdrop : "none";
        bool on = backdrop is "mica" or "acrylic";

        try
        {
            int dark = Palette.IsLight ? 0 : 1;
            NativeMethods.DwmSetWindowAttribute(Hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            if (BackdropSupported)
            {
                int type = on ? (backdrop == "mica" ? DWMSBT_MAINWINDOW : DWMSBT_TRANSIENTWINDOW) : DWMSBT_NONE;
                NativeMethods.DwmSetWindowAttribute(Hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
            }
        }
        catch (Exception ex) { App.Log($"[MainWindow] DWM attributes failed: {ex.Message}"); }

        if (on != _backdropOn)
        {
            // The glass frame covering the whole window is what lets the backdrop show through the client area
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false, GlassFrameThickness = on ? new Thickness(-1) : new Thickness(0),
            });
            _backdropOn = on;
        }
        if (HwndSource.FromHwnd(Hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = on ? Colors.Transparent : Palette.Color(0x131211);

        foreach (var (key, rgb, alpha) in Translucent)
        {
            if (on) Resources[key] = Frozen(Palette.Color(rgb, alpha));
            else Resources.Remove(key);
        }

        // The backdrop is drawn as for an active window: the preview never takes the focus, and an "inactive"
        // Mica is just a flat color
        if (on) SendMessage(Hwnd, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
    }

    /// <summary>Keeps the backdrop in its active look (see ApplyAppearance). Called from the window hook.</summary>
    private IntPtr KeepBackdropActive(IntPtr hwnd, int msg, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCACTIVATE || !_backdropOn) return IntPtr.Zero;
        handled = true;
        return DefWindowProc(hwnd, msg, (IntPtr)1, (IntPtr)(-1)); // -1: don't repaint the (hidden) standard frame
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1, DWMSBT_MAINWINDOW = 2, DWMSBT_TRANSIENTWINDOW = 3;
    private const int WM_NCACTIVATE = 0x0086;

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
