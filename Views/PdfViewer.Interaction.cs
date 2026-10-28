using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lupik.Core;

namespace Lupik.Views;

public partial class PdfViewer
{
    private bool _panning, _selecting, _selectAllPending;
    private Point _panOrigin;
    private double _panHorizontal, _panVertical;
    private (int Page, int Index)? _anchor, _selectionStart, _selectionEnd;
    private int _anchorEnd, _selectionClicks;
    private DispatcherTimer? _selectionScrollTimer;
    private CancellationTokenSource _textReads = new();
    private int _ocrPending;

    private void CancelTextReads()
    {
        _textReads.Cancel(); _textReads.Dispose();
        _textReads = new CancellationTokenSource();
    }

    public bool HasSelection => _selectionStart != null && _selectionEnd != null;

    private async Task<PdfTextPage?> EnsureTextAsync(PageSlot slot, int page)
    {
        if (slot.Text != null) return slot.Text;
        var doc = _pdfDoc;
        if (doc == null) return null;
        int token = _loadToken;
        try
        {
            var cancellation = _textReads.Token;
            var reading = slot.TextTask ??= ReadPageTextAsync(doc, page, cancellation);
            var text = await reading;
            if (token != _loadToken || !ReferenceEquals(doc, _pdfDoc)) return null;
            if (slot.Image.Source == null && !_selectAllPending && !IsSelectedPage(page))
            {
                slot.TextTask = null;
                return text;
            }
            slot.Text = text;
            if (IsSelectedPage(page)) UpdateSelectionHighlights();
            return text;
        }
        catch (ObjectDisposedException) { return null; }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { App.Log($"[PdfViewer] Text layer unavailable: {ex.Message}"); return null; }
    }

    private async Task<PdfTextPage> ReadPageTextAsync(PdfDocument doc, int page, CancellationToken cancellation)
    {
        var text = await Task.Run(() => doc.ReadTextPage(page, cancellation), cancellation);
        if (text.Count > 0) return text;
        cancellation.ThrowIfCancellationRequested();
        _ocrPending++;
        TextReading.Visibility = Visibility.Visible;
        try
        {
            text = await PdfTextOcr.ReadAsync(doc, page, cancellation);
            App.Log($"[PdfViewer] Local OCR page {page + 1}: {text.Count} text units");
            return text;
        }
        finally
        {
            if (--_ocrPending == 0) TextReading.Visibility = Visibility.Collapsed;
        }
    }

    private static int Compare((int Page, int Index) a, (int Page, int Index) b) =>
        a.Page != b.Page ? a.Page.CompareTo(b.Page) : a.Index.CompareTo(b.Index);

    private bool IsSelectedPage(int page)
    {
        if (_selectionStart is not { } a || _selectionEnd is not { } b) return false;
        return page >= Math.Min(a.Page, b.Page) && page <= Math.Max(a.Page, b.Page);
    }

    private ((int Page, int Index) Start, (int Page, int Index) End)? OrderedSelection() =>
        _selectionStart is { } a && _selectionEnd is { } b ? Compare(a, b) <= 0 ? (a, b) : (b, a) : null;

    private void UpdateSelectionHighlights()
    {
        var range = OrderedSelection();
        for (int page = 0; page < _slots.Count; page++)
        {
            var slot = _slots[page];
            if (range is not { } selection || slot.Text is not { } text || page < selection.Start.Page || page > selection.End.Page)
            {
                slot.Highlights.SetSelection(null); continue;
            }
            int first = page == selection.Start.Page ? selection.Start.Index : 0;
            int last = page == selection.End.Page ? selection.End.Index : text.Count - 1;
            slot.Highlights.SetSelection(text.Rectangles(first, last));
        }
    }

    public string SelectedText()
    {
        if (OrderedSelection() is not { } selection) return "";
        var text = new StringBuilder();
        for (int page = selection.Start.Page; page <= selection.End.Page && page < _slots.Count; page++)
        {
            if (_slots[page].Text is not { } model) continue;
            if (text.Length > 0) text.AppendLine();
            text.Append(model.GetText(page == selection.Start.Page ? selection.Start.Index : 0,
                page == selection.End.Page ? selection.End.Index : model.Count - 1));
        }
        return text.ToString();
    }

    public void CopySelection()
    {
        string text = SelectedText();
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (Exception ex) { App.Log($"[PdfViewer] Copy text failed: {ex.Message}"); }
    }

    public void ClearSelection()
    {
        StopPointerGesture();
        _anchor = _selectionStart = _selectionEnd = null;
        UpdateSelectionHighlights();
    }

    public async Task SelectAllAsync()
    {
        if (_selectAllPending) return;
        StopPointerGesture();
        int token = _loadToken;
        _selectAllPending = true;
        try
        {
            for (int page = 0; page < _slots.Count; page++)
            {
                await EnsureTextAsync(_slots[page], page);
                if (token != _loadToken) return;
            }
            int first = _slots.FindIndex(s => s.Text?.Count > 0), last = _slots.FindLastIndex(s => s.Text?.Count > 0);
            if (first < 0) return;
            _anchor = _selectionStart = (first, 0);
            _anchorEnd = 0; _selectionClicks = 1;
            _selectionEnd = (last, _slots[last].Text!.Count - 1);
            UpdateSelectionHighlights();
        }
        finally { _selectAllPending = false; }
    }

    private int PageAt(Point position, bool nearest)
    {
        int bestPage = -1;
        double best = double.MaxValue;
        for (int i = 0; i < _slots.Count; i++)
        {
            var image = _slots[i].Image;
            if (image.ActualWidth <= 0 || image.ActualHeight <= 0) continue;
            Point origin = image.TranslatePoint(new Point(), PagesScrollViewer);
            var bounds = new Rect(origin, new Size(image.ActualWidth, image.ActualHeight));
            if (bounds.Contains(position)) return i;
            if (!nearest) continue;
            double dy = Math.Max(0, Math.Max(bounds.Top - position.Y, position.Y - bounds.Bottom));
            if (dy < best) { best = dy; bestPage = i; }
        }
        return bestPage;
    }

    private int HitCharacter(int page, Point position, bool nearest)
    {
        var slot = _slots[page];
        if (slot.Text == null) return -1;
        Point origin = slot.Image.TranslatePoint(new Point(), PagesScrollViewer);
        double w = slot.Image.ActualWidth, h = slot.Image.ActualHeight;
        return w <= 0 || h <= 0 ? -1 : slot.Text.HitTest(new Point((position.X - origin.X) / w, (position.Y - origin.Y) / h), 4 / w, 4 / h, nearest);
    }

    private bool InViewport(Point position) => position.X >= 0 && position.Y >= 0 &&
        position.X < PagesScrollViewer.ViewportWidth && position.Y < PagesScrollViewer.ViewportHeight;

    private async void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (_pdfDoc == null || !InViewport(e.GetPosition(PagesScrollViewer))) return;
        if (e.ChangedButton == MouseButton.Right)
        {
            if (_selecting) return;
            BeginPan(e.GetPosition(PagesScrollViewer)); e.Handled = true; return;
        }
        if (e.ChangedButton != MouseButton.Left || _panning) return;
        e.Handled = true;
        Point position = e.GetPosition(PagesScrollViewer);
        int page = PageAt(position, nearest: false);
        if (page < 0) { ClearSelection(); return; }
        int token = _loadToken;
        await EnsureTextAsync(_slots[page], page);
        if (token != _loadToken || Mouse.LeftButton != MouseButtonState.Pressed) return;
        int index = HitCharacter(page, position, nearest: false);
        App.Log($"[PdfViewer] Text pointer down: page={page + 1}, hit={index}, text units={_slots[page].Text?.Count ?? 0}");
        if (index < 0) { ClearSelection(); return; }
        if (KeyState.Modifiers == ModifierKeys.Shift && _anchor != null) ExtendSelection(position);
        else BeginTextSelection(page, index, e.ClickCount);
        _selecting = true;
        bool captured = PagesScrollViewer.CaptureMouse();
        App.Log($"[PdfViewer] Text drag capture={captured}");
        _selectionScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => AutoScrollSelection(), Dispatcher);
        _selectionScrollTimer.Start();
    }

    private void BeginTextSelection(int page, int index, int clicks)
    {
        var model = _slots[page].Text!;
        var range = clicks >= 3 ? model.LineRange(index) : clicks == 2 ? model.WordRange(index) : (index, index);
        _anchor = _selectionStart = (page, range.Item1);
        _anchorEnd = range.Item2;
        _selectionEnd = clicks > 1 ? (page, range.Item2) : null;
        _selectionClicks = clicks;
        UpdateSelectionHighlights();
    }

    private void ExtendSelection(Point position)
    {
        if (_anchor is not { } anchor) return;
        int page = PageAt(position, nearest: true);
        if (page < 0) return;
        int index = HitCharacter(page, position, nearest: true);
        if (index < 0) return;
        bool forward = Compare((page, index), anchor) >= 0;
        var model = _slots[page].Text!;
        var range = _selectionClicks >= 3 ? model.LineRange(index) : _selectionClicks == 2 ? model.WordRange(index) : (index, index);
        _selectionStart = (anchor.Page, forward ? anchor.Index : _anchorEnd);
        _selectionEnd = (page, forward ? range.Item2 : range.Item1);
        UpdateSelectionHighlights();
    }

    private void BeginPan(Point position)
    {
        StopPointerGesture();
        _panning = true;
        _panOrigin = position;
        _panHorizontal = PagesScrollViewer.HorizontalOffset;
        _panVertical = PagesScrollViewer.VerticalOffset;
        PagesScrollViewer.Cursor = Cursors.SizeAll;
        bool captured = PagesScrollViewer.CaptureMouse();
        App.Log($"[PdfViewer] Pan capture={captured}");
    }

    private void MovePan(Point position)
    {
        PagesScrollViewer.ScrollToHorizontalOffset(_panHorizontal - (position.X - _panOrigin.X));
        PagesScrollViewer.ScrollToVerticalOffset(_panVertical - (position.Y - _panOrigin.Y));
    }

    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        Point position = e.GetPosition(PagesScrollViewer);
        if (_panning)
        {
            if (e.RightButton != MouseButtonState.Pressed) { StopPointerGesture(); return; }
            MovePan(position); e.Handled = true; return;
        }
        if (_selecting)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { StopPointerGesture(); return; }
            ExtendSelection(position); e.Handled = true; return;
        }
        int page = PageAt(position, nearest: false);
        PagesScrollViewer.Cursor = page >= 0 && HitCharacter(page, position, nearest: false) >= 0 ? Cursors.IBeam : Cursors.Arrow;
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right && _panning || e.ChangedButton == MouseButton.Left && _selecting)
        {
            StopPointerGesture(); e.Handled = true;
        }
    }

    private void AutoScrollSelection()
    {
        if (!_selecting || Mouse.LeftButton != MouseButtonState.Pressed) { StopPointerGesture(); return; }
        var point = Mouse.GetPosition(PagesScrollViewer);
        double h = PagesScrollViewer.ViewportHeight, w = PagesScrollViewer.ViewportWidth;
        double dy = point.Y < 20 ? point.Y - 20 : point.Y > h - 20 ? point.Y - h + 20 : 0;
        double dx = point.X < 20 ? point.X - 20 : point.X > w - 20 ? point.X - w + 20 : 0;
        if (dx == 0 && dy == 0) return;
        PagesScrollViewer.ScrollToVerticalOffset(PagesScrollViewer.VerticalOffset + Math.Clamp(dy, -35, 35));
        PagesScrollViewer.ScrollToHorizontalOffset(PagesScrollViewer.HorizontalOffset + Math.Clamp(dx, -35, 35));
        ExtendSelection(point);
    }

    private void StopPointerGesture()
    {
        _panning = _selecting = false;
        _selectionScrollTimer?.Stop();
        PagesScrollViewer.ClearValue(CursorProperty);
        if (PagesScrollViewer.IsMouseCaptured) PagesScrollViewer.ReleaseMouseCapture();
    }

    private void OnPointerCaptureLost(object sender, MouseEventArgs e)
    {
        if ((_selecting || _panning) && !PagesScrollViewer.IsMouseCaptured) App.Log("[PdfViewer] Pointer capture lost");
        if (!PagesScrollViewer.IsMouseCaptured) StopPointerGesture();
    }
}
