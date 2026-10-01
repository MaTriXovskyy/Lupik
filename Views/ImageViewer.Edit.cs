using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Lupik.Localization;
using Point = System.Windows.Point;

namespace Lupik.Views;

/// <summary>
/// Quick fixes to a picture in edit mode (E): blur (pixelate) a part, draw an arrow or a frame, add a note,
/// make it smaller. Marks are kept as fractions of the picture, so they land in the same place at full resolution
/// when saving (Magick redraws them on the original, rotation included).
/// </summary>
public partial class ImageViewer : IEditable
{
    private enum MarkKind { Blur, Arrow, Frame, Text }

    /// <summary>A mark on the picture: positions are fractions (0..1) of the picture as shown (after rotation).</summary>
    private sealed record Mark(MarkKind Kind, Point From, Point To, Color Color, string Text = "");

    private static readonly string[] EditableExtensions = { ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".bmp", ".tif", ".tiff" };

    private readonly List<object> _history = new(); // marks and resizes, for undo
    private readonly List<Mark> _marks = new();
    private int? _resizeTo; // longest side after saving
    private bool _editing;
    private int _rotationAtStart;

    public event Action? DirtyChanged;

    public bool IsDirty => _marks.Count > 0 || _resizeTo != null || Rotation != _rotationAtStart;

    public string? WhyNotEditable()
    {
        if (_currentPath == null || PreviewImage.Source == null) return Loc.T("edit.cantRead");
        string ext = System.IO.Path.GetExtension(_currentPath).ToLowerInvariant();
        return Array.IndexOf(EditableExtensions, ext) < 0 ? Loc.T("edit.imageFormat", ext.TrimStart('.').ToUpperInvariant()) : null;
    }

    public void BeginEdit()
    {
        _editing = true;
        _marks.Clear();
        _history.Clear();
        _resizeTo = null;
        _rotationAtStart = Rotation;
        CancelCrop();
        ResetZoom();
        AnnotationLayer.Visibility = Visibility.Visible;
        Footer.Visibility = Visibility.Collapsed;
        EditFooter.Visibility = Visibility.Visible;
        UpdateEditSize();
        Redraw();
    }

    public void EndEdit()
    {
        _editing = false;
        _marks.Clear();
        _history.Clear();
        _resizeTo = null;
        AnnotationLayer.Children.Clear();
        AnnotationLayer.Visibility = Visibility.Collapsed;
        EditFooter.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Visible;
    }

    private void Changed()
    {
        UpdateEditSize();
        Redraw();
        DirtyChanged?.Invoke();
    }

    public bool HandleEditKey(Key key, ModifierKeys mods)
    {
        if (key == Key.Z && mods == ModifierKeys.Control) { Undo(); return true; }
        if (mods == 0)
        {
            switch (key)
            {
                case Key.B: ToolBlur.IsChecked = true; return true;
                case Key.A: ToolArrow.IsChecked = true; return true;
                case Key.R when _marks.Count == 0: Rotate(90); Changed(); return true;
                case Key.O: ToolFrame.IsChecked = true; return true; // (F is full screen)
                case Key.T: ToolText.IsChecked = true; return true;
            }
        }
        if (key == Key.R && mods == ModifierKeys.Shift && _marks.Count == 0) { Rotate(-90); Changed(); return true; }
        return false;
    }

    private void OnUndoClicked(object sender, RoutedEventArgs e) => Undo();

    private void Undo()
    {
        if (_history.Count == 0) return;
        var last = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        if (last is Mark mark) _marks.Remove(mark);
        else _resizeTo = _history.OfType<int>().Cast<int?>().LastOrDefault();
        Changed();
    }

    private Color CurrentColor =>
        ColorYellow.IsChecked == true ? Color.FromRgb(0xFF, 0xD0, 0x2E)
        : ColorWhite.IsChecked == true ? Colors.White
        : ColorBlack.IsChecked == true ? Color.FromRgb(0x11, 0x11, 0x11)
        : Color.FromRgb(0xE5, 0x30, 0x2A);

    private MarkKind CurrentTool =>
        ToolArrow.IsChecked == true ? MarkKind.Arrow
        : ToolFrame.IsChecked == true ? MarkKind.Frame
        : ToolText.IsChecked == true ? MarkKind.Text
        : MarkKind.Blur;

    // ---------- Resize ----------

    private (int W, int H) SavedSize()
    {
        bool sideways = Rotation is 90 or 270;
        int w = (int)(sideways ? _naturalHeight : _naturalWidth), h = (int)(sideways ? _naturalWidth : _naturalHeight);
        if (_resizeTo is int longest && Math.Max(w, h) > longest)
        {
            double k = (double)longest / Math.Max(w, h);
            return ((int)Math.Round(w * k), (int)Math.Round(h * k));
        }
        return (w, h);
    }

    private void UpdateEditSize()
    {
        var (w, h) = SavedSize();
        EditSizeText.Text = (_resizeTo != null ? "→ " : "") + $"{w} × {h} px";
    }

    private void OnResizeClicked(object sender, RoutedEventArgs e)
    {
        int longest = (int)Math.Max(_naturalWidth, _naturalHeight);
        string suggestion = longest > 1920 ? "1920" : Math.Max(16, longest / 2).ToString();
        string? answer = InputDialog.Ask(Window.GetWindow(this)!, Loc.T("imageEdit.resizeTitle"), Loc.T("imageEdit.resizeHint", longest),
            suggestion, Loc.T("imageEdit.resizeOk"), "shrink",
            text => int.TryParse(text.Trim(), out int n) && n >= 16 && n < longest ? null : Loc.T("imageEdit.resizeBad", longest - 1));
        if (answer == null) return;
        _resizeTo = int.Parse(answer.Trim());
        _history.Add(_resizeTo.Value);
        Changed();
    }

    // ---------- Drawing ----------

    private Point? _dragStart;
    private FrameworkElement? _dragPreview;

    private Rect ImageArea() => PreviewImage.TransformToVisual(AnnotationLayer).TransformBounds(new Rect(PreviewImage.RenderSize));

    private Point ToFraction(Point p)
    {
        var area = ImageArea();
        return new Point(Math.Clamp((p.X - area.X) / area.Width, 0, 1), Math.Clamp((p.Y - area.Y) / area.Height, 0, 1));
    }

    private Point FromFraction(Point f)
    {
        var area = ImageArea();
        return new Point(area.X + f.X * area.Width, area.Y + f.Y * area.Height);
    }

    private void OnAnnotateMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editing) return;
        var p = e.GetPosition(AnnotationLayer);
        if (!ImageArea().Contains(p)) return;

        if (CurrentTool == MarkKind.Text)
        {
            var color = CurrentColor;
            string? text = InputDialog.Ask(Window.GetWindow(this)!, Loc.T("imageEdit.textTitle"), null, "", Loc.T("imageEdit.textOk"), "type",
                t => t.Trim().Length == 0 ? "" : null);
            if (!string.IsNullOrWhiteSpace(text)) Add(new Mark(MarkKind.Text, ToFraction(p), ToFraction(p), color, text.Trim()));
            e.Handled = true;
            return;
        }
        _dragStart = p;
        AnnotationLayer.CaptureMouse();
        e.Handled = true;
    }

    private void OnAnnotateMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start) return;
        var mark = new Mark(CurrentTool, ToFraction(start), ToFraction(e.GetPosition(AnnotationLayer)), CurrentColor);
        if (_dragPreview != null) AnnotationLayer.Children.Remove(_dragPreview);
        _dragPreview = Visual(mark);
        if (_dragPreview != null) AnnotationLayer.Children.Add(_dragPreview);
    }

    private void OnAnnotateMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not Point start) return;
        _dragStart = null;
        AnnotationLayer.ReleaseMouseCapture();
        if (_dragPreview != null) { AnnotationLayer.Children.Remove(_dragPreview); _dragPreview = null; }

        var end = e.GetPosition(AnnotationLayer);
        if (Math.Abs(end.X - start.X) < 6 && Math.Abs(end.Y - start.Y) < 6) return; // a click, not a drag
        Add(new Mark(CurrentTool, ToFraction(start), ToFraction(end), CurrentColor));
    }

    private void Add(Mark mark)
    {
        _marks.Add(mark);
        _history.Add(mark);
        Changed();
    }

    private void OnAnnotationLayerSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        AnnotationLayer.Children.Clear();
        if (!_editing) return;
        foreach (var mark in _marks)
            if (Visual(mark) is FrameworkElement element) AnnotationLayer.Children.Add(element);
    }

    /// <summary>How a mark looks on screen (the saved picture gets the same thing drawn by Magick).</summary>
    private FrameworkElement? Visual(Mark mark)
    {
        var area = ImageArea();
        if (area.Width < 1 || area.Height < 1) return null;
        var a = FromFraction(mark.From);
        var b = FromFraction(mark.To);
        var rect = new Rect(a, b);
        double stroke = Math.Max(2.5, area.Width / 250);
        var brush = new SolidColorBrush(mark.Color);
        var shadow = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.6, Color = Colors.Black };

        switch (mark.Kind)
        {
            case MarkKind.Blur:
            {
                if (rect.Width < 2 || rect.Height < 2) return null;
                var image = new Image { Width = rect.Width, Height = rect.Height, Stretch = Stretch.Fill, Source = PixelatedPreview(mark) };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(image, rect.X);
                Canvas.SetTop(image, rect.Y);
                return image;
            }
            case MarkKind.Frame:
            {
                var frame = new Rectangle { Width = rect.Width, Height = rect.Height, Stroke = brush, StrokeThickness = stroke, RadiusX = 2, RadiusY = 2, Effect = shadow };
                Canvas.SetLeft(frame, rect.X);
                Canvas.SetTop(frame, rect.Y);
                return frame;
            }
            case MarkKind.Arrow:
            {
                var path = new System.Windows.Shapes.Path { Stroke = brush, Fill = brush, StrokeThickness = stroke, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, Effect = shadow };
                var (shaft, head) = ArrowGeometry(a, b, stroke);
                var group = new GeometryGroup();
                group.Children.Add(new LineGeometry(a, shaft));
                group.Children.Add(new PathGeometry(new[] { new PathFigure(head[0], new[] { new PolyLineSegment(new[] { head[1], head[2] }, true) }, true) }));
                path.Data = group;
                return path;
            }
            default:
            {
                var text = new TextBlock
                {
                    Text = mark.Text,
                    Foreground = brush,
                    FontSize = TextSize(area.Width),
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Segoe UI"),
                    Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 1, Color = mark.Color.R + mark.Color.G + mark.Color.B > 380 ? Colors.Black : Colors.White },
                };
                Canvas.SetLeft(text, a.X);
                Canvas.SetTop(text, a.Y);
                return text;
            }
        }
    }

    private static double TextSize(double imageWidth) => Math.Max(14, imageWidth / 28);

    /// <summary>The arrow's shaft end (where the head starts) and the head's three corners.</summary>
    private static (Point Shaft, Point[] Head) ArrowGeometry(Point a, Point b, double stroke)
    {
        var v = b - a;
        double length = Math.Max(1, v.Length);
        v /= length;
        double headLength = Math.Min(length * 0.45, stroke * 5.5), headWidth = headLength * 0.8;
        var n = new Vector(-v.Y, v.X);
        var baseCenter = b - v * headLength;
        return (baseCenter + v * stroke * 0.5, new[] { b, baseCenter + n * headWidth / 2, baseCenter - n * headWidth / 2 });
    }

    /// <summary>The shown picture under a blur mark, shrunk: stretched back with nearest-neighbour it's the mosaic.</summary>
    private BitmapSource? PixelatedPreview(Mark mark)
    {
        if (PreviewImage.Source is not BitmapSource source) return null;
        BitmapSource shown = Rotation == 0 ? source : new TransformedBitmap(source, new RotateTransform(Rotation));
        var r = FractionRect(mark, shown.PixelWidth, shown.PixelHeight);
        if (r.Width < 1 || r.Height < 1) return null;
        var cropped = new CroppedBitmap(shown, new Int32Rect(r.X, r.Y, r.Width, r.Height));
        double block = BlockSize(shown.PixelWidth, shown.PixelHeight);
        double k = Math.Min(1, 1 / block);
        return new TransformedBitmap(cropped, new ScaleTransform(Math.Max(k, 1.0 / r.Width), Math.Max(k, 1.0 / r.Height)));
    }

    /// <summary>Mosaic tiles: about 1/70 of the picture's longest side, never smaller than 6 px.</summary>
    private static double BlockSize(int w, int h) => Math.Max(6, Math.Max(w, h) / 70.0);

    private static Int32Rect FractionRect(Mark mark, int w, int h)
    {
        int x1 = (int)Math.Round(Math.Min(mark.From.X, mark.To.X) * w), x2 = (int)Math.Round(Math.Max(mark.From.X, mark.To.X) * w);
        int y1 = (int)Math.Round(Math.Min(mark.From.Y, mark.To.Y) * h), y2 = (int)Math.Round(Math.Max(mark.From.Y, mark.To.Y) * h);
        x1 = Math.Clamp(x1, 0, w - 1); y1 = Math.Clamp(y1, 0, h - 1);
        return new Int32Rect(x1, y1, Math.Clamp(x2 - x1, 1, w - x1), Math.Clamp(y2 - y1, 1, h - y1));
    }

    // ---------- Saving ----------

    public Task SaveToAsync(string path)
    {
        string source = _currentPath!;
        var marks = _marks.ToList();
        int rotation = Rotation;
        int? resizeTo = _resizeTo;
        double shownWidth = ImageArea().Width; // strokes and text keep their on-screen proportions
        return Task.Run(() => SaveEdited(source, path, marks, rotation, resizeTo, shownWidth));
    }

    private static void SaveEdited(string source, string target, List<Mark> marks, int rotation, int? resizeTo, double shownWidth)
    {
        // The pixels as Lupik shows them (no EXIF auto-rotation: an orientation tag stays in the file and turns
        // the picture and the marks together in other apps)
        using var image = new ImageMagick.MagickImage(source);
        if (rotation != 0) image.Rotate(rotation);
        int w = (int)image.Width, h = (int)image.Height;
        double scale = w / Math.Max(1, shownWidth); // picture pixels per on-screen pixel
        double stroke = Math.Max(2.5, shownWidth / 250) * scale;

        foreach (var mark in marks)
        {
            var from = new Point(mark.From.X * w, mark.From.Y * h);
            var to = new Point(mark.To.X * w, mark.To.Y * h);
            var color = new ImageMagick.MagickColor(mark.Color.R, mark.Color.G, mark.Color.B);
            switch (mark.Kind)
            {
                case MarkKind.Blur:
                {
                    var r = FractionRect(mark, w, h);
                    using var region = image.Clone();
                    region.Crop(new ImageMagick.MagickGeometry(r.X, r.Y, (uint)r.Width, (uint)r.Height));
                    region.ResetPage();
                    double block = BlockSize(w, h);
                    region.Scale((uint)Math.Max(1, Math.Ceiling(r.Width / block)), (uint)Math.Max(1, Math.Ceiling(r.Height / block)));
                    region.Sample((uint)r.Width, (uint)r.Height); // nearest neighbour: the mosaic
                    image.Composite(region, r.X, r.Y, ImageMagick.CompositeOperator.Over);
                    break;
                }
                case MarkKind.Frame:
                    new ImageMagick.Drawing.Drawables()
                        .StrokeColor(color).StrokeWidth(stroke).FillColor(ImageMagick.MagickColors.Transparent)
                        .StrokeLineJoin(ImageMagick.LineJoin.Round)
                        .Rectangle(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Max(from.X, to.X), Math.Max(from.Y, to.Y))
                        .Draw(image);
                    break;
                case MarkKind.Arrow:
                {
                    var (shaft, head) = ArrowGeometry(from, to, stroke);
                    new ImageMagick.Drawing.Drawables()
                        .StrokeColor(color).StrokeWidth(stroke).StrokeLineCap(ImageMagick.LineCap.Round)
                        .Line(from.X, from.Y, shaft.X, shaft.Y)
                        .StrokeWidth(stroke * 0.6).StrokeLineJoin(ImageMagick.LineJoin.Round).FillColor(color)
                        .Polygon(head.Select(p => new ImageMagick.PointD(p.X, p.Y)))
                        .Draw(image);
                    break;
                }
                case MarkKind.Text:
                {
                    double size = TextSize(shownWidth) * scale;
                    bool light = mark.Color.R + mark.Color.G + mark.Color.B > 380;
                    string font = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeuib.ttf");
                    var text = new ImageMagick.Drawing.Drawables()
                        .Font(File.Exists(font) ? font : "Arial").FontPointSize(size)
                        .StrokeColor(light ? ImageMagick.MagickColors.Black : ImageMagick.MagickColors.White).StrokeWidth(Math.Max(1, size / 18))
                        .FillColor(color)
                        .Text(from.X, from.Y + size, mark.Text);
                    text.Draw(image);
                    // The outline went over the letters: draw them again, filled only
                    new ImageMagick.Drawing.Drawables()
                        .Font(File.Exists(font) ? font : "Arial").FontPointSize(size)
                        .StrokeColor(ImageMagick.MagickColors.Transparent).FillColor(color)
                        .Text(from.X, from.Y + size, mark.Text)
                        .Draw(image);
                    break;
                }
            }
        }

        if (resizeTo is int longest && Math.Max(w, h) > longest)
            image.Resize(new ImageMagick.MagickGeometry((uint)longest, (uint)longest));

        string ext = System.IO.Path.GetExtension(source).ToLowerInvariant();
        var format = ext switch
        {
            ".png" => ImageMagick.MagickFormat.Png,
            ".webp" => ImageMagick.MagickFormat.WebP,
            ".bmp" => ImageMagick.MagickFormat.Bmp,
            ".tif" or ".tiff" => ImageMagick.MagickFormat.Tiff,
            _ => ImageMagick.MagickFormat.Jpeg,
        };
        if (format == ImageMagick.MagickFormat.Jpeg) image.Quality = 95;
        image.Write(target, format);
    }

    public void ReleaseFile() { } // the picture is decoded into memory; the file isn't held open

    public async Task ReloadAsync(string path)
    {
        await LoadImageAsync(path);
        _marks.Clear();
        _history.Clear();
        _resizeTo = null;
        _rotationAtStart = 0;
        ImageRotation.Angle = 0;
        if (_editing) { AnnotationLayer.Visibility = Visibility.Visible; UpdateEditSize(); Redraw(); }
        DirtyChanged?.Invoke();
    }
}
