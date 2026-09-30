using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lupik.Core.Office;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>Word documents (.docx) drawn by Lupik itself, on one long sheet of paper. No Office needed.</summary>
public partial class DocxViewer : UserControl, ISearchable
{
    private int _loadToken;

    public DocxViewer()
    {
        InitializeComponent();
    }

    public static bool CanOpen(string ext) => Array.IndexOf(DocxReader.Extensions, ext) >= 0;

    /// <summary>
    /// Reads the document in the background. False only if it couldn't be read (the caller falls back to Windows'
    /// previewer); a load superseded by a newer one returns true, and the caller sees its own token is stale.
    /// </summary>
    public async Task<bool> LoadAsync(string filePath)
    {
        int token = ++_loadToken;
        DocxDocument doc;
        try
        {
            doc = await Task.Run(() => DocxReader.Read(filePath));
        }
        catch (Exception ex)
        {
            App.Log($"[DocxViewer] Could not read '{filePath}': {ex.Message}");
            return false;
        }
        if (token != _loadToken) return true;

        _matches.Clear();
        Page.Document = Build(doc);
        PageScroller.ScrollToHome();
        BadgeText.Text = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

        int words = doc.Blocks.Sum(CountWords);
        long size = new FileInfo(filePath).Length;
        SummaryText.Text = Loc.Plural("count.words", words) + "  •  " + FormatSize(size) +
                           (doc.ImageCount > 0 ? "  •  " + Loc.Plural("count.images", doc.ImageCount) : "");
        return true;
    }

    /// <summary>Drops the document (called when Lupik goes idle).</summary>
    public void Release()
    {
        _loadToken++;
        _matches.Clear();
        Page.Document = new FlowDocument();
    }

    // ---------- Building the FlowDocument ----------

    private const double PagePaddingX = 80; // the page margin (Padding of the RichTextBox in the XAML)
    private static double Px(double pt) => pt * 96 / 72;

    private static FlowDocument Build(DocxDocument doc)
    {
        var flow = new FlowDocument
        {
            FontFamily = new FontFamily(doc.DefaultFont + ", Calibri, Segoe UI"),
            FontSize = Px(doc.DefaultSizePt),
            Foreground = Brushes.Black,
            Background = Brushes.White,
            TextAlignment = TextAlignment.Left,
            LineHeight = double.NaN,
        };
        foreach (var block in doc.Blocks) flow.Blocks.Add(BuildBlock(block, doc));
        if (flow.Blocks.Count == 0) flow.Blocks.Add(new Paragraph());
        return flow;
    }

    private static Block BuildBlock(DocBlock block, DocxDocument doc) => block switch
    {
        DocParagraph p => BuildParagraph(p, doc),
        DocTable t => BuildTable(t, doc),
        _ => new Paragraph(),
    };

    private static Paragraph BuildParagraph(DocParagraph p, DocxDocument doc)
    {
        var paragraph = new Paragraph { TextAlignment = p.Alignment, Margin = new Thickness(0, 0, 0, Px(6)) };

        // Headings: the document's own style sizes win (they're on the runs); these are only the fallback
        double? headingSize = p.IsTitle ? 26 : p.HeadingLevel switch { 1 => 16, 2 => 13, 3 => 12, > 3 => 11, _ => null };
        if (p.IsTitle || p.HeadingLevel > 0)
        {
            paragraph.Margin = new Thickness(0, Px(p.HeadingLevel <= 1 ? 16 : 10), 0, Px(4));
            paragraph.KeepWithNext = true;
        }

        if (p.ListLabel != null)
        {
            double indent = 24 * (p.IndentLevel + 1) + 12;
            paragraph.Margin = new Thickness(indent, 0, 0, Px(3));
            paragraph.TextIndent = -18;
            if (p.ListLabel.Length > 0) paragraph.Inlines.Add(new Run(p.ListLabel + "  "));
        }

        foreach (var r in p.Runs)
        {
            if (r.Image != null)
            {
                double scale = Math.Min(1, (816 - 2 * PagePaddingX) / Math.Max(1, r.ImageWidth));
                paragraph.Inlines.Add(new InlineUIContainer(new Image
                {
                    Source = r.Image,
                    Width = r.ImageWidth * scale,
                    Height = r.ImageHeight * scale,
                    Stretch = Stretch.Fill,
                }) { BaselineAlignment = BaselineAlignment.Bottom });
                continue;
            }
            if (r.LineBreak || r.PageBreak) { paragraph.Inlines.Add(new LineBreak()); continue; }
            if (r.Text.Length == 0) continue;

            var run = new Run(r.Text == "\t" ? "    " : r.Text);
            if (r.Bold || (r.SizePt == null && (p.IsTitle || p.HeadingLevel is > 0 and <= 2))) run.FontWeight = FontWeights.Bold;
            if (r.Italic) run.FontStyle = FontStyles.Italic;
            var decorations = new TextDecorationCollection();
            if (r.Underline) decorations.Add(TextDecorations.Underline);
            if (r.Strike) decorations.Add(TextDecorations.Strikethrough);
            if (decorations.Count > 0) run.TextDecorations = decorations;
            if (r.SizePt is double size) run.FontSize = Px(size);
            else if (headingSize is double hs) run.FontSize = Px(hs);
            if (r.Color is Color color) run.Foreground = new SolidColorBrush(color);
            else if (p.HeadingLevel > 0 && r.SizePt == null) run.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x38, 0x64));
            if (r.Highlight is Color highlight) run.Background = new SolidColorBrush(highlight);
            paragraph.Inlines.Add(run);
        }
        return paragraph;
    }

    private static Table BuildTable(DocTable t, DocxDocument doc)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, Px(4), 0, Px(10)), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) };
        int columns = t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count);
        for (int i = 0; i < columns; i++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        foreach (var row in t.Rows)
        {
            var tableRow = new TableRow();
            foreach (var cell in row)
            {
                var tableCell = new TableCell { BorderBrush = new SolidColorBrush(Color.FromRgb(0xBF, 0xBF, 0xBF)), BorderThickness = new Thickness(0.5), Padding = new Thickness(5, 3, 5, 3) };
                foreach (var block in cell)
                {
                    var built = BuildBlock(block, doc);
                    if (built is Paragraph para) para.Margin = new Thickness(0, 0, 0, 2);
                    tableCell.Blocks.Add(built);
                }
                if (tableCell.Blocks.Count == 0) tableCell.Blocks.Add(new Paragraph());
                tableRow.Cells.Add(tableCell);
            }
            group.Rows.Add(tableRow);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static int CountWords(DocBlock block) => block switch
    {
        DocParagraph p => p.Runs.Sum(r => r.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length),
        DocTable t => t.Rows.Sum(r => r.Sum(c => c.Sum(CountWords))),
        _ => 0,
    };

    // ---------- Search ----------

    private static readonly Brush MatchBrush = Frozen(Color.FromArgb(0x90, 0xFF, 0xD8, 0x4D));
    private static readonly Brush CurrentBrush = Frozen(Color.FromRgb(0xFF, 0x9A, 0x2E));
    private readonly List<TextRange> _matches = new();
    private int _current = -1;

    public Task<int> SearchAsync(string query, CancellationToken token)
    {
        ClearSearch();
        if (query.Length == 0 || Page.Document == null) return Task.FromResult(0);

        // Find everything first: highlighting splits runs, which would upset walking the document
        var found = new List<TextRange>();
        foreach (var paragraph in Paragraphs(Page.Document.Blocks).ToList())
        {
            // The paragraph's text, with where each piece of it starts in the document
            var text = new System.Text.StringBuilder();
            var pieces = new List<(TextPointer Start, int Offset)>();
            for (var pos = paragraph.ContentStart; pos != null && pos.CompareTo(paragraph.ContentEnd) < 0; pos = pos.GetNextContextPosition(LogicalDirection.Forward))
            {
                if (pos.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
                pieces.Add((pos, text.Length));
                text.Append(pos.GetTextInRun(LogicalDirection.Forward));
            }
            string s = text.ToString();
            for (int i = s.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); i >= 0;
                 i = s.IndexOf(query, i + Math.Max(1, query.Length), StringComparison.CurrentCultureIgnoreCase))
            {
                var start = PointerAt(pieces, i);
                var end = PointerAt(pieces, i + query.Length);
                if (start == null || end == null) continue;
                found.Add(new TextRange(start, end));
            }
        }
        foreach (var range in found) range.ApplyPropertyValue(TextElement.BackgroundProperty, MatchBrush);
        _matches.AddRange(found);
        return Task.FromResult(_matches.Count);
    }

    /// <summary>Offset in the paragraph's text -> position in the document (pieces are the text runs).</summary>
    private static TextPointer? PointerAt(List<(TextPointer Start, int Offset)> pieces, int offset)
    {
        for (int k = pieces.Count - 1; k >= 0; k--)
            if (pieces[k].Offset <= offset) return pieces[k].Start.GetPositionAtOffset(offset - pieces[k].Offset, LogicalDirection.Forward);
        return null;
    }

    public void ShowMatch(int index)
    {
        if (index < 0 || index >= _matches.Count) return;
        if (_current >= 0 && _current < _matches.Count) _matches[_current].ApplyPropertyValue(TextElement.BackgroundProperty, MatchBrush);
        _current = index;
        var match = _matches[index];
        match.ApplyPropertyValue(TextElement.BackgroundProperty, CurrentBrush);

        UpdateLayout();
        var rect = match.Start.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty) return;
        var top = Page.TranslatePoint(rect.TopLeft, PageScroller).Y + PageScroller.VerticalOffset;
        PageScroller.ScrollToVerticalOffset(Math.Max(0, top - PageScroller.ViewportHeight / 3));
    }

    public void ClearSearch()
    {
        foreach (var match in _matches) match.ApplyPropertyValue(TextElement.BackgroundProperty, null!);
        _matches.Clear();
        _current = -1;
    }

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p: yield return p; break;
                case Section s: foreach (var x in Paragraphs(s.Blocks)) yield return x; break;
                case List l: foreach (var item in l.ListItems) foreach (var x in Paragraphs(item.Blocks)) yield return x; break;
                case Table t:
                    foreach (var group in t.RowGroups)
                        foreach (var row in group.Rows)
                            foreach (var cell in row.Cells)
                                foreach (var x in Paragraphs(cell.Blocks)) yield return x;
                    break;
            }
        }
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }
}
