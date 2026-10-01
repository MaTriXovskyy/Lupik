using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lupik.Core;
using Lupik.Localization;
using Point = System.Windows.Point;

namespace Lupik.Views;

/// <summary>
/// Pipette without a button: holding Ctrl over the picture shows a loupe with the pixels under the cursor and the
/// colour's hex code; Ctrl+click copies it (with Shift as rgb(), with Alt as hsl()). (The preview never has the keyboard focus, so Ctrl is read from Windows.)
/// </summary>
public partial class ImageViewer
{
    private const int LoupePixels = 11;
    private DispatcherTimer? _pipetteWatch;
    private Color _pipetteColor;

    private void WirePipette()
    {
        ViewportBorder.PreviewMouseMove += (_, e) => UpdatePipette(e.GetPosition(PreviewImage), e.GetPosition(ViewportBorder));
        ViewportBorder.MouseLeave += (_, _) => HidePipette();
        ViewportBorder.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!PipettePopup.IsOpen || (KeyState.Modifiers & ModifierKeys.Control) == 0) return;
            var mods = KeyState.Modifiers;
            string text = (mods & ModifierKeys.Shift) != 0 ? $"rgb({_pipetteColor.R}, {_pipetteColor.G}, {_pipetteColor.B})"
                        : (mods & ModifierKeys.Alt) != 0 ? ToHsl(_pipetteColor)
                        : Accent.ToHex(_pipetteColor);
            try
            {
                Clipboard.SetText(text);
                TextCopied?.Invoke(Loc.T("pipette.copied", text));
            }
            catch (Exception ex) { App.Log($"[ImageViewer] Clipboard: {ex.Message}"); }
            e.Handled = true; // no panning
        };
    }

    private void UpdatePipette(Point onImage, Point onViewport)
    {
        if ((KeyState.Modifiers & ModifierKeys.Control) == 0 || IsCropping || IsReadingText || AnnotationLayer.Visibility == Visibility.Visible
            || PreviewImage.Source is not BitmapSource source || PreviewImage.RenderSize.Width <= 0)
        {
            HidePipette();
            return;
        }

        // Cursor -> pixel of the shown bitmap (GetPosition already undoes zoom, pan and rotation)
        int px = (int)(onImage.X * source.PixelWidth / PreviewImage.RenderSize.Width);
        int py = (int)(onImage.Y * source.PixelHeight / PreviewImage.RenderSize.Height);
        if (px < 0 || py < 0 || px >= source.PixelWidth || py >= source.PixelHeight) { HidePipette(); return; }

        try
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
                ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            bgra.CopyPixels(new Int32Rect(px, py, 1, 1), pixel, 4, 0);
            _pipetteColor = Color.FromRgb(pixel[2], pixel[1], pixel[0]);

            // The 11 x 11 pixels around it, kept inside the picture
            int half = LoupePixels / 2;
            int x0 = Math.Clamp(px - half, 0, Math.Max(0, source.PixelWidth - LoupePixels));
            int y0 = Math.Clamp(py - half, 0, Math.Max(0, source.PixelHeight - LoupePixels));
            var area = new Int32Rect(x0, y0, Math.Min(LoupePixels, source.PixelWidth), Math.Min(LoupePixels, source.PixelHeight));
            PipetteZoom.Source = new CroppedBitmap(source, area);
            PipetteSwatch.Background = new SolidColorBrush(_pipetteColor);
            PipetteHex.Text = Accent.ToHex(_pipetteColor);

            // Next to the cursor, flipped to the other side near the right / bottom edge
            PipettePopup.HorizontalOffset = onViewport.X + 160 > ViewportBorder.ActualWidth ? onViewport.X - 156 : onViewport.X + 18;
            PipettePopup.VerticalOffset = onViewport.Y + 190 > ViewportBorder.ActualHeight ? onViewport.Y - 186 : onViewport.Y + 18;
            PipettePopup.IsOpen = true;
            WatchCtrl();
        }
        catch (Exception ex)
        {
            App.Log($"[ImageViewer] Pipette: {ex.Message}");
            HidePipette();
        }
    }

    /// <summary>Ctrl let go without moving the mouse: the loupe goes away anyway.</summary>
    private void WatchCtrl()
    {
        if (_pipetteWatch != null) return;
        _pipetteWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _pipetteWatch.Tick += (_, _) => { if ((KeyState.Modifiers & ModifierKeys.Control) == 0) HidePipette(); };
        _pipetteWatch.Start();
    }

    /// <summary>CSS hsl(): hue in degrees, saturation and lightness in percent.</summary>
    private static string ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double l = (max + min) / 2, s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1)), h = 0;
        if (d != 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return $"hsl({Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%)";
    }

    private void HidePipette()
    {
        _pipetteWatch?.Stop();
        _pipetteWatch = null;
        PipettePopup.IsOpen = false;
    }
}
