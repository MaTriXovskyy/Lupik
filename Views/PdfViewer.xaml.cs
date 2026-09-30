using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>
/// PDF (and PDF-compatible .ai) pages, rendered by PDFium. At 100% the whole first page fits the window; every page
/// has a placeholder of its real size, and only the pages on screen (plus a neighbour) are rendered, at the
/// monitor's actual resolution, so long documents open instantly and stay sharp at any zoom.
/// </summary>
public partial class PdfViewer : UserControl, ISearchable
{
    private const double PageGap = 16;       // between pages (and above the first)
    private const double SideMargin = 16;    // PagesPanel's left/right margin
    private const int KeepAround = 4;        // rendered pages kept beyond the visible ones (released further away)
    private const int RenderAhead = 2;       // pages below the visible ones rendered in advance: paging down is instant
    private const int MaxPixelWidth = 6000;  // one page bitmap at 400% on a 4K screen stays below ~150 MB

    private sealed class PageSlot
    {
        public required FrameworkElement Frame;
        public required Image Image;
        public required Border Page;
        public required SearchLayer Highlights;
        public int PixelWidth, PixelHeight; // the image's size on screen, in device pixels
        public double PointsWidth, PointsHeight;
        public int RenderedPixelWidth;   // 0 = not rendered
    }

    private string _currentFilePath = "";
    private PdfDocument? _pdfDoc;
    private readonly List<PageSlot> _slots = new();
    private double _widestPagePoints = 1;
    private double _zoomScale = 1.0;
    private double _layoutWidth, _layoutHeight; // viewport size the page sizes were computed for
    private int _loadToken;          // bumped per file: stale renders are dropped
    private bool _rendering;
    private readonly DispatcherTimer _resizeTimer;

    public PdfViewer()
    {
        InitializeComponent();
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); Relayout(keepPage: true); };
    }

    public int PageCount => _slots.Count;

    /// <summary>Opens the PDF and returns once the first page is on screen; others render as they scroll into view.</summary>
    public async Task LoadPdfAsync(string filePath)
    {
        int token = ++_loadToken;
        FormatBadgeText.Text = Path.GetExtension(filePath).Equals(".ai", StringComparison.OrdinalIgnoreCase) ? "AI" : "PDF";
        try
        {
            _currentFilePath = filePath;
            ClosePages();
            PagesScrollViewer.ScrollToTop();
            PagesScrollViewer.ScrollToLeftEnd();
            FileSizeText.Text = FormatFileSize(new FileInfo(filePath).Length);

            var (doc, sizes) = await Task.Run(() =>
            {
                var d = PdfDocument.Open(filePath);
                var list = new List<(double, double)>(d.PageCount);
                for (int i = 0; i < d.PageCount; i++) list.Add(d.PageSizePoints(i));
                return (d, list);
            });
            if (token != _loadToken) { doc.Dispose(); return; }

            _pdfDoc = doc;
            _zoomScale = 1.0;
            ZoomPercentText.Text = "100%";
            _widestPagePoints = 1;
            foreach (var (w, _) in sizes) _widestPagePoints = Math.Max(_widestPagePoints, w);
            foreach (var (w, h) in sizes) _slots.Add(CreateSlot(w, h));

            Relayout(keepPage: false);
            UpdatePageIndicator();
            // Return once the visible page(s) are drawn, so the window shows a finished page; the neighbours follow
            var visibleDone = new TaskCompletionSource();
            _ = RenderVisibleAsync(visibleDone);
            await visibleDone.Task;
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            App.Log($"[PdfViewer] Could not open '{filePath}': {ex.Message}");
            PageNavigation.Visibility = Visibility.Collapsed;
            FileSizeText.Text = ex is PdfException { Error: PdfError.Password } ? Loc.T("pdf.password") : Loc.T("pdf.error") + ": " + ex.Message;
        }
    }

    /// <summary>Drops rendered pages and the open document (called when Lupik goes idle).</summary>
    public void Release()
    {
        _loadToken++;
        ClosePages();
    }

    private void ClosePages()
    {
        _searchMatches.Clear();
        PagesPanel.Children.Clear();
        _slots.Clear();
        _pdfDoc?.Dispose();
        _pdfDoc = null;
        CancelPageEntry();
        PageBoxText.Text = "";
        PageTotalText.Text = "";
        PageNavigation.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// A page: the shadow is a separate layer underneath (an Effect on the page itself would send the page through
    /// an extra bitmap and soften it); the page image sits inside a 1 px outline, pixel for pixel.
    /// </summary>
    private static PageSlot CreateSlot(double w, double h)
    {
        var image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var shadow = new Border
        {
            Background = Brushes.White,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, Opacity = 0.35, ShadowDepth = 3 },
        };
        var highlights = new SearchLayer();
        var page = new Border
        {
            Child = new Grid { Children = { image, highlights } },
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x36, 0x31)),
            BorderThickness = new Thickness(1),
        };
        var frame = new Grid { Margin = new Thickness(0, 0, 0, PageGap), Children = { shadow, page } };
        return new PageSlot { Frame = frame, Image = image, Page = page, Highlights = highlights, PointsWidth = w, PointsHeight = h };
    }

    // ---------- Layout ----------

    /// <summary>
    /// 100% = the whole first page on screen: as wide as the viewport allows, but no taller than it
    /// (then zoom in for details). Returned as the width of the widest page.
    /// </summary>
    private double FitWidth()
    {
        double viewportW = PagesScrollViewer.ViewportWidth > 0 ? PagesScrollViewer.ViewportWidth : PagesScrollViewer.ActualWidth - 18;
        double viewportH = PagesScrollViewer.ViewportHeight > 0 ? PagesScrollViewer.ViewportHeight : PagesScrollViewer.ActualHeight;
        double byWidth = viewportW - 2 * SideMargin - 2;
        if (_slots.Count > 0 && viewportH > 0)
        {
            var first = _slots[0];
            double firstHeightPerWidth = first.PointsHeight / Math.Max(1, first.PointsWidth);
            double firstWidthShare = first.PointsWidth / _widestPagePoints;
            double byHeight = (viewportH - 2 * PageGap) / firstHeightPerWidth / firstWidthShare;
            byWidth = Math.Min(byWidth, byHeight);
        }
        return Math.Max(200, byWidth);
    }

    /// <summary>Sizes every placeholder for the current width and zoom; renders what's now on screen.</summary>
    private void Relayout(bool keepPage)
    {
        if (_slots.Count == 0) return;
        int page = keepPage ? CurrentPage() : 0;
        double pageFraction = keepPage ? FractionIntoPage(page) : 0;

        _layoutWidth = PagesScrollViewer.ViewportWidth;
        _layoutHeight = PagesScrollViewer.ViewportHeight;
        double baseWidth = FitWidth() * _zoomScale;
        if (PagesPanel.Children.Count != _slots.Count)
        {
            PagesPanel.Children.Clear();
            foreach (var slot in _slots) PagesPanel.Children.Add(slot.Frame);
        }
        // Sizes are worked out in device pixels, so at 125% / 150% scaling the page image still lands exactly on
        // whole pixels (a 1 DIP outline would be 1.25 px there and push the image off by a fraction)
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var slot in _slots)
        {
            double w = baseWidth * slot.PointsWidth / _widestPagePoints;
            int outerW = (int)Math.Round(w * dpi);
            int outerH = (int)Math.Round(outerW * slot.PointsHeight / Math.Max(1, slot.PointsWidth));
            slot.PixelWidth = Math.Min(MaxPixelWidth, Math.Max(16, outerW - 2));
            slot.PixelHeight = Math.Max(16, outerH - 2);
            slot.Page.BorderThickness = new Thickness(1 / dpi); // one device pixel
            slot.Frame.Width = outerW / dpi;
            slot.Frame.Height = outerH / dpi;
        }
        PagesPanel.UpdateLayout();

        if (keepPage) PagesScrollViewer.ScrollToVerticalOffset(PageTop(page) + pageFraction * _slots[page].Frame.Height);
        _ = RenderVisibleAsync();
    }

    private double PageTop(int index)
    {
        double y = PagesPanel.Margin.Top;
        for (int i = 0; i < index && i < _slots.Count; i++) y += _slots[i].Frame.Height + PageGap;
        return y;
    }

    /// <summary>The page under the middle of the viewport.</summary>
    private int CurrentPage()
    {
        if (_slots.Count == 0) return 0;
        double middle = PagesScrollViewer.VerticalOffset + PagesScrollViewer.ViewportHeight / 3;
        double y = PagesPanel.Margin.Top;
        for (int i = 0; i < _slots.Count; i++)
        {
            y += _slots[i].Frame.Height + PageGap;
            if (middle < y) return i;
        }
        return _slots.Count - 1;
    }

    private double FractionIntoPage(int page)
    {
        double height = _slots[page].Frame.Height;
        return height > 0 ? Math.Clamp((PagesScrollViewer.VerticalOffset - PageTop(page)) / height, 0, 1) : 0;
    }

    private (int First, int Last) VisibleRange()
    {
        double top = PagesScrollViewer.VerticalOffset, bottom = top + Math.Max(1, PagesScrollViewer.ViewportHeight);
        int first = -1, last = -1;
        double y = PagesPanel.Margin.Top;
        for (int i = 0; i < _slots.Count; i++)
        {
            double h = _slots[i].Frame.Height;
            if (y + h >= top && y <= bottom)
            {
                if (first < 0) first = i;
                last = i;
            }
            else if (y > bottom) break;
            y += h + PageGap;
        }
        return first < 0 ? (0, 0) : (first, last);
    }

    // ---------- Rendering ----------

    /// <summary>
    /// Renders the visible pages first, then one above and <see cref="RenderAhead"/> below, at the screen's real
    /// pixel size; frees bitmaps of pages far away. One render at a time: when it finishes it looks again (the user
    /// may have scrolled meanwhile). <paramref name="visibleDone"/> completes once the visible pages are drawn.
    /// </summary>
    private async Task RenderVisibleAsync(TaskCompletionSource? visibleDone = null)
    {
        if (_rendering || _pdfDoc == null || _slots.Count == 0) { visibleDone?.TrySetResult(); return; }
        _rendering = true;
        int token = _loadToken;
        try
        {
            while (token == _loadToken && _pdfDoc != null)
            {
                var (shownFirst, shownLast) = VisibleRange();
                int first = Math.Max(0, shownFirst - 1);
                int last = Math.Min(_slots.Count - 1, shownLast + RenderAhead);

                // Far from the screen: let the bitmap go (a long PDF would otherwise fill the memory)
                for (int i = 0; i < _slots.Count; i++)
                    if ((i < first - KeepAround || i > last + KeepAround) && _slots[i].RenderedPixelWidth > 0)
                    {
                        _slots[i].Image.Source = null;
                        _slots[i].RenderedPixelWidth = 0;
                    }

                // Exactly the pixels the image covers on screen (inside the outline): any mismatch means WPF
                // rescales the whole page, and that is what makes text soft
                bool Pending(int i) => _slots[i].RenderedPixelWidth != _slots[i].PixelWidth;
                int next = -1;
                for (int i = shownFirst; i <= shownLast && next < 0; i++) if (Pending(i)) next = i;
                if (next < 0)
                {
                    visibleDone?.TrySetResult();
                    for (int i = shownLast + 1; i <= last && next < 0; i++) if (Pending(i)) next = i;
                    if (next < 0 && first < shownFirst && Pending(first)) next = first;
                }
                if (next < 0) break;
                int pixels = _slots[next].PixelWidth, pixelsHigh = _slots[next].PixelHeight;

                var doc = _pdfDoc;
                var slot = _slots[next];
                BitmapSource bitmap;
                try
                {
                    int index = next;
                    bitmap = await Task.Run(() => doc.RenderPage(index, pixels, pixelsHigh));
                }
                catch (ObjectDisposedException)
                {
                    return; // closed while rendering (another file, or idle)
                }
                if (token != _loadToken) return;
                slot.Image.Source = bitmap;
                slot.RenderedPixelWidth = pixels;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[PdfViewer] Rendering failed: {ex.Message}");
        }
        finally
        {
            _rendering = false;
            visibleDone?.TrySetResult();
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_slots.Count == 0) return;
        UpdatePageIndicator();
        _ = RenderVisibleAsync();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_slots.Count == 0) return;
        if (Math.Abs(PagesScrollViewer.ViewportWidth - _layoutWidth) < 1 && Math.Abs(PagesScrollViewer.ViewportHeight - _layoutHeight) < 1) return;
        _resizeTimer.Stop();
        _resizeTimer.Start(); // relayout once the resizing settles
    }

    private void UpdatePageIndicator()
    {
        if (_slots.Count == 0) return;
        int page = CurrentPage();
        if (_pageEntry == null) PageBoxText.Text = (page + 1).ToString(Loc.Instance.Culture);
        PageTotalText.Text = "/ " + _slots.Count.ToString(Loc.Instance.Culture);
        PrevPageButton.IsEnabled = page > 0;
        NextPageButton.IsEnabled = page < _slots.Count - 1;
    }

    // ---------- Navigation ----------

    public void GoToPage(int index)
    {
        if (_slots.Count == 0) return;
        index = Math.Clamp(index, 0, _slots.Count - 1);
        PagesScrollViewer.ScrollToVerticalOffset(PageTop(index) - PagesPanel.Margin.Top / 2);
    }

    /// <summary>Previous / next page (−1 / +1).</summary>
    public void StepPage(int direction)
    {
        int page = CurrentPage();
        // Scrolled into the middle of a page: "previous" first goes back to its top
        if (direction < 0 && PagesScrollViewer.VerticalOffset - PageTop(page) > 24) GoToPage(page);
        else GoToPage(page + direction);
    }

    public void FirstPage() => GoToPage(0);
    public void LastPage() => GoToPage(_slots.Count - 1);

    /// <summary>↑ / ↓: scroll a bit.</summary>
    public void ScrollBy(double delta) => PagesScrollViewer.ScrollToVerticalOffset(PagesScrollViewer.VerticalOffset + delta);

    // ---------- Search (Ctrl+F) ----------

    private readonly List<(int Page, List<Rect> Rects)> _searchMatches = new();
    private int _currentMatch = -1;

    /// <summary>Reads the text of every page (in the background, page by page) and marks each match.</summary>
    public async Task<int> SearchAsync(string query, System.Threading.CancellationToken token)
    {
        ClearSearch();
        var doc = _pdfDoc;
        if (doc == null || query.Length == 0) return 0;
        int loadToken = _loadToken;
        for (int i = 0; i < _slots.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            int page = i;
            List<List<Rect>> found;
            try { found = await Task.Run(() => doc.FindText(page, query), token); }
            catch (ObjectDisposedException) { return 0; } // closed meanwhile
            if (loadToken != _loadToken) return 0;
            token.ThrowIfCancellationRequested();
            foreach (var rects in found) _searchMatches.Add((page, rects));
            if (found.Count > 0) _slots[page].Highlights.SetMatches(found);
        }
        return _searchMatches.Count;
    }

    public void ShowMatch(int index)
    {
        if (index < 0 || index >= _searchMatches.Count) return;
        if (_currentMatch >= 0 && _currentMatch < _searchMatches.Count) _slots[_searchMatches[_currentMatch].Page].Highlights.SetCurrent(null);
        _currentMatch = index;
        var (page, rects) = _searchMatches[index];
        var slot = _slots[page];
        slot.Highlights.SetCurrent(rects);

        // Scroll so the match sits in the upper third, and sideways too when zoomed in
        double y = PageTop(page) + rects[0].Y * slot.Frame.Height;
        PagesScrollViewer.ScrollToVerticalOffset(Math.Max(0, y - PagesScrollViewer.ViewportHeight / 3));
        double pageLeft = Math.Max(0, (PagesPanel.ActualWidth - slot.Frame.Width) / 2);
        double x = pageLeft + rects[0].X * slot.Frame.Width;
        if (x < PagesScrollViewer.HorizontalOffset || x > PagesScrollViewer.HorizontalOffset + PagesScrollViewer.ViewportWidth - 40)
            PagesScrollViewer.ScrollToHorizontalOffset(Math.Max(0, x - PagesScrollViewer.ViewportWidth / 3));
    }

    public void ClearSearch()
    {
        foreach (var (page, _) in _searchMatches) if (page < _slots.Count) _slots[page].Highlights.SetMatches(null);
        _searchMatches.Clear();
        _currentMatch = -1;
    }

    /// <summary>Match highlights over a page, stored as fractions of the page so zooming keeps them in place.</summary>
    private sealed class SearchLayer : FrameworkElement
    {
        private static readonly Brush MatchBrush = Frozen(Color.FromArgb(0x66, 0xFF, 0xD0, 0x2E));
        private static readonly Brush CurrentBrush = Frozen(Color.FromArgb(0x88, 0xFF, 0x8C, 0x1A));
        private List<List<Rect>>? _matches;
        private List<Rect>? _current;

        public SearchLayer() => IsHitTestVisible = false;

        public void SetMatches(List<List<Rect>>? matches) { _matches = matches; _current = null; InvalidateVisual(); }
        public void SetCurrent(List<Rect>? rects) { _current = rects; InvalidateVisual(); }

        protected override void OnRender(DrawingContext dc)
        {
            if (_matches == null) return;
            double w = ActualWidth, h = ActualHeight;
            foreach (var match in _matches)
                foreach (var r in match)
                    dc.DrawRectangle(ReferenceEquals(match, _current) ? CurrentBrush : MatchBrush, null,
                        new Rect(r.X * w - 1, r.Y * h - 1, r.Width * w + 2, r.Height * h + 2));
        }

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }

    // ---------- Typing a page number ----------

    /// <summary>Digits typed so far (null = not typing). Keys arrive from MainWindow.HandleHookKey.</summary>
    private string? _pageEntry;

    public bool IsEnteringPage => _pageEntry != null;

    private void OnPageBoxClicked(object sender, MouseButtonEventArgs e)
    {
        BeginPageEntry("");
        e.Handled = true;
    }

    private void BeginPageEntry(string digits)
    {
        if (_slots.Count == 0) return;
        _pageEntry = digits;
        PageBox.BorderBrush = (Brush)FindResource("Gold");
        ShowPageEntry();
    }

    private void ShowPageEntry() => PageBoxText.Text = _pageEntry + "|"; // the bar stands in for a caret

    private void CancelPageEntry()
    {
        if (_pageEntry == null) return;
        _pageEntry = null;
        PageBox.BorderBrush = (Brush)FindResource("LineStrong");
        UpdatePageIndicator();
    }

    /// <summary>
    /// A key while a PDF is shown: digits start / extend the page number, Backspace deletes, Enter jumps, Esc cancels.
    /// Returns false for keys it doesn't take (they keep their usual meaning).
    /// </summary>
    public bool HandlePageEntryKey(Key key)
    {
        if (_slots.Count == 0) return false;
        int digit = key is >= Key.D0 and <= Key.D9 ? key - Key.D0 : key is >= Key.NumPad0 and <= Key.NumPad9 ? key - Key.NumPad0 : -1;
        if (digit >= 0)
        {
            if (_pageEntry == null) BeginPageEntry("");
            if (_pageEntry!.Length < _slots.Count.ToString().Length) _pageEntry += digit;
            ShowPageEntry();
            return true;
        }
        if (_pageEntry == null) return false;

        switch (key)
        {
            case Key.Back:
                if (_pageEntry.Length > 0) _pageEntry = _pageEntry[..^1];
                ShowPageEntry();
                return true;
            case Key.Enter:
                string typed = _pageEntry;
                CancelPageEntry();
                if (int.TryParse(typed, out int page) && page > 0) GoToPage(page - 1);
                return true;
            case Key.Escape:
                CancelPageEntry();
                return true;
            default:
                CancelPageEntry(); // anything else: stop typing, and the key does its usual thing
                return false;
        }
    }

    private void OnPrevPageClicked(object sender, RoutedEventArgs e) => StepPage(-1);
    private void OnNextPageClicked(object sender, RoutedEventArgs e) => StepPage(1);

    // ---------- Zoom ----------

    private void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, 0.5, 4.0);
        if (Math.Abs(zoom - _zoomScale) < 0.001) return;
        _zoomScale = zoom;
        ZoomPercentText.Text = $"{Math.Round(_zoomScale * 100)}%";
        Relayout(keepPage: true);
    }

    private void OnZoomInClicked(object sender, RoutedEventArgs e) => SetZoom(_zoomScale + 0.25);
    private void OnZoomOutClicked(object sender, RoutedEventArgs e) => SetZoom(_zoomScale - 0.25);

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Core.KeyState.Modifiers == ModifierKeys.Control)
        {
            SetZoom(_zoomScale + (e.Delta > 0 ? 0.25 : -0.25));
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
