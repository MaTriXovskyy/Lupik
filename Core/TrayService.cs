using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace QuickPeek.Core;

public class TrayService : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private ToolStripMenuItem? _autostartMenuItem;
    private readonly MainWindow _ownerWindow;

    public event Action? DoubleClicked;

    public TrayService(MainWindow ownerWindow)
    {
        _ownerWindow = ownerWindow;
    }

    public void Initialize()
    {
        try
        {
            _notifyIcon = new NotifyIcon
            {
                Text = "QuickPeek (Spacja = podgląd)",
                Icon = LoadAppIcon(),
                Visible = true
            };

            var contextMenu = new ContextMenuStrip();

            var statusItem = new ToolStripMenuItem("QuickPeek: Aktywny")
            {
                Enabled = false,
                Font = new Font(contextMenu.Font, System.Drawing.FontStyle.Bold)
            };
            contextMenu.Items.Add(statusItem);
            contextMenu.Items.Add(new ToolStripSeparator());

            var openItem = new ToolStripMenuItem("Otwórz podgląd");
            openItem.Click += (s, e) => DoubleClicked?.Invoke();
            contextMenu.Items.Add(openItem);

            AddSampleItem(contextMenu, "Pokaż próbkę grafiki", "sample_image.png");
            AddSampleItem(contextMenu, "Pokaż próbkę kodu", "sample_code.cs");
            AddSampleItem(contextMenu, "Pokaż próbkę PDF", "sample_document.pdf");

            contextMenu.Items.Add(new ToolStripSeparator());

            // --- Settings ---
            _autostartMenuItem = new ToolStripMenuItem("Uruchamiaj przy starcie Windows")
            {
                Checked = IsAutostartEnabled()
            };
            _autostartMenuItem.Click += (s, e) => ToggleAutostart();
            contextMenu.Items.Add(_autostartMenuItem);

            var spaceItem = new ToolStripMenuItem("Spacja w Eksploratorze otwiera podgląd")
            {
                Checked = Settings.Current.SpaceInExplorer
            };
            spaceItem.Click += (s, e) =>
            {
                Settings.Update(st => st.SpaceInExplorer = !st.SpaceInExplorer);
                spaceItem.Checked = Settings.Current.SpaceInExplorer;
            };
            contextMenu.Items.Add(spaceItem);

            var hotkeyMenu = new ToolStripMenuItem("Skrót globalny");
            foreach (var (label, value) in new[]
                     {
                         ("Ctrl+Spacja", GlobalHotkey.CtrlSpace),
                         ("Ctrl+Alt+Spacja", GlobalHotkey.CtrlAltSpace),
                         ("Wyłączony", GlobalHotkey.None),
                     })
            {
                var option = new ToolStripMenuItem(label) { Checked = Settings.Current.Hotkey == value };
                option.Click += (s, e) =>
                {
                    Settings.Update(st => st.Hotkey = value);
                    foreach (ToolStripMenuItem other in hotkeyMenu.DropDownItems) other.Checked = other == option;
                };
                hotkeyMenu.DropDownItems.Add(option);
            }
            contextMenu.Items.Add(hotkeyMenu);
            contextMenu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Wyjście");
            exitItem.Click += (s, e) => Application.Current.Shutdown();
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.DoubleClick += (s, e) =>
            {
                DoubleClicked?.Invoke();
            };

            App.Log("[TrayService] NotifyIcon initialized successfully and is visible in system tray.");
        }
        catch (Exception ex)
        {
            App.Log($"[TrayService] Error initializing NotifyIcon: {ex}");
        }
    }

    private void AddSampleItem(ContextMenuStrip menu, string label, string fileName)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += (s, e) =>
        {
            string sample = Path.Combine(AppContext.BaseDirectory, "test_samples", fileName);
            if (File.Exists(sample))
                _ownerWindow.Dispatcher.InvokeAsync(() => _ownerWindow.ShowFile(sample));
        };
        menu.Items.Add(item);
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

    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("QuickPeek") != null;
        }
        catch
        {
            return false;
        }
    }

    private void ToggleAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            if (IsAutostartEnabled())
            {
                key.DeleteValue("QuickPeek", false);
                if (_autostartMenuItem != null) _autostartMenuItem.Checked = false;
            }
            else
            {
                key.SetValue("QuickPeek", $"\"{exePath}\" --tray"); // --tray: start silently in the tray
                if (_autostartMenuItem != null) _autostartMenuItem.Checked = true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Nie udało się zmienić ustawienia autostartu:\n{ex.Message}", "QuickPeek");
        }
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
