using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

using Lupik.Localization;
namespace Lupik.Views;

public partial class PdfViewer : UserControl
{
    private string _currentFilePath = "";
    private uint _totalPages = 0;
    private double _zoomScale = 1.0;
    private PdfDocument? _pdfDoc;

    public PdfViewer()
    {
        InitializeComponent();
    }

    private int _renderToken;

    /// <summary>
    /// Opens the PDF and returns as soon as the first page is on screen;
    /// the remaining pages keep rendering in the background.
    /// </summary>
    public async Task LoadPdfAsync(string filePath)
    {
        int token = ++_renderToken; // also stops a previous file's background rendering
        FormatBadgeText.Text = Path.GetExtension(filePath).Equals(".ai", StringComparison.OrdinalIgnoreCase) ? "AI" : "PDF";
        try
        {
            _currentFilePath = filePath;
            _pdfDoc = null;
            _totalPages = 0;
            PagesPanel.Children.Clear();
            PagesScrollViewer.ScrollToTop();

            var fileInfo = new FileInfo(filePath);
            FileSizeText.Text = FormatFileSize(fileInfo.Length);

            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
            var doc = await PdfDocument.LoadFromFileAsync(file);
            if (token != _renderToken) return;

            _pdfDoc = doc;
            _totalPages = _pdfDoc.PageCount;

            PageIndicatorText.Text = Loc.T("pdf.page", 1, _totalPages);
            _zoomScale = 1.0;
            ZoomPercentText.Text = "100%";

            await RenderPagesAsync(token, firstPageOnly: true);
            _ = RenderRemainingPagesAsync(token);
        }
        catch (Exception ex)
        {
            if (token != _renderToken) return;
            PageIndicatorText.Text = Loc.T("pdf.error");
            FileSizeText.Text = ex.Message;
        }
    }

    /// <summary>Drops rendered pages and the open document (called when Lupik goes idle).</summary>
    public void Release()
    {
        _renderToken++; // stops background rendering
        PagesPanel.Children.Clear();
        _pdfDoc = null;
        _totalPages = 0;
    }

    private async Task RenderRemainingPagesAsync(int token)
    {
        try
        {
            await RenderPagesAsync(token, firstPageOnly: false, startIndex: 1);
        }
        catch (Exception ex)
        {
            App.Log($"[PdfViewer] Background page rendering failed: {ex.Message}");
        }
    }

    private async Task RerenderAllAsync()
    {
        int token = ++_renderToken;
        PagesPanel.Children.Clear();
        await RenderPagesAsync(token, firstPageOnly: false);
    }

    private async Task RenderPagesAsync(int token, bool firstPageOnly, uint startIndex = 0)
    {
        if (_pdfDoc == null) return;

        // Render first 50 pages maximum for instant responsiveness
        uint count = firstPageOnly ? Math.Min(_totalPages, 1) : Math.Min(_totalPages, 50);

        for (uint i = startIndex; i < count; i++)
        {
            if (token != _renderToken) return; // user moved on to another file or zoom level

            using var page = _pdfDoc.GetPage(i);
            using var stream = new InMemoryRandomAccessStream();

            var renderOptions = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(100, page.Size.Width * 1.5 * _zoomScale)
            };

            await page.RenderToStreamAsync(stream, renderOptions);
            if (token != _renderToken) return;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream.AsStream();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            var img = new Image
            {
                Source = bitmap,
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var border = new Border
            {
                Child = img,
                Margin = new Thickness(0, 0, 0, 16),
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(49, 50, 68)),
                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 12,
                    Opacity = 0.35,
                    ShadowDepth = 3
                }
            };

            PagesPanel.Children.Add(border);
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_totalPages == 0 || PagesPanel.Children.Count == 0) return;

        double offset = PagesScrollViewer.VerticalOffset;
        double totalHeight = PagesScrollViewer.ExtentHeight;

        if (totalHeight > 0)
        {
            int pageIndex = (int)Math.Min(_totalPages, Math.Max(1, Math.Round((offset / totalHeight) * _totalPages) + 1));
            PageIndicatorText.Text = Loc.T("pdf.page", pageIndex, _totalPages);
        }
    }

    private async void OnZoomInClicked(object sender, RoutedEventArgs e)
    {
        if (_zoomScale < 3.0)
        {
            _zoomScale += 0.25;
            ZoomPercentText.Text = $"{Math.Round(_zoomScale * 100)}%";
            await RerenderAllAsync();
        }
    }

    private async void OnZoomOutClicked(object sender, RoutedEventArgs e)
    {
        if (_zoomScale > 0.5)
        {
            _zoomScale -= 0.25;
            ZoomPercentText.Text = $"{Math.Round(_zoomScale * 100)}%";
            await RerenderAllAsync();
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Core.KeyState.Modifiers == ModifierKeys.Control)
        {
            if (e.Delta > 0)
                OnZoomInClicked(sender, e);
            else
                OnZoomOutClicked(sender, e);

            e.Handled = true;
        }
    }

    private void OnOpenExternalClicked(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_currentFilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_currentFilePath) { UseShellExecute = true });
            }
            catch
            {
                // Ignore error opening external app
            }
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
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
