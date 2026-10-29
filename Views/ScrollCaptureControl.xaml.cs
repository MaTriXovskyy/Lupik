using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Lupik.Core;
using Lupik.Localization;
using PixelRect=System.Drawing.Rectangle;

namespace Lupik.Views;

public partial class ScrollCaptureControl : Window
{
    private readonly PixelRect _area;
    private bool _closed,_excluded;
    internal event Action? Finished;
    internal event Action? Cancelled;
    internal ScrollCaptureControl(PixelRect area)
    {
        InitializeComponent(); _area=area;
        SourceInitialized+=(_,_)=>
        {
            IntPtr handle=new WindowInteropHelper(this).Handle;
            SetWindowLong(handle,-20,GetWindowLong(handle,-20)|0x08000080);
            _excluded=Environment.OSVersion.Version.Build>=19041 && SetWindowDisplayAffinity(handle,0x11);
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        };
        Loaded+=(_,_)=>Place();
        Closed+=(_,_)=>{_closed=true; Cancelled?.Invoke();};
    }
    internal void SetStatus(string key,int width,int height)
    {
        SizeText.Text=$"{width} × {height} px";
        StatusText.Text=Loc.T(key);
    }
    internal void Building() { FinishButton.IsEnabled=false; StatusText.Text=Loc.T("capture.scrollBuilding"); }
    internal void Dismiss() {if(!_closed) Close();}
    internal bool SafeToCapture
    {
        get
        {
            if(_excluded) return true;
            GetWindowRect(new WindowInteropHelper(this).Handle,out var r);
            return !_area.IntersectsWith(PixelRect.FromLTRB(r.Left,r.Top,r.Right,r.Bottom));
        }
    }
    private void OnFinish(object sender,RoutedEventArgs e)=>Finished?.Invoke();
    private void OnCancel(object sender,RoutedEventArgs e)=>Cancelled?.Invoke();
    private void Place()
    {
        if(_closed) return;
        IntPtr handle=new WindowInteropHelper(this).Handle;
        double scale=GetDpiForWindow(handle)/96.0;
        int width=(int)Math.Round(Width*scale),height=(int)Math.Round(Height*scale),pad=(int)Math.Round(12*scale);
        var screens=System.Windows.Forms.Screen.AllScreens.OrderByDescending(s=>s.Bounds.IntersectsWith(_area));
        var candidates=screens.SelectMany(s=>new[]{
            new PixelRect(s.WorkingArea.Right-width-pad,s.WorkingArea.Top+pad,width,height),
            new PixelRect(s.WorkingArea.Right-width-pad,s.WorkingArea.Bottom-height-pad,width,height),
            new PixelRect(s.WorkingArea.Left+pad,s.WorkingArea.Top+pad,width,height)}).ToArray();
        var position=candidates.FirstOrDefault(r=>!r.IntersectsWith(_area));
        if(position.Width==0) position=candidates[0];
        SetWindowPos(handle,new IntPtr(-1),position.X,position.Y,width,height,0x0010);
    }
    private IntPtr WindowMessage(IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled)
    {
        if(msg==0x0021) {handled=true;return new IntPtr(3);}
        if(msg==0x02E0) Dispatcher.BeginInvoke(Place);
        if(msg==0x007E) Cancelled?.Invoke();
        return IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd,out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd,uint affinity);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd,int index,int value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
}
