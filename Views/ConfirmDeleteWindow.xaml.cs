using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Lupik.Core;

using Lupik.Localization;
namespace Lupik.Views;

/// <summary>"Move to Recycle Bin?" card: shows what is about to go, Enter confirms, Esc cancels.</summary>
public partial class ConfirmDeleteWindow : Window
{
    private readonly string _path;

    private ConfirmDeleteWindow(string path)
    {
        InitializeComponent();
        _path = path;

        bool isFolder = Directory.Exists(path);
        NameText.Text = Path.GetFileName(path.TrimEnd('\\', '/'));
        NameText.ToolTip = path;
        HeadingText.Text = Loc.T(isFolder ? "delete.headingFolder" : "delete.heading");
        MetaText.Text = Describe(path, isFolder);

        // The shell thumbnail shows the picture itself for images, the file-type icon otherwise
        ShellThumbnails.Request(path, 144, ShellThumbnails.CurrentGeneration,
            bmp => Dispatcher.BeginInvoke(() => Thumb.Source = bmp));

        // Like the preview, the card never takes the focus from Explorer (that can stall for seconds);
        // Enter / Esc reach it through the keyboard hook (MainWindow.HandleHookKey)
        ShowActivated = false;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
            // Same 5 s UI Automation stall as the preview (see NativeMethods.RefuseAutomation)
            System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
            {
                NativeMethods.RefuseAutomation(msg, ref handled);
                return IntPtr.Zero;
            });
        };
        Loaded += (_, _) => Animate();
        Closed += (_, _) => { if (Current == this) Current = null; };
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>; true if the user confirmed.</summary>
    public static bool Confirm(Window owner, string path)
    {
        var dialog = new ConfirmDeleteWindow(path) { Owner = owner };
        Current = dialog;
        return dialog.ShowDialog() == true;
    }

    /// <summary>The open confirmation, if any (keys are routed to it).</summary>
    public static ConfirmDeleteWindow? Current { get; private set; }

    public void Answer(bool confirmed)
    {
        if (IsVisible) DialogResult = confirmed;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private static string Describe(string path, bool isFolder)
    {
        try
        {
            if (isFolder)
            {
                var dir = new DirectoryInfo(path);
                int files = 0, folders = 0;
                foreach (var entry in dir.EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo) folders++; else files++;
                    if (files + folders > 999) break;
                }
                return $"{Loc.T("delete.folderLabel")} · {Loc.Plural("count.files", files)}, {Loc.Plural("count.folders", folders)}";
            }
            var info = new FileInfo(path);
            string ext = info.Extension.TrimStart('.').ToUpperInvariant();
            return (ext.Length > 0 ? ext + " · " : "") + FormatSize(info.Length);
        }
        catch
        {
            return "";
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    private void Animate()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) { DialogResult = true; e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
