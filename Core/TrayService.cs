using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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

    public TrayService(MainWindow ownerWindow)
    {
    }

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

    public void ShowBalloonNotification(string title, string text)
    {
        try
        {
            _notifyIcon?.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            App.Log($"[TrayService] ShowBalloonNotification error: {ex.Message}");
        }
    }

    /// <summary>The app icon (same as the exe and taskbar), at the tray's small-icon size.</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
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
        return CreateModernIcon(); // fallback
    }

    private static Icon CreateModernIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Dark rounded pill
            using var bgBrush = new SolidBrush(Color.FromArgb(24, 24, 27));
            g.FillEllipse(bgBrush, 2, 2, 28, 28);

            // Cyan ring
            using var pen = new Pen(Color.FromArgb(56, 189, 248), 2.5f);
            g.DrawEllipse(pen, 7, 7, 18, 18);

            // Center eye pupil
            using var pupilBrush = new SolidBrush(Color.FromArgb(56, 189, 248));
            g.FillEllipse(pupilBrush, 12, 12, 8, 8);
        }

        IntPtr hIcon = bmp.GetHicon();
        return (Icon)Icon.FromHandle(hIcon).Clone();
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
