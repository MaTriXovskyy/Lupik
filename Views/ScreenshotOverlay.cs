using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lupik.Core;
using Lupik.Localization;
using PixelPoint = System.Drawing.Point;
using PixelRect = System.Drawing.Rectangle;

namespace Lupik.Views;

/// <summary>One frozen desktop and physical-pixel selection shared by all monitor overlays.</summary>
internal sealed class ScreenshotSession(BitmapSource bitmap, PixelRect desktop, PixelRect[] monitors,
    ScreenshotWindow[]? windows = null, ScreenshotMode initialMode = ScreenshotMode.Region)
{
    private readonly TaskCompletionSource<ScreenshotSelection?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ScreenshotOverlay> _windows = new();
    private PixelPoint? _start;
    private PixelPoint _pointer;
    private readonly List<PixelPoint> _outline = new();
    private StreamGeometry? _outlineGeometry;
    private double _pointSpacing=2;
    private bool _finished;

    internal ScreenshotMode Mode { get; private set; } = initialMode;
    internal bool TextMode { get; private set; }
    internal bool SelectionStarted { get; private set; }
    internal bool RestoreFocusOnCancel { get; private set; } = true;
    internal PixelRect? Selection => Mode switch
    {
        ScreenshotMode.Window => ScreenshotWindows.At(windows ?? [], _pointer)?.Bounds,
        ScreenshotMode.Monitor => monitors.FirstOrDefault(m => m.Contains(_pointer)) is { Width: > 0 } monitor ? monitor : null,
        ScreenshotMode.Desktop => desktop,
        ScreenshotMode.Freeform => _outline.Count>1 ? PixelRect.FromLTRB(_outline.Min(p=>p.X),_outline.Min(p=>p.Y),_outline.Max(p=>p.X),_outline.Max(p=>p.Y)) : null,
        _ => _start is { } start ? SelectionBetween(start, _pointer, desktop) : null
    };
    internal bool Dragging => _start != null;
    internal StreamGeometry OutlineGeometry => _outlineGeometry ??= ScreenshotFreeform.Geometry(_outline);

    internal Task<ScreenshotSelection?> SelectAsync(bool activate = true)
    {
        try
        {
            NativeMethods.GetCursorPos(out var cursor);
            _pointer = new(cursor.x, cursor.y);
            foreach (var monitor in monitors)
            {
                var window = new ScreenshotOverlay(this, bitmap, desktop, monitor, monitors.Length > 1);
                _windows.Add(window);
                window.Show();
                window.Place();
            }
            var active = _windows.FirstOrDefault(w => w.Monitor.Contains(_pointer)) ?? _windows.First();
            if (activate)
            {
                active.Activate();
                NativeMethods.SetForegroundWindow(active.Handle);
                active.Focus();
            }
        }
        catch { Cancel(); throw; }
        return _completion.Task;
    }

    internal static PixelRect SelectionBetween(PixelPoint start, PixelPoint end, PixelRect desktop)
    {
        int x1 = Math.Clamp(start.X, desktop.Left, desktop.Right), y1 = Math.Clamp(start.Y, desktop.Top, desktop.Bottom);
        int x2 = Math.Clamp(end.X, desktop.Left, desktop.Right), y2 = Math.Clamp(end.Y, desktop.Top, desktop.Bottom);
        return PixelRect.FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    internal void SetMode(ScreenshotMode mode, bool remember = true)
    {
        if (_finished) return;
        if(remember && ScreenshotModes.Remembered(mode) && Settings.Current.ScreenCaptureMode!=mode.ToString())
            Settings.Update(s=>s.ScreenCaptureMode=mode.ToString());
        if(Mode==mode) return;
        Mode = mode; _start = null; SelectionStarted=false; ResetOutline();
        if(mode!=ScreenshotMode.Region) TextMode=false;
        Redraw();
    }
    internal void ToggleTextMode()
    {
        if(_finished) return;
        SetMode(ScreenshotMode.Region,remember:false);
        TextMode=!TextMode;
        Redraw();
    }
    internal void InvokeMode(ScreenshotMode mode, PixelRect activeMonitor, bool remember = true)
    {
        if (_finished) return;
        SetMode(mode,remember);
        if (mode is ScreenshotMode.Monitor or ScreenshotMode.Desktop)
            Complete(new PixelPoint(activeMonitor.Left+activeMonitor.Width/2,activeMonitor.Top+activeMonitor.Height/2));
    }
    internal void Begin(PixelPoint point)
    {
        if (_finished) return;
        SelectionStarted=true;
        _pointer=point;
        if(Mode is ScreenshotMode.Region or ScreenshotMode.Freeform or ScreenshotMode.Scroll) _start=point;
        if(Mode==ScreenshotMode.Freeform) { ResetOutline(); AddPoint(point,force:true); }
        Redraw();
    }
    internal void Move(PixelPoint point)
    {
        if(_finished || (Mode is ScreenshotMode.Region or ScreenshotMode.Freeform or ScreenshotMode.Scroll) && _start==null) return;
        _pointer=point;
        if(Mode==ScreenshotMode.Freeform) AddPoint(point);
        Redraw();
    }
    internal void Complete(PixelPoint point)
    {
        if (_finished || (Mode is ScreenshotMode.Region or ScreenshotMode.Freeform or ScreenshotMode.Scroll) && _start == null) return;
        _pointer = point;
        if(Mode==ScreenshotMode.Freeform) AddPoint(point,force:true);
        if (Selection is not { Width: >= 2, Height: >= 2 } area ||
            Mode==ScreenshotMode.Freeform && (_outline.Count<3 || OutlineGeometry.GetArea()<4))
        { _start=null; SelectionStarted=false; ResetOutline(); Redraw(); return; }
        Finish(new ScreenshotSelection(Mode, area, Mode == ScreenshotMode.Window ? ScreenshotWindows.At(windows ?? [], _pointer) : null,
            Mode==ScreenshotMode.Freeform ? _outline.ToArray() : null,TextMode));
    }
    private void ResetOutline() { _outline.Clear(); _outlineGeometry=null; _pointSpacing=2; }
    private void AddPoint(PixelPoint point,bool force=false)
    {
        point=new PixelPoint(Math.Clamp(point.X,desktop.Left,desktop.Right),Math.Clamp(point.Y,desktop.Top,desktop.Bottom));
        if(_outline.Count>0)
        {
            var last=_outline[^1]; double dx=(double)point.X-last.X,dy=(double)point.Y-last.Y;
            if(dx==0 && dy==0 || !force && dx*dx+dy*dy<_pointSpacing*_pointSpacing) return;
        }
        if(_outline.Count>=16384)
        {
            // Bound tessellation work on exceptionally long gestures, retaining both ends.
            var reduced=_outline.Where((_,index)=>index%2==0).ToArray();
            _outline.Clear(); _outline.AddRange(reduced); _pointSpacing*=2;
        }
        _outline.Add(point); _outlineGeometry=null;
    }
    internal void Cancel() => Finish(null);
    private void Redraw() { foreach (var window in _windows) window.Redraw(); }

    private void Finish(ScreenshotSelection? area)
    {
        if (_finished) return;
        _finished = true;
        foreach (var window in _windows)
        {
            if (window.IsMouseCaptured) window.ReleaseMouseCapture();
            window.CloseOverlay();
        }
        _windows.Clear();
        _completion.TrySetResult(area);
    }

    internal void CheckForeground()
    {
        if (!_finished && !_windows.Any(w => w.Handle == NativeMethods.GetForegroundWindow()))
        { RestoreFocusOnCancel = false; Cancel(); }
    }
}

/// <summary>A separate opaque window per monitor keeps mixed-DPI selection in physical pixels.</summary>
internal sealed class ScreenshotOverlay : Window
{
    private readonly ScreenshotSession _session;
    private readonly Surface _surface;
    private readonly ScreenshotToolbar _toolbar;
    private bool _closed;
    private bool _pressArmed;
    private ScreenshotCursor? _captureCursor;
    private double _cursorScale;
    internal PixelRect Monitor { get; }
    internal IntPtr Handle { get; private set; }

    internal ScreenshotOverlay(ScreenshotSession session, BitmapSource desktopImage, PixelRect desktop, PixelRect monitor, bool multipleMonitors)
    {
        _session = session;
        Monitor = monitor;
        Title = "Lupik — " + Loc.T("capture.title");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Left = Top = 0;
        Width = Height = 1;
        var image = new CroppedBitmap(desktopImage, new Int32Rect(monitor.X - desktop.X, monitor.Y - desktop.Y, monitor.Width, monitor.Height));
        image.Freeze();
        _surface = new Surface(session, image, monitor);
        _toolbar = new ScreenshotToolbar(multipleMonitors) { HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12,24,12,0) };
        _toolbar.ModePicked += mode => _session.InvokeMode(mode,Monitor);
        _toolbar.TextToggleRequested += _session.ToggleTextMode;
        _toolbar.SetMode(session.Mode,session.TextMode);
        var layout = new System.Windows.Controls.Grid();
        layout.Children.Add(_surface); layout.Children.Add(_toolbar);
        Content = layout;
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(Handle)?.AddHook(WindowMessage);
            Place();
            UpdateCursor();
        };
        PreviewKeyDown += (_, e) =>
        {
            if ((e.Key == Key.System ? e.SystemKey : e.Key) == Key.Escape) { _session.Cancel(); e.Handled = true; }
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            int number = key >= Key.D1 && key <= Key.D4 ? key - Key.D1 : key >= Key.NumPad1 && key <= Key.NumPad4 ? key - Key.NumPad1 : -1;
            if (number >= 0 && (number < 3 || multipleMonitors)) { _session.InvokeMode((ScreenshotMode)number,Monitor); e.Handled = true; }
            if(key is Key.D5 or Key.NumPad5) { _session.SetMode(ScreenshotMode.Freeform); e.Handled=true; }
            if(key is Key.D6 or Key.NumPad6) { _session.SetMode(ScreenshotMode.Scroll); e.Handled=true; }
            if(key==Key.T && KeyState.Modifiers==ModifierKeys.None)
            { if(!e.IsRepeat) _session.ToggleTextMode(); e.Handled=true; }
        };
        PreviewMouseRightButtonDown += (_, e) => { _session.Cancel(); e.Handled = true; };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_toolbar.IsOverControls) return;
            Activate();
            _pressArmed = true;
            _session.Begin(Pointer());
            if ((_session.Mode is ScreenshotMode.Region or ScreenshotMode.Freeform or ScreenshotMode.Scroll) && !CaptureMouse()) _session.Cancel();
            e.Handled = true;
        };
        PreviewMouseMove += (_, _) => { if (_pressArmed || !_toolbar.IsOverControls) _session.Move(Pointer()); };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!_pressArmed) return;
            _pressArmed = false;
            _session.Complete(Pointer());
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
        };
        Closed += (_, _) => { _closed = true; _captureCursor?.Dispose(); _session.Cancel(); };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, _session.CheckForeground);
    }

    private static PixelPoint Pointer()
    {
        NativeMethods.GetCursorPos(out var p);
        return new(p.x, p.y);
    }

    internal void Place()
    {
        if (Handle != IntPtr.Zero)
            SetWindowPos(Handle, new IntPtr(-1), Monitor.Left, Monitor.Top, Monitor.Width, Monitor.Height, 0x0010);
    }
    internal void Redraw()
    {
        _surface.InvalidateVisual();
        _toolbar.Visibility=_session.SelectionStarted ? Visibility.Collapsed : Visibility.Visible;
        _toolbar.SetMode(_session.Mode,_session.TextMode);
    }
    internal void CloseOverlay() { if (!_closed) Close(); }

    private void UpdateCursor()
    {
        if(_closed) return;
        double scale=VisualTreeHelper.GetDpi(this).DpiScaleX;
        if(_captureCursor!=null && Math.Abs(scale-_cursorScale)<0.01) return;
        var old=_captureCursor;
        _captureCursor=new ScreenshotCursor(scale); _cursorScale=scale;
        Cursor=_captureCursor.Value; old?.Dispose();
    }

    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == 0x007E) _session.Cancel(); // display topology changed while selecting
        if (msg == 0x02E0) Dispatcher.BeginInvoke(()=>{ Place(); UpdateCursor(); }); // physical bounds and cursor after DPI update
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private sealed class Surface(ScreenshotSession session, BitmapSource image, PixelRect monitor) : FrameworkElement
    {
        private readonly Brush _shade = new SolidColorBrush(Color.FromArgb(125, 0, 0, 0));
        private readonly Brush _panel = new SolidColorBrush(Color.FromArgb(245, 27, 25, 23));
        private readonly Brush _accent = new SolidColorBrush(Accent.Color);
        private readonly Typeface _font = new("Segoe UI");

        private FormattedText Text(string value, double size, Brush brush) => new(value, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, _font, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth, height = ActualHeight;
            if (width <= 0 || height <= 0) return;
            var full = new Rect(0, 0, width, height);
            dc.DrawImage(image, full);
            Geometry? freeform=null;
            if(session.Mode==ScreenshotMode.Freeform && session.Dragging)
            {
                var transformed=new GeometryGroup { Transform=new MatrixTransform(width/monitor.Width,0,0,height/monitor.Height,
                    -monitor.X*width/monitor.Width,-monitor.Y*height/monitor.Height) };
                transformed.Children.Add(session.OutlineGeometry); freeform=transformed;
            }
            var holes = new GeometryGroup { FillRule = FillRule.EvenOdd };
            holes.Children.Add(new RectangleGeometry(full));
            Rect? selected = null;
            var area = session.Selection;
            if (area is { Width: > 0, Height: > 0 } box)
            {
                var overlap = PixelRect.Intersect(box, monitor);
                if (overlap.Width > 0 && overlap.Height > 0)
                {
                    selected = new Rect((overlap.X - monitor.X) * width / monitor.Width, (overlap.Y - monitor.Y) * height / monitor.Height,
                        overlap.Width * width / monitor.Width, overlap.Height * height / monitor.Height);
                    if(freeform==null) holes.Children.Add(new RectangleGeometry(selected.Value));
                }
            }
            if(freeform!=null) holes.Children.Add(freeform);
            dc.DrawGeometry(_shade, null, holes);
            if(freeform!=null)
            {
                dc.DrawGeometry(null,new Pen(Brushes.Black,4),freeform);
                dc.DrawGeometry(null,new Pen(Brushes.White,1.5),freeform);
            }
            if (selected is { } selection)
            {
                if(freeform==null)
                {
                    dc.DrawRectangle(null, new Pen(Brushes.Black, 3), selection);
                    dc.DrawRectangle(null, new Pen(_accent, 1.5), selection);
                }
                if (area is { } dimensions)
                {
                    var label = Text($"{dimensions.Width} × {dimensions.Height} px", 12, Brushes.White);
                    double x = Math.Clamp(selection.Left, 8, Math.Max(8, width - label.Width - 24));
                    double y = selection.Bottom + 8;
                    if (y + 30 > height) y = Math.Max(8, selection.Top - 34);
                    dc.DrawRoundedRectangle(_panel, null, new Rect(x, y, label.Width + 20, 28), 6, 6);
                    dc.DrawText(label, new Point(x + 10, y + 5));
                }
            }
        }
    }
}
