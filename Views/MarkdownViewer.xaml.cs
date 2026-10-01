using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit.Highlighting;
using Lupik.Localization;
using Markdig;
using Markdig.Extensions.Tables;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;
using WpfRow = System.Windows.Documents.TableRow;
using WpfCell = System.Windows.Documents.TableCell;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;

namespace Lupik.Views;

/// <summary>
/// Markdown drawn as a document (headings, lists, task lists, quotes, tables, links, pictures, highlighted code),
/// in the app's theme. "Code" (or M) switches to the source in the code view, which is also where it's edited.
/// </summary>
public partial class MarkdownViewer : UserControl, ISearchable
{
    public static readonly string[] Extensions = { ".md", ".markdown", ".mdown", ".mkd" };

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private int _loadToken;
    private string _folder = "";

    /// <summary>"Code" clicked: show the source.</summary>
    public event Action? CodeRequested;

    public MarkdownViewer()
    {
        InitializeComponent();
    }

    public static bool CanOpen(string ext) => Array.IndexOf(Extensions, ext) >= 0;

    public async Task<bool> LoadAsync(string filePath)
    {
        int token = ++_loadToken;
        string text;
        MarkdownDocument parsed;
        try
        {
            (text, parsed) = await Task.Run(() =>
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var (encoding, _) = Core.TextEncoding.Detect(stream);
                using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
                string content = reader.ReadToEnd();
                return (content, Markdown.Parse(content, Pipeline));
            });
        }
        catch (Exception ex)
        {
            App.Log($"[MarkdownViewer] Could not read '{filePath}': {ex.Message}");
            return false;
        }
        if (token != _loadToken) return true;

        _folder = Path.GetDirectoryName(filePath) ?? "";
        _matches.Clear();
        var doc = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        foreach (var block in parsed) AddBlock(doc.Blocks, block);
        Page.Document = doc;
        PageScroller.ScrollToHome();

        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        SummaryText.Text = Loc.Plural("count.words", words) + "  •  " + FormatSize(new FileInfo(filePath).Length);
        return true;
    }

    public void Release()
    {
        _loadToken++;
        Page.Document = new FlowDocument();
        _matches.Clear();
    }

    private void OnCodeClicked(object sender, RoutedEventArgs e) => CodeRequested?.Invoke();

    // ---------- Blocks ----------

    private void AddBlock(BlockCollection into, MdBlock block)
    {
        switch (block)
        {
            case HeadingBlock h:
            {
                var p = new Paragraph { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, h.Level <= 2 ? 22 : 16, 0, 8) };
                p.FontSize = h.Level switch { 1 => 28, 2 => 22, 3 => 18, 4 => 16, _ => 14.5 };
                p.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
                Themed(p, "cFBF8F2");
                if (h.Level <= 2)
                {
                    p.Padding = new Thickness(0, 0, 0, 6);
                    p.BorderThickness = new Thickness(0, 0, 0, 1);
                    p.SetResourceReference(Paragraph.BorderBrushProperty, "c2F2B27");
                }
                AddInlines(p.Inlines, h.Inline);
                into.Add(p);
                break;
            }
            case ParagraphBlock pb:
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 12), LineHeight = 23 };
                AddInlines(p.Inlines, pb.Inline);
                into.Add(p);
                break;
            }
            case QuoteBlock q:
            {
                var section = new Section { Padding = new Thickness(14, 2, 0, 2), Margin = new Thickness(0, 0, 0, 12), BorderThickness = new Thickness(3, 0, 0, 0) };
                section.SetResourceReference(Section.BorderBrushProperty, "Gold");
                Themed(section, "cB5AB9D");
                foreach (var child in q) AddBlock(section.Blocks, child);
                into.Add(section);
                break;
            }
            case ListBlock list:
            {
                bool tasks = list.OfType<ListItemBlock>().All(item => item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList });
                var l = new List
                {
                    MarkerStyle = tasks ? TextMarkerStyle.None : list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(tasks ? 4 : 24, 0, 0, 0),
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out int start)) l.StartIndex = start;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var li = new ListItem();
                    foreach (var child in item) AddBlock(li.Blocks, child);
                    foreach (var p in li.Blocks.OfType<Paragraph>()) p.Margin = new Thickness(0, 0, 0, 4);
                    l.ListItems.Add(li);
                }
                into.Add(l);
                break;
            }
            case FencedCodeBlock or CodeBlock:
                into.Add(CodeBox((LeafBlock)block, (block as FencedCodeBlock)?.Info));
                break;
            case ThematicBreakBlock:
            {
                var rule = new Paragraph { Margin = new Thickness(0, 10, 0, 18), FontSize = 1, BorderThickness = new Thickness(0, 1, 0, 0) };
                rule.SetResourceReference(Paragraph.BorderBrushProperty, "c3B3631");
                into.Add(rule);
                break;
            }
            case MdTable table:
                into.Add(TableOf(table));
                break;
            case HtmlBlock html:
            {
                // Raw HTML isn't rendered: shown as-is, dimmed
                var p = new Paragraph(new Run(string.Join("\n", html.Lines.Lines.Take(html.Lines.Count).Select(l => l.ToString()))))
                    { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
                Themed(p, "c8A8074");
                into.Add(p);
                break;
            }
            case ContainerBlock container:
                foreach (var child in container) AddBlock(into, child);
                break;
        }
    }

    /// <summary>A code block, coloured like the code view (same palette, same languages).</summary>
    private WpfBlock CodeBox(LeafBlock block, string? language)
    {
        string code = string.Join("\n", block.Lines.Lines.Take(block.Lines.Count).Select(l => l.ToString())).TrimEnd('\n');
        var p = new Paragraph
        {
            FontFamily = new FontFamily("Cascadia Mono, Cascadia Code, Consolas"), FontSize = 12.5, LineHeight = 19,
            Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 14), BorderThickness = new Thickness(1),
        };
        p.SetResourceReference(Paragraph.BackgroundProperty, "c161412");
        p.SetResourceReference(Paragraph.BorderBrushProperty, "c2A2622");
        Themed(p, "cE9E2D6");

        var definition = HighlightingFor(language);
        if (definition == null)
        {
            p.Inlines.Add(new Run(code));
            return p;
        }
        CodeTheme.Apply(definition);
        var document = new ICSharpCode.AvalonEdit.Document.TextDocument(code);
        var highlighter = new ICSharpCode.AvalonEdit.Highlighting.DocumentHighlighter(document, definition);
        for (int n = 1; n <= document.LineCount; n++)
        {
            var line = document.GetLineByNumber(n);
            var highlighted = highlighter.HighlightLine(n);
            int pos = line.Offset;
            foreach (var section in highlighted.Sections.Where(s => s.Color?.Foreground != null).OrderBy(s => s.Offset))
            {
                if (section.Offset < pos) continue; // nested sections: the outer one already coloured this text
                if (section.Offset > pos) p.Inlines.Add(new Run(document.GetText(pos, section.Offset - pos)));
                var run = new Run(document.GetText(section.Offset, section.Length));
                if (section.Color.Foreground.GetColor(null) is Color c) run.Foreground = new SolidColorBrush(c);
                if (section.Color.FontWeight != null) run.FontWeight = section.Color.FontWeight.Value;
                if (section.Color.FontStyle != null) run.FontStyle = section.Color.FontStyle.Value;
                p.Inlines.Add(run);
                pos = section.Offset + section.Length;
            }
            if (pos < line.EndOffset) p.Inlines.Add(new Run(document.GetText(pos, line.EndOffset - pos)));
            if (n < document.LineCount) p.Inlines.Add(new LineBreak());
        }
        return p;
    }

    private static readonly Dictionary<string, string> LanguageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = ".cs", ["csharp"] = ".cs", ["c#"] = ".cs", ["js"] = ".js", ["javascript"] = ".js", ["ts"] = ".js", ["typescript"] = ".js",
        ["json"] = ".json", ["xml"] = ".xml", ["xaml"] = ".xml", ["html"] = ".html", ["css"] = ".css", ["py"] = ".py", ["python"] = ".py",
        ["sql"] = ".sql", ["cpp"] = ".cpp", ["c++"] = ".cpp", ["c"] = ".c", ["java"] = ".java", ["php"] = ".php", ["ps1"] = ".ps1",
        ["powershell"] = ".ps1", ["md"] = ".md", ["markdown"] = ".md", ["vb"] = ".vb", ["fs"] = ".fs", ["bat"] = ".bat", ["cmd"] = ".bat",
    };

    private static IHighlightingDefinition? HighlightingFor(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        string lang = language.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return LanguageExtensions.TryGetValue(lang, out var ext) ? HighlightingManager.Instance.GetDefinitionByExtension(ext) : null;
    }

    private WpfTable TableOf(MdTable table)
    {
        var t = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 14), BorderThickness = new Thickness(1) };
        t.SetResourceReference(WpfTable.BorderBrushProperty, "c3B3631");
        int columns = table.OfType<MdRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (int i = 0; i < columns; i++) t.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        foreach (var row in table.OfType<MdRow>())
        {
            var tr = new WpfRow();
            if (row.IsHeader)
            {
                tr.FontWeight = FontWeights.SemiBold;
                tr.SetResourceReference(WpfRow.BackgroundProperty, "c1B1917");
            }
            for (int i = 0; i < row.Count; i++)
            {
                var cell = new WpfCell { Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0, 0, 1, 1) };
                cell.SetResourceReference(WpfCell.BorderBrushProperty, "c3B3631");
                if (row[i] is MdCell md)
                {
                    foreach (var child in md) AddBlock(cell.Blocks, child);
                    foreach (var p in cell.Blocks.OfType<Paragraph>()) p.Margin = new Thickness(0);
                    var align = i < table.ColumnDefinitions.Count ? table.ColumnDefinitions[i].Alignment : null;
                    if (align == TableColumnAlign.Right) foreach (var p in cell.Blocks.OfType<Paragraph>()) p.TextAlignment = TextAlignment.Right;
                    if (align == TableColumnAlign.Center) foreach (var p in cell.Blocks.OfType<Paragraph>()) p.TextAlignment = TextAlignment.Center;
                }
                tr.Cells.Add(cell);
            }
            group.Rows.Add(tr);
        }
        t.RowGroups.Add(group);
        return t;
    }

    // ---------- Inlines ----------

    private void AddInlines(InlineCollection into, ContainerInline? container)
    {
        if (container == null) return;
        foreach (var inline in container) AddInline(into, inline);
    }

    private void AddInline(InlineCollection into, MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                into.Add(new Run(literal.Content.ToString()));
                break;
            case HtmlEntityInline entity:
                into.Add(new Run(entity.Transcoded.ToString()));
                break;
            case LineBreakInline br:
                if (br.IsHard) into.Add(new LineBreak()); else into.Add(new Run(" "));
                break;
            case CodeInline code:
            {
                var run = new Run(code.Content) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
                run.SetResourceReference(TextElement.BackgroundProperty, "c24211E");
                Themed(run, "cECE6DC");
                into.Add(run);
                break;
            }
            case TaskList task:
            {
                var box = new Run(task.Checked ? "☑ " : "☐ ") { FontSize = 16 };
                Themed(box, task.Checked ? "Gold" : "c8A8074");
                into.Add(box);
                break;
            }
            case EmphasisInline em:
            {
                Span span = em.DelimiterChar == '~' ? new Span() : em.DelimiterCount >= 2 ? new Bold() : new Italic();
                if (em.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                AddInlines(span.Inlines, em);
                into.Add(span);
                break;
            }
            case LinkInline { IsImage: true } image:
                into.Add(ImageOf(image.Url, image.Title));
                break;
            case LinkInline link:
            {
                var h = LinkTo(link.Url);
                AddInlines(h.Inlines, link);
                if (h.Inlines.Count == 0) h.Inlines.Add(new Run(link.Url));
                into.Add(h);
                break;
            }
            case AutolinkInline auto:
            {
                var h = LinkTo(auto.IsEmail ? "mailto:" + auto.Url : auto.Url);
                h.Inlines.Add(new Run(auto.Url));
                into.Add(h);
                break;
            }
            case HtmlInline html:
            {
                var run = new Run(html.Tag);
                Themed(run, "c8A8074");
                into.Add(run);
                break;
            }
            case ContainerInline container:
                AddInlines(into, container);
                break;
        }
    }

    /// <summary>A link: web addresses open in the browser, relative ones next to the file in their app.</summary>
    private Hyperlink LinkTo(string? url)
    {
        var h = new Hyperlink { TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = url };
        h.SetResourceReference(TextElement.ForegroundProperty, "GoldHover");
        h.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#')) return;
            string target = Uri.TryCreate(url, UriKind.Absolute, out var abs) && !abs.IsFile ? url : Path.GetFullPath(Path.Combine(_folder, Uri.UnescapeDataString(url)));
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { App.Log($"[MarkdownViewer] Link '{target}': {ex.Message}"); }
        };
        return h;
    }

    private WpfInline ImageOf(string? url, string? title)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return new Run("");
            Uri uri = Uri.TryCreate(url, UriKind.Absolute, out var abs) ? abs : new Uri(Path.GetFullPath(Path.Combine(_folder, Uri.UnescapeDataString(url))));
            if (uri.IsFile && !File.Exists(uri.LocalPath)) return new Run($"[{title ?? Path.GetFileName(uri.LocalPath)}]");
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.DecodePixelWidth = 1600;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            var image = new Image { Source = bmp, MaxWidth = 800, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, ToolTip = title };
            return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom };
        }
        catch (Exception ex)
        {
            App.Log($"[MarkdownViewer] Image '{url}': {ex.Message}");
            return new Run("");
        }
    }

    private static void Themed(FrameworkContentElement element, string foregroundKey) =>
        element.SetResourceReference(TextElement.ForegroundProperty, foregroundKey);

    // ---------- Search (like the Word view) ----------

    private static readonly Brush MatchBrush = Frozen(Color.FromArgb(0x70, 0xFF, 0xD8, 0x4D));
    private static readonly Brush CurrentBrush = Frozen(Color.FromArgb(0xC0, 0xFF, 0x9A, 0x2E));
    private readonly List<TextRange> _matches = new();
    private int _current = -1;

    public Task<int> SearchAsync(string query, CancellationToken token)
    {
        ClearSearch();
        if (query.Length == 0 || Page.Document == null) return Task.FromResult(0);
        var found = new List<TextRange>();
        foreach (var paragraph in Paragraphs(Page.Document.Blocks).ToList())
        {
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
                if (start != null && end != null) found.Add(new TextRange(start, end));
            }
        }
        foreach (var range in found) range.ApplyPropertyValue(TextElement.BackgroundProperty, MatchBrush);
        _matches.AddRange(found);
        return Task.FromResult(_matches.Count);
    }

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
                case WpfTable t:
                    foreach (var group in t.RowGroups)
                        foreach (var row in group.Rows)
                            foreach (var cell in row.Cells)
                                foreach (var x in Paragraphs(cell.Blocks)) yield return x;
                    break;
            }
        }
    }

    /// <summary>Scrolls by a number of pixels (↑ / ↓ keys).</summary>
    public void ScrollBy(double delta) => PageScroller.ScrollToVerticalOffset(PageScroller.VerticalOffset + delta);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
        return $"{len:0.##} {sizes[order]}";
    }
}
