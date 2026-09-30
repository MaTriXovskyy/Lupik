using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>
/// Two text files side by side (select two in Explorer, like comparing two pictures): removed lines red on the left,
/// added green on the right, changed words marked inside changed lines. Both sides scroll together;
/// ↑/↓ jump between changes.
/// </summary>
public partial class DiffViewer : UserControl, ISearchable
{
    private const int MaxChars = 2 * 1024 * 1024; // per file: bigger ones are cut off

    /// <summary>
    /// One line on one side: its type, its number in the file (null for the filler opposite an insert),
    /// the changed words inside a modified line, and its text.
    /// </summary>
    private sealed record Line(ChangeType Type, int? Number, List<(int Start, int Length)> Changed, string Text);

    private List<Line> _left = new(), _right = new();
    private readonly List<int> _changes = new(); // first line of each block of changes
    private int _currentChange = -1;
    private int _loadToken;
    private bool _syncing;
    private readonly List<NumberMargin> _margins = new();

    public string PathA { get; private set; } = "";
    public string PathB { get; private set; } = "";

    public DiffViewer()
    {
        InitializeComponent();
        foreach (var (editor, lines, isNew) in new[] { (LeftEditor, (Func<List<Line>>)(() => _left), false), (RightEditor, () => _right, true) })
        {
            editor.TextArea.TextView.BackgroundRenderers.Add(new DiffBackground(lines, isNew));
            var margin = new NumberMargin(lines);
            _margins.Add(margin);
            editor.TextArea.LeftMargins.Insert(0, margin);
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
            editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x58, 0x5B, 0x70));
            editor.TextArea.SelectionBorder = null;
        }
        _leftSearch = new SearchHighlighter(LeftEditor);
        _rightSearch = new SearchHighlighter(RightEditor);

        // Both sides always show the same lines
        LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(LeftEditor, RightEditor);
        RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(RightEditor, LeftEditor);
    }

    private void Sync(TextEditor from, TextEditor to)
    {
        if (_syncing) return;
        _syncing = true;
        // The text views' own offsets (the editors' ScrollViewers only catch up afterwards). Only real differences:
        // a side that can't scroll as far (clamped) must not bounce the other one back and forth
        var source = from.TextArea.TextView;
        var target = to.TextArea.TextView;
        if (Math.Abs(source.VerticalOffset - target.VerticalOffset) > 0.5) to.ScrollToVerticalOffset(source.VerticalOffset);
        if (Math.Abs(source.HorizontalOffset - target.HorizontalOffset) > 0.5) to.ScrollToHorizontalOffset(source.HorizontalOffset);
        _syncing = false;
    }

    /// <summary>Text files Lupik can compare (the code preview's formats).</summary>
    public static bool IsText(string path, string[] codeExtensions) =>
        Array.IndexOf(codeExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    public async Task<bool> LoadAsync(string a, string b)
    {
        int token = ++_loadToken;
        var (left, right, changes, added, removed, truncated) = await Task.Run(() => Compare(a, b));
        if (token != _loadToken) return false;

        PathA = a;
        PathB = b;
        _left = left;
        _right = right;
        _changes.Clear();
        _changes.AddRange(changes);
        _currentChange = -1;
        _leftSearch.Clear();
        _rightSearch.Clear();

        foreach (var (editor, lines, path) in new[] { (LeftEditor, left, a), (RightEditor, right, b) })
        {
            var definition = CodeViewer.HighlightingFor(Path.GetExtension(path).ToLowerInvariant());
            CodeTheme.Apply(definition);
            editor.SyntaxHighlighting = definition;
            editor.Text = string.Join("\n", lines.Select(l => l.Text));
            editor.ScrollToHome();
        }
        foreach (var margin in _margins) margin.Reset();

        LeftName.Text = Path.GetFileName(a);
        LeftName.ToolTip = a;
        RightName.Text = Path.GetFileName(b);
        RightName.ToolTip = b;
        AddedText.Text = "+" + added;
        RemovedText.Text = "−" + removed;
        SummaryText.Text = changes.Count == 0 ? Loc.T("diff.identical")
            : Loc.Plural("count.changes", changes.Count) + (truncated ? "  •  " + Loc.T("diff.truncated") : "");
        UpdateChangeText();
        if (changes.Count > 0) _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => GoToChange(0));
        return true;
    }

    public void Release()
    {
        _loadToken++;
        _left = new();
        _right = new();
        _changes.Clear();
        LeftEditor.Text = "";
        RightEditor.Text = "";
    }

    private static (List<Line>, List<Line>, List<int>, int, int, bool) Compare(string a, string b)
    {
        bool truncated = false;
        string Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxChars];
            int n = reader.ReadBlock(buffer, 0, buffer.Length);
            if (reader.Peek() >= 0) truncated = true;
            return new string(buffer, 0, n).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        var model = SideBySideDiffBuilder.Diff(Read(a), Read(b), ignoreWhiteSpace: false, ignoreCase: false);
        var left = model.OldText.Lines.Select(ToLine).ToList();
        var right = model.NewText.Lines.Select(ToLine).ToList();

        var changes = new List<int>();
        int added = 0, removed = 0;
        bool inChange = false;
        for (int i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var l = i < left.Count ? left[i].Type : ChangeType.Unchanged;
            var r = i < right.Count ? right[i].Type : ChangeType.Unchanged;
            bool changed = l != ChangeType.Unchanged || r != ChangeType.Unchanged;
            if (changed && !inChange) changes.Add(i);
            inChange = changed;
            if (r is ChangeType.Inserted or ChangeType.Modified) added++;
            if (l is ChangeType.Deleted or ChangeType.Modified) removed++;
        }
        return (left, right, changes, added, removed, truncated);
    }

    private static Line ToLine(DiffPiece piece)
    {
        string text = piece.Type == ChangeType.Imaginary ? "" : piece.Text ?? "";
        var changed = new List<(int, int)>();
        if (piece.Type == ChangeType.Modified && piece.SubPieces != null)
        {
            int offset = 0;
            foreach (var sub in piece.SubPieces)
            {
                if (sub.Type == ChangeType.Imaginary || sub.Text == null) continue;
                if (sub.Type != ChangeType.Unchanged) changed.Add((offset, sub.Text.Length));
                offset += sub.Text.Length;
            }
        }
        return new Line(piece.Type, piece.Position, changed, text);
    }

    // ---------- Jumping between changes (↑/↓, toolbar) ----------

    public void StepChange(int direction)
    {
        if (_changes.Count == 0) return;
        int next = _currentChange < 0 ? (direction > 0 ? 0 : _changes.Count - 1)
            : Math.Clamp(_currentChange + direction, 0, _changes.Count - 1);
        GoToChange(next);
    }

    private void GoToChange(int index)
    {
        _currentChange = index;
        int line = _changes[index] + 1;
        var view = RightEditor.TextArea.TextView;
        RightEditor.ScrollTo(line, 1);
        double y = view.GetVisualTopByDocumentLine(Math.Min(line, RightEditor.Document.LineCount));
        RightEditor.ScrollToVerticalOffset(Math.Max(0, y - view.ActualHeight / 3));
        UpdateChangeText();
    }

    private void UpdateChangeText() =>
        ChangeText.Text = _changes.Count == 0 ? "" : Loc.T("diff.change", Math.Max(1, _currentChange + 1), _changes.Count);

    private void OnPrevChange(object sender, RoutedEventArgs e) => StepChange(-1);
    private void OnNextChange(object sender, RoutedEventArgs e) => StepChange(1);

    // ---------- Search (Ctrl+F): both sides, left first ----------

    private readonly SearchHighlighter _leftSearch, _rightSearch;

    public Task<int> SearchAsync(string query, CancellationToken token) =>
        Task.FromResult(_leftSearch.Find(query) + _rightSearch.Find(query));

    public void ShowMatch(int index)
    {
        if (index < _leftSearch.Count) { _rightSearch.Show(-1); _leftSearch.Show(index); }
        else { _leftSearch.Show(-1); _rightSearch.Show(index - _leftSearch.Count); }
    }

    public void ClearSearch()
    {
        _leftSearch.Clear();
        _rightSearch.Clear();
    }

    // ---------- Drawing ----------

    private static Brush Frozen(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }

    private static readonly Brush DeletedLine = Frozen(0x38, 0xE5, 0x53, 0x4B);
    private static readonly Brush InsertedLine = Frozen(0x33, 0x4C, 0xC3, 0x64);
    private static readonly Brush ModifiedLine = Frozen(0x24, 0xE3, 0xB3, 0x41);
    private static readonly Brush FillerLine = Frozen(0xFF, 0x1E, 0x1B, 0x18); // no line here: the other side added some
    private static readonly Brush ChangedWordsOld = Frozen(0x70, 0xE5, 0x53, 0x4B);
    private static readonly Brush ChangedWordsNew = Frozen(0x66, 0x4C, 0xC3, 0x64);

    /// <summary>Colors each line by its change type, and the changed words inside modified lines.</summary>
    private sealed class DiffBackground(Func<List<Line>> lines, bool isNew) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            var list = lines();
            if (list.Count == 0 || !textView.VisualLinesValid) return;
            foreach (var visual in textView.VisualLines)
            {
                int index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= list.Count) continue;
                var line = list[index];
                Brush? brush = line.Type switch
                {
                    ChangeType.Deleted => DeletedLine,
                    ChangeType.Inserted => InsertedLine,
                    ChangeType.Modified => ModifiedLine,
                    ChangeType.Imaginary => FillerLine,
                    _ => null,
                };
                if (brush == null) continue;
                double top = visual.VisualTop - textView.VerticalOffset;
                dc.DrawRectangle(brush, null, new Rect(0, top, textView.ActualWidth + textView.HorizontalOffset, visual.Height));

                foreach (var (start, length) in line.Changed)
                {
                    var segment = new ICSharpCode.AvalonEdit.Document.TextSegment
                    {
                        StartOffset = visual.FirstDocumentLine.Offset + Math.Min(start, visual.FirstDocumentLine.Length),
                        Length = Math.Max(0, Math.Min(length, visual.FirstDocumentLine.Length - start)),
                    };
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                        dc.DrawRectangle(isNew ? ChangedWordsNew : ChangedWordsOld, null, rect);
                }
            }
        }
    }

    /// <summary>Line numbers from the files themselves (the filler lines opposite an insert get none).</summary>
    private sealed class NumberMargin(Func<List<Line>> lines) : AbstractMargin
    {
        private static readonly Typeface Face = new("Cascadia Mono, Consolas");
        private static readonly Brush NumberBrush = Frozen(0xFF, 0x5A, 0x53, 0x4B);

        protected override Size MeasureOverride(Size availableSize)
        {
            int max = lines().Select(l => l.Number ?? 0).DefaultIfEmpty(0).Max();
            return new Size(Math.Max(3, max.ToString().Length) * 8 + 26, 0);
        }

        protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
        {
            if (oldTextView != null) oldTextView.VisualLinesChanged -= Redraw;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView != null) newTextView.VisualLinesChanged += Redraw;
        }

        // Only redraw: re-measuring here would lay the editor out again, which changes the visual lines again...
        private void Redraw(object? sender, EventArgs e) => InvalidateVisual();

        /// <summary>New files: the numbers may need a wider margin.</summary>
        public void Reset() => InvalidateMeasure();

        protected override void OnRender(DrawingContext dc)
        {
            var view = TextView;
            var list = lines();
            if (view == null || !view.VisualLinesValid) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var visual in view.VisualLines)
            {
                int index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= list.Count || list[index].Number is not int number) continue;
                var text = new FormattedText(number.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Face, 12, NumberBrush, dpi);
                double y = visual.VisualTop - view.VerticalOffset + (visual.Height - text.Height) / 2;
                dc.DrawText(text, new Point(RenderSize.Width - text.Width - 14, y));
            }
        }
    }
}
