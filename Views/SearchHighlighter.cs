using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Lupik.Views;

/// <summary>
/// Search matches in an AvalonEdit text (the code preview and both sides of a diff): a gold wash behind every
/// match, orange behind the current one.
/// </summary>
public sealed class SearchHighlighter : IBackgroundRenderer
{
    private static readonly Brush MatchBrush = Frozen(Color.FromArgb(0x55, 0xFF, 0xD8, 0x4D));
    private static readonly Brush CurrentBrush = Frozen(Color.FromArgb(0xB0, 0xFF, 0x9A, 0x2E));
    private static readonly Pen CurrentPen = FrozenPen(Color.FromRgb(0xFF, 0xB8, 0x5C));

    private readonly TextEditor _editor;
    private readonly List<(int Offset, int Length)> _matches = new();
    private int _current = -1;

    public SearchHighlighter(TextEditor editor)
    {
        _editor = editor;
        editor.TextArea.TextView.BackgroundRenderers.Add(this);
    }

    public KnownLayer Layer => KnownLayer.Selection;

    public int Count => _matches.Count;

    public int Find(string query)
    {
        _matches.Clear();
        _current = -1;
        if (query.Length > 0)
        {
            string text = _editor.Document.Text;
            for (int i = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); i >= 0 && _matches.Count < 100_000;
                 i = text.IndexOf(query, i + query.Length, StringComparison.CurrentCultureIgnoreCase))
                _matches.Add((i, query.Length));
        }
        _editor.TextArea.TextView.InvalidateLayer(Layer);
        return _matches.Count;
    }

    public void Show(int index)
    {
        if (index < 0 || index >= _matches.Count) return;
        _current = index;
        var (offset, length) = _matches[index];
        var location = _editor.Document.GetLocation(offset);
        _editor.ScrollTo(location.Line, location.Column);
        // ScrollTo only makes the line visible; keep the match away from the very edge
        var view = _editor.TextArea.TextView;
        double y = view.GetVisualTopByDocumentLine(location.Line);
        if (y < view.VerticalOffset + 30 || y > view.VerticalOffset + view.ActualHeight - 60)
            _editor.ScrollToVerticalOffset(Math.Max(0, y - view.ActualHeight / 3));
        view.InvalidateLayer(Layer);
    }

    public void Clear()
    {
        _matches.Clear();
        _current = -1;
        _editor.TextArea.TextView.InvalidateLayer(Layer);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_matches.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
        int viewStart = textView.VisualLines[0].FirstDocumentLine.Offset;
        int viewEnd = textView.VisualLines[^1].LastDocumentLine.EndOffset;

        // Matches are in document order: jump to the first one on screen
        int lo = 0, hi = _matches.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (_matches[mid].Offset + _matches[mid].Length < viewStart) lo = mid + 1; else hi = mid; }

        for (int i = lo; i < _matches.Count && _matches[i].Offset <= viewEnd; i++)
        {
            var segment = new TextSegment { StartOffset = _matches[i].Offset, Length = _matches[i].Length };
            bool current = i == _current;
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                var r = new Rect(rect.X - 1, rect.Y, rect.Width + 2, rect.Height);
                drawingContext.DrawRoundedRectangle(current ? CurrentBrush : MatchBrush, current ? CurrentPen : null, r, 2, 2);
            }
        }
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c) { var p = new Pen(new SolidColorBrush(c), 1); p.Freeze(); return p; }
}
