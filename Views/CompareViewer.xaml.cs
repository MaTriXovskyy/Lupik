using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

namespace QuickPeek.Views;

/// <summary>
/// Two images compared either side by side or with a before/after slider.
/// Zoom and pan are shared, so both images always show the same spot.
/// </summary>
public partial class CompareViewer : UserControl
{
    private static readonly Brush Accent = Frozen(Color.FromRgb(0xCB, 0xA6, 0xF7));
    private static readonly Brush Warning = Frozen(Color.FromRgb(0xF9, 0xE2, 0xAF));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0xA1, 0xA1, 0xAA));

    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _translate = new();

    private static bool _sliderMode;       // remembered between comparisons
    private double _split = 0.5;           // slider position, fraction of the width
    private bool _panning, _splitting;
    private Point _lastMouse;
    private int _loadToken;

    private record struct Side(string Path, BitmapSource Bitmap, long Size, int Width, int Height);
    private Side _a, _b;

    public string PathA => _a.Path;
    public string PathB => _b.Path;

    /// <summary>"Pojedynczo": go back to the normal preview of the first image.</summary>
    public event Action<string>? SingleRequested;

    public CompareViewer()
    {
        InitializeComponent();
        var group = new TransformGroup();
        group.Children.Add(_scale);
        group.Children.Add(_translate);
        foreach (var image in new[] { SideImageA, SideImageB, SliderImageA, SliderImageB })
            image.RenderTransform = group;
        Viewport.SizeChanged += (_, _) => UpdateSplit();
        ApplyMode();
    }

    public async Task<bool> LoadAsync(string pathA, string pathB, Func<string, Task<(BitmapSource, long, int, int)>> decode)
    {
        int token = ++_loadToken;
        var taskA = decode(pathA);
        var taskB = decode(pathB);
        var (bmpA, sizeA, wA, hA) = await taskA;
        var (bmpB, sizeB, wB, hB) = await taskB;
        if (token != _loadToken) return false;

        _a = new Side(pathA, bmpA, sizeA, wA, hA);
        _b = new Side(pathB, bmpB, sizeB, wB, hB);
        _split = 0.5;
        Show();
        Fit();
        return true;
    }

    public void Release()
    {
        _loadToken++;
        SideImageA.Source = SideImageB.Source = SliderImageA.Source = SliderImageB.Source = null;
        _a = _b = default;
    }

    private void Show()
    {
        SideImageA.Source = SliderImageA.Source = _a.Bitmap;
        SideImageB.Source = SliderImageB.Source = _b.Bitmap;

        string nameA = Path.GetFileName(_a.Path), nameB = Path.GetFileName(_b.Path);
        SideLabelA.Text = SliderLabelA.Text = "A  " + nameA;
        SideLabelB.Text = SliderLabelB.Text = "B  " + nameB;

        // Footer: highlight what differs (dimensions matter most when comparing versions)
        bool sameSize = _a.Width == _b.Width && _a.Height == _b.Height;
        InfoTextA.Text = $"A: {_a.Width} × {_a.Height} px · {FormatSize(_a.Size)}";
        InfoTextB.Text = $"B: {_b.Width} × {_b.Height} px · {FormatSize(_b.Size)}" + (sameSize ? "" : "  (inne wymiary)");
        InfoTextA.Foreground = Muted;
        InfoTextB.Foreground = sameSize ? Muted : Warning;
        UpdateSplit();
    }

    // ---------- Modes ----------

    private void OnSideClicked(object sender, RoutedEventArgs e) { _sliderMode = false; ApplyMode(); }
    private void OnSliderClicked(object sender, RoutedEventArgs e) { _sliderMode = true; ApplyMode(); }
    private void OnSwapClicked(object sender, RoutedEventArgs e) => Swap();
    private void OnSingleClicked(object sender, RoutedEventArgs e) => SingleRequested?.Invoke(_a.Path);

    private void ApplyMode()
    {
        SideBySide.Visibility = _sliderMode ? Visibility.Collapsed : Visibility.Visible;
        SliderMode.Visibility = _sliderMode ? Visibility.Visible : Visibility.Collapsed;
        // Active mode in the accent colour; the other back to the style's colour (a null brush would hide it)
        if (_sliderMode) { SliderButton.Foreground = Accent; SideButton.ClearValue(ForegroundProperty); }
        else { SideButton.Foreground = Accent; SliderButton.ClearValue(ForegroundProperty); }
        Fit();
        UpdateSplit();
    }

    private void Swap()
    {
        (_a, _b) = (_b, _a);
        _split = 1 - _split;
        Show();
    }

    /// <summary>Keys while comparing. Returns true when handled.</summary>
    public bool HandleKey(Key key)
    {
        if (Keyboard.Modifiers != 0) return false;
        switch (key)
        {
            case Key.S: _sliderMode = !_sliderMode; ApplyMode(); return true;
            case Key.X: Swap(); return true;
            case Key.OemPlus or Key.Add: ZoomBy(1.25); return true;
            case Key.OemMinus or Key.Subtract: ZoomBy(1 / 1.25); return true;
            case Key.D0 or Key.NumPad0: Fit(); return true;
            case Key.Left when _sliderMode: _split = Math.Max(0, _split - 0.05); UpdateSplit(); return true;
            case Key.Right when _sliderMode: _split = Math.Min(1, _split + 0.05); UpdateSplit(); return true;
            case Key.Left or Key.Right or Key.Up or Key.Down: return true; // no file navigation while comparing
            default: return false;
        }
    }

    // ---------- Slider ----------

    private void UpdateSplit()
    {
        double w = Viewport.ActualWidth, h = Viewport.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double x = Math.Round(w * _split);
        SliderClipB.Clip = new RectangleGeometry(new Rect(x, 0, Math.Max(0, w - x), h));
        SplitLine.Height = h;
        Canvas.SetLeft(SplitLine, x - 1);
        Canvas.SetLeft(SplitHandle, x - SplitHandle.Width / 2);
        Canvas.SetTop(SplitHandle, (h - SplitHandle.Height) / 2);
    }

    private bool NearSplit(Point p) => _sliderMode && Math.Abs(p.X - Viewport.ActualWidth * _split) <= 16;

    // ---------- Zoom / pan (shared by both images) ----------

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Zoom around the cursor: position in the (untransformed) image under the mouse
        Image target = _sliderMode ? SliderImageA : e.GetPosition(PaneB).X >= 0 ? SideImageB : SideImageA;
        ZoomAt(e.GetPosition(target), e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private void ZoomBy(double factor)
    {
        var image = _sliderMode ? SliderImageA : SideImageA;
        ZoomAt(new Point(image.ActualWidth / 2, image.ActualHeight / 2), factor);
    }

    private void ZoomAt(Point p, double factor)
    {
        double scale = _scale.ScaleX * factor;
        if (scale < 0.1 || scale > 30) return;
        _translate.X = p.X - (p.X - _translate.X) * factor;
        _translate.Y = p.Y - (p.Y - _translate.Y) * factor;
        _scale.ScaleX = _scale.ScaleY = scale;
        ZoomText.Text = $"{Math.Round(scale * 100)}%";
    }

    private void OnFitClicked(object sender, RoutedEventArgs e) => Fit();
    private void OnZoomInClicked(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void OnZoomOutClicked(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);

    private void Fit()
    {
        _scale.ScaleX = _scale.ScaleY = 1;
        _translate.X = _translate.Y = 0;
        ZoomText.Text = "100%";
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Viewport);
        if (e.ClickCount == 2 && !NearSplit(p)) { Fit(); return; }
        _splitting = NearSplit(p);
        _panning = !_splitting;
        _lastMouse = p;
        Viewport.CaptureMouse();
        if (_splitting) MoveSplit(p);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _panning = _splitting = false;
        Viewport.ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Viewport);
        Viewport.Cursor = _splitting || NearSplit(p) ? Cursors.SizeWE : null;
        if (e.LeftButton != MouseButtonState.Pressed) { _panning = _splitting = false; return; }

        if (_splitting) MoveSplit(p);
        else if (_panning)
        {
            _translate.X += p.X - _lastMouse.X;
            _translate.Y += p.Y - _lastMouse.Y;
            _lastMouse = p;
        }
    }

    private void MoveSplit(Point p)
    {
        if (Viewport.ActualWidth <= 0) return;
        _split = Math.Clamp(p.X / Viewport.ActualWidth, 0, 1);
        UpdateSplit();
    }

    // ---------- Helpers ----------

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
        return $"{len:0.##} {sizes[order]}";
    }
}
