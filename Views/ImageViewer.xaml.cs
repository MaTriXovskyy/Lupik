using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;

using Lupik.Localization;
namespace Lupik.Views;

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
            // The shown image stays cached too: reopening it (Space, Space) is instant instead of a fresh decode
            var decoding = GetDecodeTask(filePath);
            Remember(filePath, decoding);

            // A slow decode (big PNG, PSD, AVIF...): show a quick stand-in first, the real picture replaces it
            if (await Task.WhenAny(decoding, Task.Delay(QuickPreviewAfterMs)) != decoding)
            {
                var quick = await QuickPreviewAsync(filePath);
                if (token != _loadToken) return false;
                if (quick is var (preview, size0, w0, h0) && !decoding.IsCompleted)
                {
                    Show(filePath, preview, size0, w0, h0, placeholder: true);
                    _ = ReplacePlaceholderAsync(decoding, token);
                    return true;
                }
            }

            var (bitmap, size, width, height) = await decoding;
            if (token != _loadToken) return false;
            Show(filePath, bitmap, size, width, height, placeholder: false);
            return true;
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return false;

            PreviewImage.Source = null;
            _naturalWidth = _naturalHeight = 0;
            DimensionsText.Text = Loc.T("image.readError");
            FileSizeText.Text = ex.Message;
            return true;
        }
    }

    private void Show(string filePath, BitmapSource bitmap, long size, int width, int height, bool placeholder)
    {
        _naturalWidth = width;
        _naturalHeight = height;

        PreviewImage.Source = bitmap;
        _showingPlaceholder = placeholder;
        _detailRequested = false;
        ImageRotation.Angle = 0; // rotation is per file
        ResetZoom();

        FileSizeText.Text = FormatFileSize(size);
        _formatLabel = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        FormatText.Text = _formatLabel;
        DimensionsText.Text = $"{_naturalWidth:F0} × {_naturalHeight:F0} px";
        _currentPath = filePath;
        if (_infoOpen) _ = LoadInfoAsync(filePath);
    }

    // --- Quick stand-in while a slow format decodes ---

    private const int QuickPreviewAfterMs = 60; // faster decodes just appear; a stand-in would only flicker
    private bool _showingPlaceholder;

    private async Task ReplacePlaceholderAsync(Task<(BitmapSource, long, int, int)> decoding, int token)
    {
        try
        {
            var (bitmap, _, _, _) = await decoding;
            if (token != _loadToken || !_showingPlaceholder) return;
            PreviewImage.Source = bitmap;
            _showingPlaceholder = false;
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            App.Log($"[ImageViewer] Decode after stand-in failed: {ex.Message}");
            DimensionsText.Text = Loc.T("image.readError");
        }
    }

    /// <summary>
    /// Something to show right away, with the real pixel size: Explorer's cached thumbnail if it has one
    /// (only from its cache: generating one would be as slow as decoding), else the JPEG's own EXIF thumbnail.
    /// </summary>
    private static async Task<(BitmapSource, long, int, int)?> QuickPreviewAsync(string filePath)
    {
        try
        {
            var header = await Task.Run(() => ReadHeader(filePath));
            if (header is not var (w, h, size, embedded)) return null;
            var cached = await Lupik.Core.ShellThumbnails.FromCacheAsync(filePath, 1024);
            var preview = cached ?? embedded;
            return preview == null ? null : (preview, size, w, h);
        }
        catch (Exception ex)
        {
            App.Log($"[ImageViewer] No quick preview: {ex.Message}");
            return null;
        }
    }

    /// <summary>Pixel size (and the embedded thumbnail, if any) from the file header, without decoding the image.</summary>
    private static (int, int, long, BitmapSource?)? ReadHeader(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        long size = new FileInfo(filePath).Length;
        if (ext == ".svg" || IsPostScript(ext)) return null; // rendered, their size depends on the rendering
        if (MagickOnlyFormats.Contains(ext))
        {
            var info = new ImageMagick.MagickImageInfo(filePath);
            return ((int)info.Width, (int)info.Height, size, null);
        }
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        BitmapSource? thumbnail = null;
        try
        {
            if (frame.Thumbnail is BitmapSource t) { thumbnail = new WriteableBitmap(t); thumbnail.Freeze(); }
        }
        catch
        {
            // no embedded thumbnail
        }
        return (frame.PixelWidth, frame.PixelHeight, size, thumbnail);
    }

    // --- Decoding: JPEGs at the window's size first, full resolution when zooming in ---

    /// <summary>
    /// The most image a fitted preview can show, in real pixels: the largest work area, as far as the window may grow.
    /// </summary>
    private static (int Width, int Height) PreviewBox()
    {
        int w = 1280, h = 720;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            w = Math.Max(w, screen.WorkingArea.Width);
            h = Math.Max(h, screen.WorkingArea.Height);
        }
        double scale = 0.95 * Lupik.Core.Settings.Current.WindowScale;
        return ((int)(w * scale), (int)(h * scale));
    }

    /// <summary>
    /// Width to decode a JPEG at. JPEG can shrink by 2, 4 or 8 while decoding, which is much faster than a full decode;
    /// any other size means a full decode plus a resize (slower than no resize at all). So: the smallest such step that
    /// still fills the preview window, or null for none. Full resolution: capped at <see cref="MaxDecodeSize"/>.
    /// </summary>
    private static int? JpegDecodeWidth(int w, int h, bool full)
    {
        if (!full)
        {
            var (boxW, boxH) = PreviewBox();
            double fit = Math.Min(1, Math.Min((double)boxW / w, (double)boxH / h));
            int shrink = 1;
            while (shrink < 8 && w / (shrink * 2) >= w * fit && h / (shrink * 2) >= h * fit) shrink *= 2;
            if (shrink > 1 && Math.Max(w, h) / shrink <= MaxDecodeSize) return w / shrink;
        }
        return CappedWidth(w, h);
    }

    /// <summary>Width that keeps the longest side within <see cref="MaxDecodeSize"/> (null: fits already).</summary>
    private static int? CappedWidth(int w, int h) =>
        Math.Max(w, h) <= MaxDecodeSize ? null : Math.Max(1, (int)Math.Round(w * (double)MaxDecodeSize / Math.Max(w, h)));

    private static bool IsJpeg(string ext) => ext is ".jpg" or ".jpeg" or ".jfif";

    private static (BitmapSource, long, int, int) Decode(string filePath) => Decode(filePath, full: false);

    private static (BitmapSource, long, int, int) Decode(string filePath, bool full)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".svg") return DecodeSvg(filePath);
        if (IsPostScript(ext)) return DecodeEps(filePath);
        if (MagickOnlyFormats.Contains(ext)) return DecodeWithMagick(filePath);
        try
        {
            return DecodeWithWic(filePath, full);
        }
        catch (Exception ex)
        {
            // Damaged file or missing Windows codec: Magick.NET is more forgiving
            App.Log($"[ImageViewer] Windows decoder failed ({ex.Message}), falling back to Magick.NET");
            return DecodeWithMagick(filePath);
        }
    }

    private bool _detailRequested;

    /// <summary>Zoomed in (or rotated) past what the window-sized decode holds: swap in the full resolution.</summary>
    private async void EnsureDetail()
    {
        if (_detailRequested || _currentPath == null || PreviewImage.Source is not BitmapSource shown) return;
        if (!_showingPlaceholder && !IsReduced(shown)) return;
        _detailRequested = true;
        int token = _loadToken;
        string path = _currentPath;
        try
        {
            var (bitmap, _, _, _) = await Task.Run(() => Decode(path, full: true));
            if (token != _loadToken) return;
            PreviewImage.Source = bitmap;
            _showingPlaceholder = false;
        }
        catch (Exception ex)
        {
            App.Log($"[ImageViewer] Full-resolution decode failed: {ex.Message}");
        }
    }

    /// <summary>Smaller than the full-resolution decode would be.</summary>
    private bool IsReduced(BitmapSource shown) =>
        Math.Max(shown.PixelWidth, shown.PixelHeight) < Math.Min(MaxDecodeSize, Math.Max(_naturalWidth, _naturalHeight)) - 1;

    /// <summary>The image at full (decode) resolution, e.g. for the clipboard.</summary>
    private async Task<BitmapSource?> FullBitmapAsync()
    {
        if (PreviewImage.Source is not BitmapSource shown || _currentPath == null) return null;
        if (!_showingPlaceholder && !IsReduced(shown)) return shown;
        string path = _currentPath;
        var (bitmap, _, _, _) = await Task.Run(() => Decode(path, full: true));
        return bitmap;
    }

    // --- Preload cache: neighbors of the current image are decoded ahead of time ---

    private sealed record CacheEntry(DateTime LastWrite, Task<(BitmapSource, long, int, int)> Decoding);

    private const int MaxCachedImages = 4; // decoded images are capped at 4096 px, so ≤ ~64 MB each
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _cacheOrder = new();

    /// <summary>Drops the shown image and all preloaded ones (called when Lupik goes idle).</summary>
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
            Remember(filePath, decoding, lastWrite);
        }
        catch (Exception ex)
        {
            App.Log($"[ImageViewer] Preload skipped: {ex.Message}");
        }
    }

    /// <summary>Puts a decode (finished or running) into the small most-recently-used cache.</summary>
    private void Remember(string filePath, Task<(BitmapSource, long, int, int)> decoding, DateTime? lastWrite = null)
    {
        try
        {
            var stamp = lastWrite ?? File.GetLastWriteTimeUtc(filePath);
            if (!_cache.TryGetValue(filePath, out var existing) || existing.Decoding != decoding)
                _cache[filePath] = new CacheEntry(stamp, decoding);
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
            App.Log($"[ImageViewer] Cache skipped: {ex.Message}");
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
    private static (BitmapSource, long, int, int) DecodeWithWic(string filePath, bool full)
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
        // Huge images are decoded at a reduced size; JPEGs just big enough for the window (see JpegDecodeWidth)
        int? width = IsJpeg(Path.GetExtension(filePath).ToLowerInvariant()) ? JpegDecodeWidth(w, h, full) : CappedWidth(w, h);
        if (width is int decodeWidth) bmp.DecodePixelWidth = decodeWidth;
        bmp.EndInit();
        bmp.Freeze();
        return (bmp, stream.Length, w, h);
    }

    internal static bool IsPostScript(string ext) => ext is ".eps" or ".epsf" or ".epsi" or ".ps";

    /// <summary>EPS / PS: drawn by Lupik's own PostScript interpreter (Core/PostScript), no Ghostscript needed.</summary>
    private static (BitmapSource, long, int, int) DecodeEps(string filePath)
    {
        byte[] data = File.ReadAllBytes(filePath);
        var result = Core.PostScript.EpsRenderer.Render(data, minSize: 1024, maxSize: MaxDecodeSize);
        return (result.Bitmap, data.Length, result.PixelWidth, result.PixelHeight);
    }

    /// <summary>
    /// For Save As / print: a Magick image of the file. Formats Magick can't read without extra software
    /// (EPS needs Ghostscript) are rendered here first and handed over as PNG.
    /// </summary>
    internal static ImageMagick.MagickImage OpenForExport(string filePath)
    {
        if (!IsPostScript(Path.GetExtension(filePath).ToLowerInvariant())) return new ImageMagick.MagickImage(filePath);
        var (bitmap, _, _, _) = DecodeEps(filePath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        return new ImageMagick.MagickImage(png.ToArray());
    }

    /// <summary>
    /// SVG is rendered by Magick.NET's bundled librsvg. Vectors have no pixel size of their own, so small ones
    /// (icons, logos) are rasterized at a higher density to stay sharp; the reported size is the rendered one.
    /// </summary>
    private static (BitmapSource, long, int, int) DecodeSvg(string filePath)
    {
        const int MinRenderSize = 1024;
        byte[] data = File.ReadAllBytes(filePath);

        var settings = new ImageMagick.MagickReadSettings
        {
            Format = ImageMagick.MagickFormat.Svg,
            BackgroundColor = ImageMagick.MagickColors.Transparent,
            Density = new ImageMagick.Density(96),
        };
        var probe = new ImageMagick.MagickImageInfo(data, settings); // nominal size at 96 DPI
        double longest = Math.Max(1, Math.Max(probe.Width, probe.Height));
        double scale = Math.Clamp(MinRenderSize / longest, 1, MaxDecodeSize / longest);
        settings.Density = new ImageMagick.Density(96 * scale);

        using var image = new ImageMagick.MagickImage(data, settings);
        return (ToBitmapSource(image), data.Length, (int)image.Width, (int)image.Height);
    }

    /// <summary>
    /// HEIC/HEIF (e.g. iPhone photos) are HEVC-compressed; Windows can only decode them with a paid Store codec,
    /// so they go through the bundled Magick.NET decoder instead.
    /// </summary>
    private static (BitmapSource, long, int, int) DecodeWithMagick(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var image = new ImageMagick.MagickImage(stream, FirstImageOnly);
        image.AutoOrient(); // phone photos store rotation as metadata
        return (ToBitmapSource(image, MaxDecodeSize), stream.Length, (int)image.Width, (int)image.Height);
    }

    /// <summary>Only the first image: for a PSD that's the flattened composite, and Magick then skips the layers.</summary>
    internal static ImageMagick.MagickReadSettings FirstImageOnly => new() { FrameIndex = 0, FrameCount = 1 };

    /// <summary>
    /// Magick's pixels straight into a WPF bitmap (no PNG/BMP in between for WPF to decode again). Bigger than
    /// <paramref name="maxSize"/>: shrunk with Windows' scaler, several times faster than Magick's resize.
    /// </summary>
    internal static BitmapSource ToBitmapSource(ImageMagick.IMagickImage<byte> image, int maxSize = int.MaxValue)
    {
        int w = (int)image.Width, h = (int)image.Height;
        bool alpha = image.HasAlpha;
        byte[] data;
        using (var pixels = image.GetPixelsUnsafe()) data = pixels.ToByteArray(ImageMagick.PixelMapping.BGRA)!;
        BitmapSource bitmap = BitmapSource.Create(w, h, 96, 96, alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32, null, data, w * 4);
        if (Math.Max(w, h) > maxSize)
        {
            double scale = (double)maxSize / Math.Max(w, h);
            bitmap = new WriteableBitmap(new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)));
        }
        bitmap.Freeze();
        return bitmap;
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
        if (newScale > 1) EnsureDetail();

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
                EnsureDetail();
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
        if (newScale > 1) EnsureDetail();
        UpdateZoomText();
    }

    /// <summary>Rotation applied in the preview, in degrees (0/90/180/270). Save As applies it to the file.</summary>
    public int Rotation => (int)ImageRotation.Angle;

    public void Rotate(int degrees)
    {
        ImageRotation.Angle = ((int)ImageRotation.Angle + degrees + 360) % 360;
        ResetZoom();
        if (Rotation is 90 or 270) EnsureDetail();
        FormatText.Text = _formatLabel + (Rotation != 0 ? "  ·  " + Loc.T("image.rotated", Rotation) : "");
    }

    private void OnRotateLeftClicked(object sender, RoutedEventArgs e) => Rotate(-90);
    private void OnRotateRightClicked(object sender, RoutedEventArgs e) => Rotate(90);

    private void OnCopyImageClicked(object sender, RoutedEventArgs e) => CopyImageToClipboard();

    /// <summary>Copies the (rotated) image as a bitmap, ready to paste into Photoshop, Messenger, etc.</summary>
    public async void CopyImageToClipboard()
    {
        if (PreviewImage.Source == null) return;
        try
        {
            if (await FullBitmapAsync() is not BitmapSource source) return;
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

    // --- Crop ---

    private enum CropDrag { None, New, Move, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right }
    private CropDrag _cropDrag;
    private Rect _imageBounds; // the displayed image, in CropLayer coordinates
    private Rect _crop;
    private Point _cropAnchor;
    private Point _lastCropPoint;
    private Rect _cropAtDragStart;

    public bool IsCropping => CropLayer.Visibility == Visibility.Visible;

    /// <summary>Starts crop mode on the whole (fitted) image.</summary>
    public void BeginCrop()
    {
        if (PreviewImage.Source == null) return;
        ResetZoom();
        CropLayer.Visibility = Visibility.Visible;
        UpdateLayout();
        _imageBounds = DisplayedImageBounds();
        _crop = _imageBounds;
        RedrawCrop();
    }

    public void CancelCrop() => CropLayer.Visibility = Visibility.Collapsed;

    /// <summary>The selection as fractions (0..1) of the image as shown, i.e. after rotation.</summary>
    public Rect CropFraction =>
        _imageBounds.Width <= 0 || _imageBounds.Height <= 0 ? new Rect(0, 0, 1, 1) : new Rect(
            (_crop.X - _imageBounds.X) / _imageBounds.Width, (_crop.Y - _imageBounds.Y) / _imageBounds.Height,
            _crop.Width / _imageBounds.Width, _crop.Height / _imageBounds.Height);

    private Rect DisplayedImageBounds() =>
        PreviewImage.TransformToVisual(CropLayer).TransformBounds(new Rect(PreviewImage.RenderSize));

    private void OnCropLayerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsCropping) return;
        var f = CropFraction; // keep the same part of the image selected
        _imageBounds = DisplayedImageBounds();
        _crop = new Rect(_imageBounds.X + f.X * _imageBounds.Width, _imageBounds.Y + f.Y * _imageBounds.Height,
            f.Width * _imageBounds.Width, f.Height * _imageBounds.Height);
        RedrawCrop();
    }

    /// <summary>What the mouse is over: a corner, an edge (anywhere along it), the inside, or nothing.</summary>
    private CropDrag HitCrop(Point p)
    {
        const double corner = 18, edge = 10;
        var r = _crop;
        bool Near(Point c) => Math.Abs(p.X - c.X) <= corner && Math.Abs(p.Y - c.Y) <= corner;
        if (Near(r.TopLeft)) return CropDrag.TopLeft;
        if (Near(r.TopRight)) return CropDrag.TopRight;
        if (Near(r.BottomLeft)) return CropDrag.BottomLeft;
        if (Near(r.BottomRight)) return CropDrag.BottomRight;
        bool withinX = p.X >= r.Left - edge && p.X <= r.Right + edge;
        bool withinY = p.Y >= r.Top - edge && p.Y <= r.Bottom + edge;
        if (withinX && Math.Abs(p.Y - r.Top) <= edge) return CropDrag.Top;
        if (withinX && Math.Abs(p.Y - r.Bottom) <= edge) return CropDrag.Bottom;
        if (withinY && Math.Abs(p.X - r.Left) <= edge) return CropDrag.Left;
        if (withinY && Math.Abs(p.X - r.Right) <= edge) return CropDrag.Right;
        return r.Contains(p) ? CropDrag.Move : CropDrag.New;
    }

    private void OnCropMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(CropLayer);
        _cropDrag = HitCrop(p);
        _cropAnchor = _cropDrag == CropDrag.New ? ClampToImage(p) : p;
        _cropAtDragStart = _crop;
        _lastCropPoint = p;
        CropLayer.CaptureMouse();
        e.Handled = true;
    }

    private void OnCropMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(CropLayer);
        if (_cropDrag == CropDrag.None)
        {
            CropLayer.Cursor = HitCrop(p) switch
            {
                CropDrag.TopLeft or CropDrag.BottomRight => Cursors.SizeNWSE,
                CropDrag.TopRight or CropDrag.BottomLeft => Cursors.SizeNESW,
                CropDrag.Top or CropDrag.Bottom => Cursors.SizeNS,
                CropDrag.Left or CropDrag.Right => Cursors.SizeWE,
                CropDrag.Move => Cursors.SizeAll,
                _ => Cursors.Cross,
            };
            return;
        }
        _lastCropPoint = p;
        UpdateCropDrag();
    }

    /// <summary>Shift / Alt pressed or released mid-drag: apply them right away, like Photoshop.</summary>
    public void CropModifiersChanged()
    {
        if (IsCropping && _cropDrag != CropDrag.None) UpdateCropDrag();
    }

    private void UpdateCropDrag()
    {
        var p = _lastCropPoint;
        var s = _cropAtDragStart;
        if (_cropDrag == CropDrag.Move)
        {
            double x = Math.Clamp(s.X + p.X - _cropAnchor.X, _imageBounds.Left, _imageBounds.Right - s.Width);
            double y = Math.Clamp(s.Y + p.Y - _cropAnchor.Y, _imageBounds.Top, _imageBounds.Bottom - s.Height);
            _crop = new Rect(x, y, s.Width, s.Height);
        }
        else
        {
            var mods = Core.KeyState.Modifiers;
            _crop = ResizeCrop(ClampToImage(p), keepRatio: (mods & ModifierKeys.Shift) != 0, fromCenter: (mods & ModifierKeys.Alt) != 0);
        }
        RedrawCrop();
    }

    /// <summary>
    /// Resizes from the grabbed corner/edge. Shift keeps the proportions (a square when drawing a new frame),
    /// Alt resizes around the center (around the click point for a new frame); both together combine.
    /// </summary>
    private Rect ResizeCrop(Point q, bool keepRatio, bool fromCenter)
    {
        var s = _cropAtDragStart;
        bool isNew = _cropDrag == CropDrag.New;
        bool movesLeft = _cropDrag is CropDrag.TopLeft or CropDrag.BottomLeft or CropDrag.Left;
        bool movesTop = _cropDrag is CropDrag.TopLeft or CropDrag.TopRight or CropDrag.Top;
        bool horizontal = isNew || _cropDrag is not (CropDrag.Top or CropDrag.Bottom);
        bool vertical = isNew || _cropDrag is not (CropDrag.Left or CropDrag.Right);
        double ratio = isNew || s.Height <= 0 ? 1 : s.Width / s.Height;

        var center = isNew ? _cropAnchor : new Point(s.X + s.Width / 2, s.Y + s.Height / 2);
        // The fixed side: the opposite corner/edge (or the click point for a new frame)
        double fixedX = isNew ? _cropAnchor.X : movesLeft ? s.Right : s.Left;
        double fixedY = isNew ? _cropAnchor.Y : movesTop ? s.Bottom : s.Top;

        double w, h;
        if (fromCenter)
        {
            w = horizontal ? 2 * Math.Abs(q.X - center.X) : s.Width;
            h = vertical ? 2 * Math.Abs(q.Y - center.Y) : s.Height;
        }
        else
        {
            w = horizontal ? Math.Abs(q.X - fixedX) : s.Width;
            h = vertical ? Math.Abs(q.Y - fixedY) : s.Height;
        }

        if (keepRatio)
        {
            if (horizontal && vertical) { if (w / ratio > h) h = w / ratio; else w = h * ratio; }
            else if (horizontal) h = w / ratio;
            else w = h * ratio;
        }

        double x, y;
        if (fromCenter)
        {
            x = center.X - w / 2;
            y = center.Y - h / 2;
        }
        else
        {
            x = horizontal ? (q.X < fixedX ? fixedX - w : fixedX) : center.X - w / 2;
            y = vertical ? (q.Y < fixedY ? fixedY - h : fixedY) : center.Y - h / 2;
        }

        var r = new Rect(x, y, w, h);
        r.Intersect(_imageBounds);
        return r.IsEmpty ? new Rect(q, q) : r;
    }

    private void OnCropMouseUp(object sender, MouseButtonEventArgs e)
    {
        CropLayer.ReleaseMouseCapture();
        _cropDrag = CropDrag.None;
        if (_crop.Width < 4 || _crop.Height < 4) _crop = _imageBounds; // a click, not a drag: back to everything
        RedrawCrop();
    }

    private Point ClampToImage(Point p) => new(
        Math.Clamp(p.X, _imageBounds.Left, _imageBounds.Right),
        Math.Clamp(p.Y, _imageBounds.Top, _imageBounds.Bottom));

    private void RedrawCrop()
    {
        var full = new RectangleGeometry(new Rect(0, 0, CropLayer.ActualWidth, CropLayer.ActualHeight));
        CropShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(_crop));

        Canvas.SetLeft(CropFrame, _crop.X);
        Canvas.SetTop(CropFrame, _crop.Y);
        CropFrame.Width = _crop.Width;
        CropFrame.Height = _crop.Height;

        void Place(FrameworkElement h, Point c) { Canvas.SetLeft(h, c.X - h.Width / 2); Canvas.SetTop(h, c.Y - h.Height / 2); }
        Place(HandleTL, _crop.TopLeft);
        Place(HandleTR, _crop.TopRight);
        Place(HandleBL, _crop.BottomLeft);
        Place(HandleBR, _crop.BottomRight);
        Place(HandleT, new Point(_crop.X + _crop.Width / 2, _crop.Top));
        Place(HandleB, new Point(_crop.X + _crop.Width / 2, _crop.Bottom));
        Place(HandleL, new Point(_crop.Left, _crop.Y + _crop.Height / 2));
        Place(HandleR, new Point(_crop.Right, _crop.Y + _crop.Height / 2));

        // Pixel size of the selection, next to the key hints
        var f = CropFraction;
        bool sideways = Rotation is 90 or 270;
        double w = (sideways ? _naturalHeight : _naturalWidth) * f.Width, h = (sideways ? _naturalWidth : _naturalHeight) * f.Height;
        CropSizeRun.Text = $"{w:0} × {h:0} px";

        CropHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(CropHint, Math.Max(8, (CropLayer.ActualWidth - CropHint.DesiredSize.Width) / 2));
        Canvas.SetTop(CropHint, 12);
    }

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
        InfoButton.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE3, 0xB3, 0x41));
        List<Lupik.Core.InfoItem> items;
        try { items = await Task.Run(() => Lupik.Core.ImageInfo.Read(path)); }
        catch (Exception ex) { App.Log($"[ImageViewer] Info failed: {ex.Message}"); return; }
        if (token != _infoToken || !_infoOpen) return;

        InfoItems.ItemsSource = items;
        InfoItems.UpdateLayout();
        double width = Math.Max(100, InfoPanel.ActualWidth > 0 ? InfoPanel.ActualWidth : ActualWidth);
        InfoItems.Measure(new Size(width - InfoItems.Margin.Left - InfoItems.Margin.Right, double.PositiveInfinity));
        AnimateInfo(InfoItems.DesiredSize.Height + InfoItems.Margin.Top + InfoItems.Margin.Bottom + 1);
    }

    /// <summary>The centered key hints only show when they fit between the left and right footer groups.</summary>
    private void OnFooterLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        const double gap = 16;
        double hints = FooterHints.ActualWidth;
        double half = (FooterGrid.ActualWidth - hints) / 2; // space on each side of the centered hints
        bool fits = half >= FooterLeft.ActualWidth + gap && half >= FooterRight.ActualWidth + gap;
        var visibility = fits ? Visibility.Visible : Visibility.Hidden; // Hidden keeps its size for this check
        if (FooterHints.Visibility != visibility) FooterHints.Visibility = visibility;
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
