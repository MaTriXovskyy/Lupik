using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Lupik.Localization;
using Lupik.Views;
using Application = System.Windows.Application;

namespace Lupik.Core;

public class TrayService : IDisposable
{
    private NotifyIcon? _notifyIcon;

    public event Action? DoubleClicked;

    public void Initialize()
    {
        try
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Visible = true
            };

            // Right click: Lupik's own menu (Views/TrayMenu) instead of the plain Windows one
            _notifyIcon.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                    Application.Current.Dispatcher.InvokeAsync(() => TrayMenu.ShowAtCursor(() => DoubleClicked?.Invoke()));
            };
            _notifyIcon.DoubleClick += (s, e) =>
            {
                DoubleClicked?.Invoke();
            };

            RefreshTexts();
            Loc.Instance.LanguageChanged += RefreshTexts; // also fires when the keys change
            Accent.Changed += RefreshIcon;

            App.Log("[TrayService] NotifyIcon initialized successfully and is visible in system tray.");
        }
        catch (Exception ex)
        {
            App.Log($"[TrayService] Error initializing NotifyIcon: {ex}");
        }
    }

    private void RefreshTexts()
    {
        if (_notifyIcon == null) return;
        if (!Application.Current.Dispatcher.CheckAccess()) { Application.Current.Dispatcher.BeginInvoke(RefreshTexts); return; }

        string tip = Loc.T("tray.tooltip", Loc.T("key.preview"));
        _notifyIcon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    /// <summary>The accent changed in Settings: the tray icon follows it.</summary>
    private void RefreshIcon()
    {
        if (_notifyIcon == null) return;
        var old = _notifyIcon.Icon;
        _notifyIcon.Icon = LoadAppIcon();
        old?.Dispose();
    }

    /// <summary>The app icon (same as the exe and taskbar), at the tray's small-icon size; drawn in the accent when it isn't the gold.</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            if (Accent.Color != Accent.Default && Application.Current.TryFindResource("AppLogo") is System.Windows.Media.ImageSource logo)
                return RenderIcon(logo, SystemInformation.SmallIconSize.Width);

            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (resource != null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[TrayService] Could not load app icon: {ex.Message}");
        }
        return (Icon)SystemIcons.Application.Clone(); // fallback
    }

    /// <summary>The vector logo as an icon: rendered to PNG and wrapped in a one-image .ico.</summary>
    private static Icon RenderIcon(System.Windows.Media.ImageSource logo, int size)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen()) dc.DrawImage(logo, new Rect(0, 0, size, size));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);

        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)1);                 // header: icon, one image
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write((int)png.Length); w.Write(22);                                   // data follows the 22-byte header
            w.Write(png.ToArray());
        }
        ico.Position = 0;
        return new Icon(ico);
    }

    public void Dispose()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }
}
