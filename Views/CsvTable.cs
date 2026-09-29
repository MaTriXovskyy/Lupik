using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickPeek.Views;

/// <summary>
/// Lightweight read-only table: draws only the cells currently in view, straight to the DrawingContext.
/// Replaces DataGrid, which builds a full element tree per cell and made scrolling large CSVs sluggish.
/// Hosted in a ScrollViewer with CanContentScroll="False", so scrolling is pixel-smooth.
/// </summary>
public class CsvTable : FrameworkElement
{
    private const double RowHeight = 26;
    private const double HeaderHeight = 30;
    private const double CellPadding = 10;

    private static readonly Brush RowBrush = Frozen(Color.FromRgb(0x13, 0x12, 0x11));
    private static readonly Brush AltRowBrush = Frozen(Color.FromRgb(0x18, 0x16, 0x14));
    private static readonly Brush HeaderBrush = Frozen(Color.FromRgb(0x1B, 0x19, 0x17));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xEC, 0xE6, 0xDC));
    private static readonly Brush HeaderTextBrush = Frozen(Color.FromRgb(0xB5, 0xAB, 0x9D));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x24, 0x21, 0x1E));
    private static readonly Pen HeaderPen = FrozenPen(Color.FromRgb(0x2F, 0x2B, 0x27));
    private static readonly Brush SelectionFill = Frozen(Color.FromArgb(0x38, 0xE3, 0xB3, 0x41));
    private static readonly Pen SelectionPen = FrozenPen(Color.FromArgb(0x90, 0xE3, 0xB3, 0x41));
    private static readonly Pen ActivePen = FrozenPen(Color.FromRgb(0xE3, 0xB3, 0x41), 2);
    private static readonly Brush HeaderSelectedBrush = Frozen(Color.FromArgb(0x30, 0xE3, 0xB3, 0x41));

    private readonly Typeface _typeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Typeface _headerTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private const double FontSize = 12.5;
    private const double HeaderFontSize = 11.5;

    private IReadOnlyList<string[]> _rows = Array.Empty<string[]>();
    private string[] _header = Array.Empty<string>();
    private double[] _widths = Array.Empty<double>();
    private double[] _offsets = Array.Empty<double>(); // left edge of each column
    private double _totalWidth;

    // Laying out text is the expensive part: rows are laid out once into frozen drawings and reused while in view
    private readonly Dictionary<int, Drawing> _rowCache = new();
    private Drawing? _headerDrawing;

    private ScrollViewer? _scroller;

    public CsvTable()
    {
        Loaded += (_, _) => AttachScroller();
    }

    private void AttachScroller()
    {
        if (_scroller != null) return;
        _scroller = FindScroller();
        if (_scroller != null) _scroller.ScrollChanged += (_, _) => InvalidateVisual();
    }

    public void SetData(string[] header, IReadOnlyList<string[]> rows)
    {
        _header = header;
        _rows = rows;
        _rowCache.Clear();
        _headerDrawing = null;
        _selection = null;
        SelectionChanged?.Invoke();

        _widths = new double[header.Length];
        _offsets = new double[header.Length];
        double x = 0;
        for (int c = 0; c < header.Length; c++)
        {
            _widths[c] = MeasureColumn(c);
            _offsets[c] = x;
            x += _widths[c];
        }
        _totalWidth = x;

        InvalidateMeasure();
        InvalidateVisual();
        _scroller?.ScrollToHome();
    }

    /// <summary>Fits the header and the longest value among the first rows (capped).</summary>
    private double MeasureColumn(int c)
    {
        string longest = _header[c];
        int limit = Math.Min(_rows.Count, 500);
        for (int r = 0; r < limit; r++)
        {
            var row = _rows[r];
            if (c < row.Length && row[c].Length > longest.Length) longest = row[c];
        }
        double width = Format(longest, _headerTypeface, FontSize, TextBrush, double.MaxValue).WidthIncludingTrailingWhitespace;
        return Math.Clamp(width + CellPadding * 2 + 4, 60, 420);
    }

    /// <summary>
    /// The whole table area is clickable. WPF's default hit-testing walks the drawn content, and it
    /// misses large parts of it here (nested clips inside the cached row drawings), so most clicks did nothing.
    /// </summary>
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var p = hitTestParameters.HitPoint;
        return p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight
            ? new PointHitTestResult(this, p)
            : null!;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(_totalWidth, HeaderHeight + _rows.Count * RowHeight);

    protected override void OnRender(DrawingContext dc)
    {
        if (_header.Length == 0) return;
        AttachScroller();

        // Without a known viewport, draw a screenful at most: never the whole (possibly huge) table
        double top = _scroller?.VerticalOffset ?? 0;
        double left = _scroller?.HorizontalOffset ?? 0;
        double viewHeight = _scroller is { ViewportHeight: > 0 } ? _scroller.ViewportHeight : Math.Min(ActualHeight, 1200);
        double viewWidth = _scroller is { ViewportWidth: > 0 } ? _scroller.ViewportWidth : Math.Min(ActualWidth, 2000);

        // Visible column range
        int firstCol = 0;
        while (firstCol < _widths.Length - 1 && _offsets[firstCol] + _widths[firstCol] < left) firstCol++;
        int lastCol = firstCol;
        while (lastCol < _widths.Length - 1 && _offsets[lastCol + 1] < left + viewWidth) lastCol++;

        // Visible row range (rows start below the header)
        int firstRow = Math.Max(0, (int)((top) / RowHeight) - 1);
        int lastRow = Math.Min(_rows.Count - 1, (int)((top + viewHeight) / RowHeight) + 1);

        double right = Math.Min(_totalWidth, left + viewWidth);
        double tableBottom = Math.Min(top + viewHeight, HeaderHeight + _rows.Count * RowHeight);

        // Never paint outside the visible area (e.g. a row peeking out above the sticky header)
        dc.PushClip(new RectangleGeometry(new Rect(left, top - 4, viewWidth, viewHeight + 4)));

        for (int r = firstRow; r <= lastRow; r++)
        {
            double y = HeaderHeight + r * RowHeight;
            dc.DrawRectangle(r % 2 == 0 ? RowBrush : AltRowBrush, null, new Rect(left, y, right - left, RowHeight));
            dc.DrawLine(GridPen, new Point(left, y + RowHeight - 0.5), new Point(right, y + RowHeight - 0.5));

            // Each row's text is laid out once into a frozen drawing; scrolling only moves it
            dc.PushTransform(new TranslateTransform(0, y));
            dc.DrawDrawing(RowDrawing(r));
            dc.Pop();
        }
        SchedulePrefetch(firstRow, lastRow);

        // Column separators
        for (int c = firstCol; c <= lastCol; c++)
        {
            double x = _offsets[c] + _widths[c] - 0.5;
            dc.DrawLine(GridPen, new Point(x, top), new Point(x, tableBottom));
        }

        // Selection (under the sticky header, so it scrolls beneath it like the rows)
        if (_selection is { } sel)
        {
            var (r1, c1, r2, c2) = sel.Normalized;
            var area = new Rect(_offsets[c1], HeaderHeight + r1 * RowHeight,
                _offsets[c2] + _widths[c2] - _offsets[c1], (r2 - r1 + 1) * RowHeight);
            dc.DrawRectangle(SelectionFill, SelectionPen, area);

            // Active cell (where the selection started) gets a stronger outline
            var active = new Rect(_offsets[sel.AnchorCol], HeaderHeight + sel.AnchorRow * RowHeight, _widths[sel.AnchorCol], RowHeight);
            dc.DrawRectangle(null, ActivePen, active);
        }

        // Sticky header, drawn last so rows scroll underneath it
        // Starts a few px higher: layout rounding can leave a 1-px gap above it where rows would peek through
        dc.DrawRectangle(HeaderBrush, null, new Rect(left, top - 4, right - left, HeaderHeight + 4));
        if (_selection is { } hs)
        {
            var (_, hc1, _, hc2) = hs.Normalized;
            dc.DrawRectangle(HeaderSelectedBrush, null,
                new Rect(_offsets[hc1], top, _offsets[hc2] + _widths[hc2] - _offsets[hc1], HeaderHeight));
        }
        dc.DrawLine(HeaderPen, new Point(left, top + HeaderHeight - 0.5), new Point(right, top + HeaderHeight - 0.5));
        dc.PushTransform(new TranslateTransform(0, top));
        dc.DrawDrawing(_headerDrawing ??= BuildRowDrawing(_header, HeaderHeight, header: true));
        dc.Pop();
        for (int c = firstCol; c <= lastCol; c++)
        {
            double x = _offsets[c] + _widths[c] - 0.5;
            dc.DrawLine(HeaderPen, new Point(x, top), new Point(x, top + HeaderHeight));
        }
        dc.Pop(); // viewport clip
    }

    // --- Selection & copy ---

    /// <summary>A rectangular cell range; Anchor is where the selection started (the active cell).</summary>
    private readonly record struct Selection(int AnchorRow, int AnchorCol, int EndRow, int EndCol, bool WholeColumns)
    {
        public (int r1, int c1, int r2, int c2) Normalized =>
            (Math.Min(AnchorRow, EndRow), Math.Min(AnchorCol, EndCol), Math.Max(AnchorRow, EndRow), Math.Max(AnchorCol, EndCol));
    }

    private Selection? _selection;
    private bool _dragging;
    private bool _dragColumns;

    public bool HasSelection => _selection != null;

    /// <summary>Number of selected cells (for status text).</summary>
    public int SelectedCellCount
    {
        get
        {
            if (_selection is not { } s) return 0;
            var (r1, c1, r2, c2) = s.Normalized;
            return (r2 - r1 + 1) * (c2 - c1 + 1);
        }
    }

    public event Action? SelectionChanged;

    private void SetSelection(Selection? selection)
    {
        _selection = selection;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selection != null) SetSelection(null);
    }

    public void SelectAll()
    {
        if (_rows.Count == 0 || _header.Length == 0) return;
        SetSelection(new Selection(0, 0, _rows.Count - 1, _header.Length - 1, WholeColumns: true));
    }

    /// <summary>
    /// Selected cells as tab-separated text, the format Excel / Google Sheets paste as cells.
    /// Whole-column selections include the header row.
    /// </summary>
    public string? GetSelectedText()
    {
        if (_selection is not { } s) return null;
        var (r1, c1, r2, c2) = s.Normalized;
        var sb = new System.Text.StringBuilder();

        void AppendRow(string[] cells)
        {
            for (int c = c1; c <= c2; c++)
            {
                if (c > c1) sb.Append('\t');
                string value = c < cells.Length ? cells[c] : "";
                // Quote values that would break the tab/newline structure, like Excel does
                if (value.IndexOfAny(new[] { '\t', '\n', '\r', '"' }) >= 0)
                    value = "\"" + value.Replace("\"", "\"\"") + "\"";
                sb.Append(value);
            }
            sb.Append("\r\n");
        }

        if (s.WholeColumns) AppendRow(_header);
        for (int r = r1; r <= r2; r++) AppendRow(_rows[r]);
        return sb.ToString();
    }

    /// <summary>Maps a point (in table coordinates) to a cell; row -1 means the sticky header.</summary>
    private (int row, int col)? HitTestCell(Point p)
    {
        if (_header.Length == 0) return null;
        double top = _scroller?.VerticalOffset ?? 0;

        int col = -1;
        for (int c = 0; c < _offsets.Length; c++)
        {
            if (p.X >= _offsets[c] && p.X < _offsets[c] + _widths[c]) { col = c; break; }
        }
        if (col < 0) col = p.X < 0 ? 0 : _header.Length - 1; // past the edges: clamp (useful while dragging)

        if (p.Y < top + HeaderHeight) return (-1, col);
        int row = Math.Clamp((int)((p.Y - HeaderHeight) / RowHeight), 0, Math.Max(0, _rows.Count - 1));
        return _rows.Count == 0 ? null : (row, col);
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (HitTestCell(e.GetPosition(this)) is not { } hit) return;
        var (row, col) = hit;

        bool extend = QuickPeek.Core.KeyState.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) && _selection != null;
        _dragColumns = row < 0;

        if (_dragColumns)
        {
            // Header click: whole column (Shift extends across columns)
            int anchorCol = extend ? _selection!.Value.AnchorCol : col;
            SetSelection(new Selection(0, anchorCol, Math.Max(0, _rows.Count - 1), col, WholeColumns: true));
        }
        else if (extend)
        {
            var s = _selection!.Value;
            SetSelection(s with { EndRow = row, EndCol = col, WholeColumns = false });
        }
        else
        {
            SetSelection(new Selection(row, col, row, col, WholeColumns: false));
        }

        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _selection is not { } s) return;

        var p = e.GetPosition(this);
        AutoScrollTowards(p);
        if (HitTestCell(p) is not { } hit) return;
        var (row, col) = hit;

        var next = _dragColumns
            ? s with { EndCol = col }
            : s with { EndRow = row < 0 ? Math.Max(0, (int)((_scroller?.VerticalOffset ?? 0) / RowHeight)) : row, EndCol = col };
        if (next != s) SetSelection(next);
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    /// <summary>Dragging past the edge of the view scrolls it, like in Excel.</summary>
    private void AutoScrollTowards(Point p)
    {
        if (_scroller == null) return;
        double top = _scroller.VerticalOffset, left = _scroller.HorizontalOffset;
        const double step = 20;

        if (p.Y > top + _scroller.ViewportHeight) _scroller.ScrollToVerticalOffset(top + step);
        else if (p.Y < top + HeaderHeight && !_dragColumns) _scroller.ScrollToVerticalOffset(Math.Max(0, top - step));

        if (p.X > left + _scroller.ViewportWidth) _scroller.ScrollToHorizontalOffset(left + step);
        else if (p.X < left) _scroller.ScrollToHorizontalOffset(Math.Max(0, left - step));
    }

    private bool _prefetchScheduled;

    /// <summary>
    /// While the app is idle, lays out rows just above and below the view,
    /// so the next scroll steps find them ready. Stops as soon as there's other work (input, rendering).
    /// </summary>
    private void SchedulePrefetch(int firstRow, int lastRow)
    {
        if (_prefetchScheduled) return;
        _prefetchScheduled = true;
        var rows = _rows;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            _prefetchScheduled = false;
            if (!ReferenceEquals(rows, _rows)) return; // another file was loaded meanwhile
            int from = Math.Max(0, firstRow - 40), to = Math.Min(_rows.Count - 1, lastRow + 40);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int r = lastRow + 1; r <= to && sw.ElapsedMilliseconds < 8; r++) RowDrawing(r);   // down first: the usual direction
            for (int r = firstRow - 1; r >= from && sw.ElapsedMilliseconds < 8; r--) RowDrawing(r);
            bool done = true;
            for (int r = from; r <= to; r++) if (!_rowCache.ContainsKey(r)) { done = false; break; }
            if (!done) SchedulePrefetch(firstRow, lastRow); // continue in small slices, keeping the UI responsive
        });
    }

    private Drawing RowDrawing(int row)
    {
        if (_rowCache.TryGetValue(row, out var cached)) return cached;
        if (_rowCache.Count > 600) _rowCache.Clear(); // bounded: a few screens of rows
        var drawing = BuildRowDrawing(_rows[row], RowHeight, header: false);
        _rowCache[row] = drawing;
        return drawing;
    }

    /// <summary>Lays out every cell of a row once (clipped to its column) into a frozen drawing.</summary>
    private Drawing BuildRowDrawing(string[] cells, double height, bool header)
    {
        var group = new DrawingGroup();
        using (var ctx = group.Open())
        {
            for (int c = 0; c < _widths.Length && c < cells.Length; c++)
            {
                if (cells[c].Length == 0) continue;
                var text = Format(cells[c], header ? _headerTypeface : _typeface, header ? HeaderFontSize : FontSize,
                    header ? HeaderTextBrush : TextBrush, _widths[c] - CellPadding * 2);
                ctx.PushClip(new RectangleGeometry(new Rect(_offsets[c], 0, _widths[c], height)));
                ctx.DrawText(text, new Point(_offsets[c] + CellPadding, (height - text.Height) / 2));
                ctx.Pop();
            }
        }
        group.Freeze();
        return group;
    }
    private FormattedText Format(string value, Typeface typeface, double size, Brush brush, double maxWidth)
    {
        // Multi-line values (quoted newlines) are shown on one line
        string single = value.IndexOf('\n') >= 0 ? value.Replace("\r", "").Replace('\n', ' ') : value;
        var text = new FormattedText(single, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (maxWidth < double.MaxValue) text.MaxTextWidth = Math.Max(1, maxWidth);
        return text;
    }

    private ScrollViewer? FindScroller()
    {
        DependencyObject? node = this;
        while (node != null && node is not ScrollViewer) node = VisualTreeHelper.GetParent(node);
        return node as ScrollViewer;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness = 1)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
