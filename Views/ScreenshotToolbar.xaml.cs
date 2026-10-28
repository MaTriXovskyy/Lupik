using System.Windows;
using System.Windows.Controls;
using Lupik.Core;

namespace Lupik.Views;

public partial class ScreenshotToolbar : UserControl
{
    private bool _updating;
    internal event Action<ScreenshotMode>? ModePicked;
    internal bool IsOverControls => BrandHost.IsMouseOver || ModeHost.IsMouseOver;
    public ScreenshotToolbar() : this(true) { }
    internal ScreenshotToolbar(bool multipleMonitors)
    {
        InitializeComponent();
        DesktopMode.Visibility = multipleMonitors ? Visibility.Visible : Visibility.Collapsed;
        Modes.Columns = multipleMonitors ? 4 : 3;
    }
    internal void SetMode(ScreenshotMode mode)
    {
        _updating = true;
        RegionMode.IsChecked = mode == ScreenshotMode.Region;
        WindowMode.IsChecked = mode == ScreenshotMode.Window;
        MonitorMode.IsChecked = mode == ScreenshotMode.Monitor;
        DesktopMode.IsChecked = mode == ScreenshotMode.Desktop;
        _updating = false;
    }
    private void OnModePicked(object sender, RoutedEventArgs e)
    {
        if (!_updating && sender is RadioButton { Tag: string mode } && Enum.TryParse<ScreenshotMode>(mode, out var value)) ModePicked?.Invoke(value);
    }
}
