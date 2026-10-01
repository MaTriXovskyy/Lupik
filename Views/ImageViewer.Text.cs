using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Lupik.Core;
using Lupik.Localization;
using Point = System.Windows.Point;

namespace Lupik.Views;

/// <summary>
/// Text mode (T): the picture is read once in the background (nothing is drawn over the words). Dragging over some
/// reads that part again, enlarged (see <see cref="TextRecognition"/>), and copies it; a click copies one word;
/// Enter copies everything. Esc (or T) leaves.
/// </summary>
public partial class ImageViewer
{
    /// <summary>Text was copied (for the notice in the title bar).</summary>
    public event Action<string>? TextCopied;

    public bool IsReadingText => TextLayer.Visibility == Visibility.Visible;

    // The picture as read: the full-resolution decode, turned like it is on screen
    private System.Drawing.Bitmap? _textBitmap;
    private IReadOnlyList<TextRecognition.Line> _textLines = Array.Empty<TextRecognition.Line>();
    private int _textToken;
    private Point? _textDragStart;

    public void ToggleTextMode()
    {
        if (IsReadingText) EndTextMode();
        else _ = BeginTextModeAsync();
    }

    private void OnTextClicked(object sender, RoutedEventArgs e) => ToggleTextMode();

    private async Task BeginTextModeAsync()
    {
        if (_currentPath == null || IsCropping) return;
        if (!TextRecognition.IsAvailable)
        {
            TextCopied?.Invoke(Loc.T("ocr.noLanguage"));
            TextButton.IsChecked = false;
            return;
        }
        int token = ++_textToken;
        TextLayer.Visibility = Visibility.Visible;
        TextButton.IsChecked = true;
        _textLines = Array.Empty<TextRecognition.Line>();
        SetTextHint("loader-circle", Loc.T("ocr.reading"));

        try
        {
            if (await FullBitmapAsync() is not BitmapSource full) { EndTextMode(); return; }
            // As shown: R-rotations applied, so sideways text that was turned upright reads upright
            BitmapSource turned = Rotation == 0 ? full : new TransformedBitmap(full, new RotateTransform(Rotation));
            turned.Freeze();
            var bitmap = await Task.Run(() => ToDrawingBitmap(turned));
            if (token != _textToken) { bitmap.Dispose(); return; }
            _textBitmap?.Dispose();
            _textBitmap = bitmap;

            var lines = await TextRecognition.RecognizeAsync(bitmap);
            if (token != _textToken) return;
            _textLines = lines;
            SetTextHint("scan-text", lines.Count == 0 ? Loc.T("ocr.none") : Loc.T("ocr.hint"));
            App.Log($"[ImageViewer] Text mode: {lines.Count} lines, {lines.Sum(l => l.Words.Count)} words");
        }
        catch (Exception ex)
        {
            App.Log($"[ImageViewer] OCR failed: {ex}");
            if (token == _textToken) SetTextHint("triangle-alert", Loc.T("ocr.failed"));
        }
    }

    public void EndTextMode()
    {
        _textToken++;
        _textDragStart = null;
        TextLayer.Visibility = Visibility.Collapsed;
        TextButton.IsChecked = false;
        TextSelection.Visibility = Visibility.Collapsed;
        _textLines = Array.Empty<TextRecognition.Line>();
        _textBitmap?.Dispose();
        _textBitmap = null;
    }

    /// <summary>Enter: everything that was found.</summary>
    public void CopyAllText()
    {
        if (!IsReadingText || _textLines.Count == 0) return;
        Copy(TextRecognition.ToText(_textLines));
    }

    private void Copy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            TextCopied?.Invoke(Loc.T("ocr.copied", text.Length > 40 ? text[..40].Replace('\n', ' ') + "…" : text.Replace('\n', ' ')));
        }
        catch (Exception ex) { App.Log($"[ImageViewer] Clipboard: {ex.Message}"); }
    }

    private void SetTextHint(string icon, string text)
    {
        TextHintIcon.Kind = icon;
        TextHintText.Text = text;
        PlaceTextHint();
    }

    private void PlaceTextHint()
    {
        TextHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(TextHint, Math.Max(8, (TextLayer.ActualWidth - TextHint.DesiredSize.Width) / 2));
        Canvas.SetTop(TextHint, Math.Max(8, TextLayer.ActualHeight - TextHint.DesiredSize.Height - 14));
    }

    // --- Where the picture's pixels are on screen (zoom, pan and rotation included)

    /// <summary>The shown picture's rectangle in TextLayer coordinates (axis-aligned: rotation is in 90° steps).</summary>
    private Rect ShownImageRect() => PreviewImage.TransformToVisual(TextLayer).TransformBounds(new Rect(PreviewImage.RenderSize));

    private Rect ToScreen(TextRecognition.Rect r)
    {
        if (_textBitmap == null) return Rect.Empty;
        var shown = ShownImageRect();
        double sx = shown.Width / _textBitmap.Width, sy = shown.Height / _textBitmap.Height;
        return new Rect(shown.X + r.X * sx, shown.Y + r.Y * sy, r.Width * sx, r.Height * sy);
    }

    private System.Drawing.Rectangle ToPixels(Rect screen)
    {
        var shown = ShownImageRect();
        double sx = _textBitmap!.Width / shown.Width, sy = _textBitmap.Height / shown.Height;
        int x = (int)Math.Floor((screen.X - shown.X) * sx), y = (int)Math.Floor((screen.Y - shown.Y) * sy);
        return new System.Drawing.Rectangle(x, y, (int)Math.Ceiling(screen.Width * sx), (int)Math.Ceiling(screen.Height * sy));
    }

    private void OnTextLayerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsReadingText) return;
        PlaceTextHint();
    }

    // --- Mouse

    private void OnTextMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_textBitmap == null) return;
        _textDragStart = e.GetPosition(TextLayer);
        TextLayer.CaptureMouse();
        e.Handled = true;
    }

    private void OnTextMouseMove(object sender, MouseEventArgs e)
    {
        if (_textDragStart is not Point start) return;
        var rect = new Rect(start, e.GetPosition(TextLayer));
        TextSelection.Visibility = Visibility.Visible;
        Canvas.SetLeft(TextSelection, rect.X);
        Canvas.SetTop(TextSelection, rect.Y);
        TextSelection.Width = rect.Width;
        TextSelection.Height = rect.Height;
    }

    private async void OnTextMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_textDragStart is not Point start || _textBitmap == null) return;
        _textDragStart = null;
        TextLayer.ReleaseMouseCapture();
        var end = e.GetPosition(TextLayer);
        var rect = new Rect(start, end);
        TextSelection.Visibility = Visibility.Collapsed;

        // A click: the word under the cursor
        if (rect.Width < 4 && rect.Height < 4)
        {
            var word = _textLines.SelectMany(l => l.Words).FirstOrDefault(w => { var r = ToScreen(w.Bounds); r.Inflate(2, 2); return r.Contains(end); });
            if (word != null) Copy(word.Text);
            return;
        }

        // A drag: that part, read again on its own and enlarged (more accurate than the whole-picture pass)
        int token = _textToken;
        var pixels = ToPixels(rect);
        SetTextHint("loader-circle", Loc.T("ocr.reading"));
        try
        {
            var lines = await TextRecognition.RecognizeAsync(_textBitmap, pixels);
            if (token != _textToken) return;
            string text = TextRecognition.ToText(lines);
            if (text.Length == 0)
            {
                // Nothing on its own: fall back to the words already found there
                var shownSelection = rect;
                text = string.Join("\n", _textLines
                    .Select(l => string.Join(" ", l.Words.Where(w => ToScreen(w.Bounds).IntersectsWith(shownSelection)).Select(w => w.Text)))
                    .Where(t => t.Length > 0));
            }
            if (text.Length > 0) Copy(text);
            SetTextHint("scan-text", text.Length > 0 ? Loc.T("ocr.hint") : Loc.T("ocr.noneHere"));
        }
        catch (Exception ex)
        {
            App.Log($"[ImageViewer] OCR of a part failed: {ex.Message}");
            SetTextHint("triangle-alert", Loc.T("ocr.failed"));
        }
    }

    // --- Helpers

    private static System.Drawing.Bitmap ToDrawingBitmap(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        using var loaded = new System.Drawing.Bitmap(stream);
        return new System.Drawing.Bitmap(loaded); // independent of the stream
    }
}
