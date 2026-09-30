using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace Lupik.Views;

/// <summary>
/// Highlights fenced code blocks (```lang ... ```) inside Markdown using the block's own language,
/// on top of the regular Markdown highlighting.
/// </summary>
public class MarkdownCodeBlockColorizer : DocumentColorizingTransformer
{
    private static readonly Regex FenceRegex = new(@"^\s*(```|~~~)\s*([\w#+.-]*)", RegexOptions.Compiled);

    // A shade darker than the page in either theme
    private static Brush BlockBackground => Core.Palette.IsLight ? LightBlock : Core.Palette.Brush(0x11111B, 0x60);
    private static readonly Brush LightBlock = new SolidColorBrush(Color.FromArgb(0x0C, 0, 0, 0));
    private static Brush FenceForeground => new SolidColorBrush(CodeTheme.Overlay);
    private static Brush PlainForeground => new SolidColorBrush(CodeTheme.Text);

    private sealed class CodeBlock
    {
        public int FenceStart;   // line number of opening fence
        public int FenceEnd;     // line number of closing fence (or last line if unclosed)
        public DocumentHighlighter? Highlighter;
    }

    private readonly List<CodeBlock> _blocks = new();

    public bool Enabled { get; private set; }

    public void Analyze(TextDocument document, bool enabled)
    {
        _blocks.Clear();
        Enabled = enabled;
        if (!enabled) return;

        CodeBlock? open = null;
        string openFence = "";
        var body = new List<string>();
        string lang = "";

        for (int n = 1; n <= document.LineCount; n++)
        {
            string text = document.GetText(document.GetLineByNumber(n));
            var m = FenceRegex.Match(text);

            if (open == null)
            {
                if (!m.Success) continue;
                open = new CodeBlock { FenceStart = n };
                openFence = m.Groups[1].Value;
                lang = m.Groups[2].Value;
                body.Clear();
            }
            else if (m.Success && m.Groups[1].Value == openFence && m.Groups[2].Value.Length == 0)
            {
                open.FenceEnd = n;
                Finish(open, lang, body);
                open = null;
            }
            else
            {
                body.Add(text);
            }
        }

        if (open != null)
        {
            open.FenceEnd = document.LineCount + 1; // unclosed: runs to end of file
            Finish(open, lang, body);
        }
    }

    private void Finish(CodeBlock block, string lang, List<string> body)
    {
        var definition = ResolveLanguage(lang);
        if (definition != null)
        {
            CodeTheme.Apply(definition);
            var subDoc = new TextDocument(string.Join("\n", body));
            block.Highlighter = new DocumentHighlighter(subDoc, definition);
        }
        _blocks.Add(block);
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!Enabled || _blocks.Count == 0) return;

        int n = line.LineNumber;
        CodeBlock? block = null;
        foreach (var b in _blocks)
        {
            if (n >= b.FenceStart && n <= b.FenceEnd) { block = b; break; }
        }
        if (block == null || line.Length == 0) return;

        int start = line.Offset, end = line.EndOffset;

        // Fence lines: dimmed
        if (n == block.FenceStart || n == block.FenceEnd)
        {
            ChangeLinePart(start, end, el =>
            {
                el.TextRunProperties.SetForegroundBrush(FenceForeground);
                el.TextRunProperties.SetBackgroundBrush(BlockBackground);
                ResetTypeface(el);
            });
            return;
        }

        // Body: reset Markdown styling, then apply the block language's highlighting
        ChangeLinePart(start, end, el =>
        {
            el.TextRunProperties.SetForegroundBrush(PlainForeground);
            el.TextRunProperties.SetBackgroundBrush(BlockBackground);
            ResetTypeface(el);
        });

        if (block.Highlighter == null) return;

        int subLineNumber = n - block.FenceStart;
        if (subLineNumber < 1 || subLineNumber > block.Highlighter.Document.LineCount) return;

        var highlighted = block.Highlighter.HighlightLine(subLineNumber);
        int subLineOffset = highlighted.DocumentLine.Offset;

        foreach (var section in highlighted.Sections)
        {
            int s = start + section.Offset - subLineOffset;
            int e = s + section.Length;
            if (s < start || e > end || s >= e) continue;

            var color = section.Color;
            ChangeLinePart(s, e, el =>
            {
                var brush = color.Foreground?.GetBrush(null);
                if (brush != null) el.TextRunProperties.SetForegroundBrush(brush);

                if (color.FontWeight != null || color.FontStyle != null)
                {
                    var tf = el.TextRunProperties.Typeface;
                    el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily,
                        color.FontStyle ?? FontStyles.Normal,
                        color.FontWeight ?? FontWeights.Normal,
                        tf.Stretch));
                }
            });
        }
    }

    private static void ResetTypeface(VisualLineElement el)
    {
        var tf = el.TextRunProperties.Typeface;
        el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, FontStyles.Normal, FontWeights.Normal, tf.Stretch));
    }

    private static IHighlightingDefinition? ResolveLanguage(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        var m = HighlightingManager.Instance;

        string ext = lang.ToLowerInvariant() switch
        {
            "js" or "javascript" or "jsx" or "ts" or "typescript" or "tsx" or "json" or "jsonc" => ".js",
            "cs" or "csharp" or "c#" => ".cs",
            "py" or "python" => ".py",
            "html" or "htm" => ".html",
            "xml" or "xaml" or "svg" or "csproj" => ".xml",
            "css" or "scss" or "less" => ".css",
            "sql" => ".sql",
            "ps" or "ps1" or "powershell" or "pwsh" or "bash" or "sh" or "shell" or "bat" or "cmd" => ".ps1",
            "c" or "cpp" or "c++" or "h" or "hpp" => ".cpp",
            "java" or "kotlin" or "kt" => ".java",
            "php" => ".php",
            "vb" or "vbnet" => ".vb",
            "md" or "markdown" => ".md",
            "rs" or "rust" or "go" or "golang" => ".cs",
            _ => "." + lang.ToLowerInvariant()
        };

        return m.GetDefinitionByExtension(ext) ?? m.GetDefinition(lang);
    }
}
