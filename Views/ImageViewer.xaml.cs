using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

namespace QuickPeek.Views;

public partial class ImageViewer : UserControl
{
    private Point _lastMousePosition;
    private bool _isDragging;
    private double _naturalWidth;
    private double _naturalHeight;
    private int _loadToken;
    private string _formatLabel = "";
    private const int MaxDecodeSize = 4096;

    // Formats Windows can't decode on its own: HEIC/HEIF (needs a paid codec), PSD (flattened composite), AVIF
    private static readonly string[] MagickOnlyFormats = { ".heic", ".heif", ".psd", ".avif" };

    public double NaturalWidth => _naturalWidth;
    public double NaturalHeight => _naturalHeight;

    public ImageViewer()
    {
        InitializeComponent();
        ViewportBorder.MouseLeftButtonDown += OnViewportDoubleClick;
    }

    /// <summary>
    /// Decodes the image on a background thread and swaps it in only once it's ready,
    /// so the previous image stays visible meanwhile. Returns false if a newer load superseded this one.
    /// </summary>
    public async Task<bool> LoadImageAsync(string filePath)
    {
        int token = ++_loadToken;
        try
        {
            var (bitmap, size, width, height) = await GetDecodeTask(filePath);

            if (token != _loadToken) return false;

            _naturalWidth = width;
            _naturalHeight = height;

            PreviewImage.Source = bitmap;
            ImageRotation.Angle = 0; // rotation is per file
            ResetZoom();

            FileSizeText.Text = FormatFileSize(size);
            _formatLabel = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
            FormatText.Text = _formatLabel;
            DimensionsText.Text = $"{_naturalWidth:F0} × {_naturalHeight:F0} px";
            _currentPath = filePath;
            if (_infoOpen) _ = LoadInfoAsync(filePath);
            return true;
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return false;

            PreviewImage.Source = null;
            _naturalWidth = _naturalHeight = 0;
            DimensionsText.Text = "Błąd odczytu";
            FileSizeText.Text = ex.Message;
            return true;
        }
    }

    private static (BitmapSource, long, int, int) Decode(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (MagickOnlyFormats.Contains(ext)) return DecodeWithMagick(filePath);
        try
        {
            return DecodeWithWic(filePath);
        }
        catch (Exception ex)
        {
            // Damaged file or missing Windows codec: Magick.NET is more forgiving
            App.Log($"[ImageViewer] Windows decoder failed ({ex.Message}), falling back to Magick.NET");
            return DecodeWithMagick(filePath);
        }
    }

    // --- Preload cache: neighbors of the current image are decoded ahead of time ---

    private sealed record CacheEntry(DateTime LastWrite, Task<(BitmapSource, long, int, int)> Decoding);

    private const int MaxCachedImages = 4; // decoded images are capped at 4096 px, so ≤ ~64 MB each
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _cacheOrder = new();

    /// <summary>Drops the shown image and all preloaded ones (called when QuickPeek goes idle).</summary>
    public void Release()
    {
        _loadToken++;
        PreviewImage.Source = null;
        _cache.Clear();
        _cacheOrder.Clear();
    }

    public void Preload(string filePath)
    {
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(filePath);
            if (_cache.TryGetValue(filePath, out var existing) && existing.LastWrite == lastWrite) return;

            var decoding = Task.Run(() => Decode(filePath));
            decoding.ContinueWith(t => App.Log($"[ImageViewer] Preload failed: {t.Exception?.InnerException?.Message}"),
                TaskContinuationOptions.OnlyOnFaulted);

            _cache[filePath] = new CacheEntry(lastWrite, decoding);
            _cacheOrder.Remove(filePath);
            _cacheOrder.AddFirst(filePath);
            while (_cacheOrder.Count > MaxCachedImages)
            {
                _cache.Remove(_cacheOrder.Last!.Value);
                _cacheOrder.RemoveLast();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ImageViewer] Preload skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Reuses a preload (finished or still running) if the file hasn't changed since; otherwise decodes now.
    /// </summary>
    internal Task<(BitmapSource, long, int, int)> GetDecodeTask(string filePath)
    {
        try
        {
            if (_cache.TryGetValue(filePath, out var entry) && !entry.Decoding.IsFaulted &&
                entry.LastWrite == File.GetLastWriteTimeUtc(filePath))
                return entry.Decoding;
        }
        catch
        {
            // fall through to a fresh decode
        }
        return Task.Run(() => Decode(filePath));
    }

    /// <summary>Fast path: Windows' built-in (WIC) decoders.</summary>
    private static (BitmapSource, long, int, int) DecodeWithWic(string filePath)
    {
        // Decode from a stream, so the file isn't locked and no stale URI cache is used
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // Header only: real dimensions without decoding the pixels
        var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        int w = header.PixelWidth, h = header.PixelHeight;
        stream.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = stream;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        // Huge photos (e.g. 50 MP) are decoded at a reduced size: far faster, still sharp on screen
        if (Math.Max(w, h) > MaxDecodeSize)
        {
            if (w >= h) bmp.DecodePixelWidth = MaxDecodeSize;
            else bmp.DecodePixelHeight = MaxDecodeSize;
        }
        bmp.EndInit();
        bmp.Freeze();
        return (bmp, stream.Length, w, h);
    }

    /// <summary>
    /// HEIC/HEIF (e.g. iPhone photos) are HEVC-compressed; Windows can only decode them with a paid Store codec,
    /// so they go through the bundled Magick.NET decoder instead.
    /// </summary>
    private static (BitmapSource, long, int, int) DecodeWithMagick(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var image = new ImageMagick.MagickImage(stream);
        image.AutoOrient(); // phone photos store rotation as metadata

        int w = (int)image.Width, h = (int)image.Height;
        if (Math.Max(w, h) > MaxDecodeSize)
            image.Resize(new ImageMagick.MagickGeometry(MaxDecodeSize, MaxDecodeSize));

        using var buffer = new MemoryStream();
        if (image.HasAlpha)
        {
            // BMP3 has no alpha channel; PNG keeps transparency (e.g. PSD layers). Quality 10 = fastest zlib level.
            image.Quality = 10;
            image.Write(buffer, ImageMagick.MagickFormat.Png32);
        }
        else
        {
            image.Write(buffer, ImageMagick.MagickFormat.Bmp3);
        }
        buffer.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = buffer;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return (bmp, stream.Length, w, h);
    }

    public void ResetZoom()
    {
        ImageScale.ScaleX = 1;
        ImageScale.ScaleY = 1;
        ImageTranslate.X = 0;
        ImageTranslate.Y = 0;
        UpdateZoomText();
    }

    private void UpdateZoomText()
    {
        ZoomText.Text = $"{Math.Round(ImageScale.ScaleX * 100)}%";
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point mousePos = e.GetPosition(PreviewImage);
        double zoomFactor = e.Delta > 0 ? 1.15 : (1.0 / 1.15);

        double newScale = ImageScale.ScaleX * zoomFactor;
        if (newScale < 0.1 || newScale > 30)
            return;

        // Zoom relative to mouse cursor
        ImageTranslate.X = mousePos.X - (mousePos.X - ImageTranslate.X) * zoomFactor;
        ImageTranslate.Y = mousePos.Y - (mousePos.Y - ImageTranslate.Y) * zoomFactor;

        ImageScale.ScaleX = newScale;
        ImageScale.ScaleY = newScale;

        UpdateZoomText();
        e.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            _lastMousePosition = e.GetPosition(this);
            _isDragging = true;
            ViewportBorder.CaptureMouse();
        }
    }

    private void OnViewportDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (Math.Abs(ImageScale.ScaleX - 1.0) < 0.05)
            {
                // Zoom in to 2.0x on double click
                ImageScale.ScaleX = 2.0;
                ImageScale.ScaleY = 2.0;
            }
            else
            {
                ResetZoom();
            }
            UpdateZoomText();
            e.Handled = true;
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            ViewportBorder.ReleaseMouseCapture();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            Point currentPosition = e.GetPosition(this);
            double deltaX = currentPosition.X - _lastMousePosition.X;
            double deltaY = currentPosition.Y - _lastMousePosition.Y;

            ImageTranslate.X += deltaX;
            ImageTranslate.Y += deltaY;

            _lastMousePosition = currentPosition;
        }
    }

    private void OnResetZoomClicked(object sender, RoutedEventArgs e)
    {
        ResetZoom();
    }

    private void OnZoomInClicked(object sender, RoutedEventArgs e) => ZoomBy(1.25);
    private void OnZoomOutClicked(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);

    /// <summary>Zooms around the center of the viewport.</summary>
    public void ZoomBy(double factor)
    {
        double newScale = ImageScale.ScaleX * factor;
        if (newScale < 0.1 || newScale > 30) return;

        var center = new Point(PreviewImage.ActualWidth / 2, PreviewImage.ActualHeight / 2);
        ImageTranslate.X = center.X - (center.X - ImageTranslate.X) * factor;
        ImageTranslate.Y = center.Y - (center.Y - ImageTranslate.Y) * factor;
        ImageScale.ScaleX = ImageScale.ScaleY = newScale;
        UpdateZoomText();
    }

    /// <summary>Rotation applied in the preview, in degrees (0/90/180/270). Save As applies it to the file.</summary>
    public int Rotation => (int)ImageRotation.Angle;

    public void Rotate(int degrees)
    {
        ImageRotation.Angle = ((int)ImageRotation.Angle + degrees + 360) % 360;
        ResetZoom();
        FormatText.Text = _formatLabel + (Rotation != 0 ? $"  ·  obrócono {Rotation}°" : "");
    }

    private void OnRotateLeftClicked(object sender, RoutedEventArgs e) => Rotate(-90);
    private void OnRotateRightClicked(object sender, RoutedEventArgs e) => Rotate(90);

    private void OnCopyImageClicked(object sender, RoutedEventArgs e) => CopyImageToClipboard();

    /// <summary>Copies the (rotated) image as a bitmap, ready to paste into Photoshop, Messenger, etc.</summary>
    public async void CopyImageToClipboard()
    {
        if (PreviewImage.Source is not BitmapSource source) return;
        try
        {
            BitmapSource bitmap = Rotation == 0
                ? source
                : new TransformedBitmap(source, new System.Windows.Media.RotateTransform(Rotation));
            Clipboard.SetImage(bitmap);

            CopyImageIcon.Kind = "check";
            CopyImageButton.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1));
            await Task.Delay(1500);
            CopyImageIcon.Kind = "clipboard-copy";
            CopyImageButton.ClearValue(ForegroundProperty);
        }
        catch (Exception ex)
        {
            App.Log($"[ImageViewer] Copy to clipboard failed: {ex.Message}");
        }
    }

    // --- Info panel (I) ---

    private string? _currentPath;
    private static bool _infoOpen; // stays open while flipping through images
    private int _infoToken;

    private void OnInfoClicked(object sender, RoutedEventArgs e) => ToggleInfo();

    public void ToggleInfo()
    {
        _infoOpen = !_infoOpen;
        if (!_infoOpen) { InfoButton.ClearValue(ForegroundProperty); AnimateInfo(0); return; }
        if (_currentPath != null) _ = LoadInfoAsync(_currentPath);
    }

    /// <summary>Metadata is read in the background (headers only), then the panel grows to fit it.</summary>
    private async Task LoadInfoAsync(string path)
    {
        int token = ++_infoToken;
        InfoButton.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCB, 0xA6, 0xF7));
        List<QuickPeek.Core.InfoItem> items;
        try { items = await Task.Run(() => QuickPeek.Core.ImageInfo.Read(path)); }
        catch (Exception ex) { App.Log($"[ImageViewer] Info failed: {ex.Message}"); return; }
        if (token != _infoToken || !_infoOpen) return;

        InfoItems.ItemsSource = items;
        InfoItems.UpdateLayout();
        double width = Math.Max(100, InfoPanel.ActualWidth > 0 ? InfoPanel.ActualWidth : ActualWidth);
        InfoItems.Measure(new Size(width - InfoItems.Margin.Left - InfoItems.Margin.Right, double.PositiveInfinity));
        AnimateInfo(InfoItems.DesiredSize.Height + InfoItems.Margin.Top + InfoItems.Margin.Bottom + 1);
    }

    private void AnimateInfo(double to)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        InfoPanel.BeginAnimation(HeightProperty, anim);
    }

    /// <summary>Window resized with the panel open: re-fit its height (items may wrap into more rows).</summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_infoOpen || InfoItems.ItemsSource == null || !sizeInfo.WidthChanged) return;
        InfoItems.Measure(new Size(Math.Max(100, sizeInfo.NewSize.Width - InfoItems.Margin.Left - InfoItems.Margin.Right), double.PositiveInfinity));
        InfoPanel.BeginAnimation(HeightProperty, null);
        InfoPanel.Height = InfoItems.DesiredSize.Height + InfoItems.Margin.Top + InfoItems.Margin.Bottom + 1;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
