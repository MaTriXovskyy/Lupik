using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>The tray icon's right-click menu, in Lupik's style (instead of the plain Windows one).</summary>
public partial class TrayMenu : Window
{
    private static TrayMenu? _open;
    private readonly Action _openPreview;
    private bool _closing;

    private TrayMenu(Action openPreview)
    {
        InitializeComponent();
        _openPreview = openPreview;
        VersionText.Text = Loc.T("settings.version", Updater.CurrentVersion);
        if (Updater.AvailableVersion is { } version)
        {
            UpdateItem.Visibility = Visibility.Visible;
            UpdateText.Text = Loc.T("tray.update", version);
        }
    }

    /// <summary>Opens the menu next to the mouse (where the tray icon was clicked).</summary>
    public static void ShowAtCursor(Action openPreview)
    {
        _open?.Close();
        var menu = _open = new TrayMenu(openPreview);
        menu.Closed += (_, _) => { if (_open == menu) _open = null; };

        // Place it off-screen first so the size (and the monitor's DPI) is known
        menu.Left = -10000;
        menu.Top = -10000;
        menu.Show();
        menu.PlaceAtCursor();
        menu.Activate();
        SetForegroundWindow(new WindowInteropHelper(menu).Handle); // so a click elsewhere closes it
        menu.Animate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private void PlaceAtCursor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var work = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;

        // Card corner sits at the cursor; the 20 px margin around it holds the shadow
        const double shadow = 20;
        double w = ActualWidth, h = ActualHeight;
        double left = cursor.X / sx - w + shadow;
        double top = cursor.Y / sy - h + shadow;
        Left = Math.Clamp(left, work.Left / sx - shadow, work.Right / sx - w + shadow);
        Top = Math.Clamp(top, work.Top / sy - shadow, work.Bottom / sy - h + shadow);
    }

    private void Animate()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(140);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        CardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, duration) { EasingFunction = ease });
    }

    private void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void OnDeactivated(object? sender, EventArgs e) => CloseMenu();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseMenu(); e.Handled = true; }
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _openPreview();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        SettingsWindow.ShowOrActivate();
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _ = Updater.OfferAsync(null);
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        Application.Current.Shutdown();
    }
}
