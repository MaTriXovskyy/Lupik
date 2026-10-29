using System.Windows;
using System.Windows.Controls;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik.Views;

public partial class ScreenshotToolbar : UserControl
{
    private bool _updating;
    internal event Action<ScreenshotMode>? ModePicked;
    internal event Action? TextToggleRequested;
    internal bool IsOverControls => IsVisible && (BrandHost.IsMouseOver || ModeHost.IsMouseOver || TextHost.IsMouseOver);
    public ScreenshotToolbar() : this(true) { }
    internal ScreenshotToolbar(bool multipleMonitors)
    {
        InitializeComponent();
        DesktopMode.Visibility = multipleMonitors ? Visibility.Visible : Visibility.Collapsed;
        Modes.Columns = multipleMonitors ? 6 : 5;
        foreach(RadioButton button in Modes.Children)
            button.ToolTip=Loc.T("capture.mode"+button.Tag)+" ("+button.ToolTip+")";
        SizeChanged+=(_,_)=>
        {
            // Keep branding, centered controls and T usable on narrow/high-DPI displays.
            foreach(RadioButton button in Modes.Children)
                if(button.Content is StackPanel panel)
                    foreach(TextBlock label in panel.Children.OfType<TextBlock>())
                        label.Visibility=ActualWidth<720 ? Visibility.Collapsed : Visibility.Visible;
        };
    }
    internal void SetMode(ScreenshotMode mode,bool textMode=false)
    {
        _updating = true;
        RegionMode.IsChecked = mode == ScreenshotMode.Region;
        WindowMode.IsChecked = mode == ScreenshotMode.Window;
        MonitorMode.IsChecked = mode == ScreenshotMode.Monitor;
        DesktopMode.IsChecked = mode == ScreenshotMode.Desktop;
        FreeformMode.IsChecked = mode == ScreenshotMode.Freeform;
        ScrollMode.IsChecked = mode == ScreenshotMode.Scroll;
        TextToggle.IsChecked=textMode;
        RegionLabel.Text=Loc.T(textMode ? "capture.modeText" : "capture.modeRegion");
        _updating = false;
    }
    private void OnTextToggle(object sender,RoutedEventArgs e) { if(!_updating) TextToggleRequested?.Invoke(); }
    private void OnModePicked(object sender, RoutedEventArgs e)
    {
        if (!_updating && sender is RadioButton { Tag: string mode } && Enum.TryParse<ScreenshotMode>(mode, out var value)) ModePicked?.Invoke(value);
    }
}
