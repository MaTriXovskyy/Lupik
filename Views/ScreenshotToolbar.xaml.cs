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
        {
            string key=(string)button.ToolTip; // the number key that picks this mode
            button.ToolTip=Loc.T("capture.mode"+button.Tag)+" ("+key+")";
            if(button.Content is StackPanel panel) panel.Children.Add(KeyBadge(key));
        }
        // The logo and the text toggle are their own pills, centred on the mode bar
        void Centre()
        {
            System.Windows.Controls.Canvas.SetTop(BrandHost,Math.Max(0,(ModeHost.ActualHeight-BrandHost.ActualHeight)/2));
            System.Windows.Controls.Canvas.SetTop(TextHost,Math.Max(0,(ModeHost.ActualHeight-TextHost.ActualHeight)/2));
        }
        ModeHost.SizeChanged+=(_,_)=>Centre();
        BrandHost.SizeChanged+=(_,_)=>Centre();
        TextHost.SizeChanged+=(_,_)=>Centre();
        SizeChanged+=(_,_)=>
        {
            // Keep branding, centered controls and T usable on narrow/high-DPI displays.
            foreach(RadioButton button in Modes.Children)
                if(button.Content is StackPanel panel)
                    foreach(TextBlock label in panel.Children.OfType<TextBlock>())
                        label.Visibility=ActualWidth<720 ? Visibility.Collapsed : Visibility.Visible;
        };
    }
    /// <summary>The key on the button itself, so the number shortcuts can be seen (they stay when labels hide).</summary>
    private static Border KeyBadge(string key)
    {
        var text=new TextBlock{Text=key,FontSize=10,HorizontalAlignment=HorizontalAlignment.Center};
        text.SetResourceReference(TextBlock.ForegroundProperty,"TextMuted");
        var badge=new Border{Child=text,CornerRadius=new CornerRadius(4),Padding=new Thickness(5,0,5,1),Margin=new Thickness(0,4,0,0),HorizontalAlignment=HorizontalAlignment.Center};
        badge.SetResourceReference(Border.BackgroundProperty,"c2F2B27");
        return badge;
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
