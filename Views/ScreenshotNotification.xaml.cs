using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lupik.Localization;

namespace Lupik.Views;

public partial class ScreenshotNotification : Window
{
    private readonly Func<Task> _open;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(7) };
    private bool _opened;
    private bool _closed;
    internal ScreenshotNotification(BitmapSource image, bool copied, Func<Task> open)
    {
        InitializeComponent(); _open = open;
        Thumbnail.Source = MakeThumbnail(image);
        StatusText.Text = Loc.T(copied ? "capture.copied" : "capture.copyFailedShort");
        DimensionsText.Text = $"{image.PixelWidth} × {image.PixelHeight} px";
        InitializeLifetime();
    }

    internal ScreenshotNotification(string text,bool copied,Func<Task> copy)
    {
        InitializeComponent(); _open=copy;
        Thumbnail.Visibility=Visibility.Collapsed; TextPreview.Visibility=Visibility.Visible;
        TextPreview.Text=text.Length>450 ? text[..450]+"…" : text;
        StatusText.Text=Loc.T(text.Length==0 ? "capture.noText" : copied ? "capture.textCopied" : "capture.copyTextAgain");
        ActionText.Text=Loc.T(text.Length==0 ? "capture.noText" : "capture.copyTextAgain");
        DimensionsText.Text=Loc.T("capture.characterCount",text.Length);
        if(text.Length==0) {OpenButton.IsEnabled=false;TextPreview.Text=Loc.T("capture.noTextHint");}
        InitializeLifetime();
    }

    private void InitializeLifetime()
    {
        SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle,-20,GetWindowLong(handle,-20) | 0x08000080); // NOACTIVATE | TOOLWINDOW
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        };
        Loaded += (_, _) => { Place(); RenewLifetime(); };
        _timer.Tick += (_, _) => Dismiss();
        MouseEnter += (_, _) => RenewLifetime();
        MouseMove += (_, _) => RenewLifetime();
        MouseLeave += (_, _) => RenewLifetime();
        Closed += (_, _) => { _closed=true; _timer.Stop(); };
    }

    private void RenewLifetime()
    {
        if (_closed) return;
        _timer.Stop();
        _timer.Start();
    }

    private static BitmapSource MakeThumbnail(BitmapSource image)
    {
        double scale = Math.Min(1, Math.Min(640.0/image.PixelWidth,360.0/image.PixelHeight));
        var visual = new DrawingVisual();
        int width = Math.Max(1,(int)Math.Round(image.PixelWidth*scale)), height = Math.Max(1,(int)Math.Round(image.PixelHeight*scale));
        using(var dc=visual.RenderOpen()) dc.DrawImage(image,new Rect(0,0,width,height));
        var result = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); result.Render(visual); result.Freeze();
        return result;
    }

    internal async Task OpenAsync()
    {
        if (_opened) return;
        _opened = true;
        Dismiss();
        await _open();
    }
    private async void OnOpen(object sender,RoutedEventArgs e) => await OpenAsync();
    internal void Dismiss() { if (!_closed) Close(); }
    private void OnDismiss(object sender,RoutedEventArgs e) { e.Handled=true; Dismiss(); }

    private void Place()
    {
        var work = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? System.Windows.Forms.SystemInformation.VirtualScreen;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        double scale = GetDpiForWindow(handle)/96.0;
        int width=(int)Math.Round(Width*scale),height=(int)Math.Round(Height*scale),margin=(int)Math.Round(8*scale);
        SetWindowPos(handle,new IntPtr(-1),work.Right-width-margin,work.Bottom-height-margin,width,height,0x0010);
    }
    private IntPtr WindowMessage(IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled)
    {
        if(msg==0x0021) { handled=true; return new IntPtr(3); } // MA_NOACTIVATE
        if(msg==0x02E0) Dispatcher.BeginInvoke(Place);
        return IntPtr.Zero;
    }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd,int index,int value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
}
