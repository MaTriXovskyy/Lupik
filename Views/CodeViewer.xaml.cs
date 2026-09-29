using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace QuickPeek.Views;

public partial class CodeViewer : UserControl
{
    private string _currentFilePath = "";
    private readonly MarkdownCodeBlockColorizer _codeBlocks = new();

    public CodeViewer()
    {
        InitializeComponent();
        TextEditorControl.TextArea.TextView.LineTransformers.Clear();
        TextEditorControl.TextArea.TextView.LineTransformers.Add(_codeBlocks);
        StyleEditor();
    }

    private void StyleEditor()
    {
        var area = TextEditorControl.TextArea;

        // Subtle selection and current-line highlight
        area.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x58, 0x5B, 0x70));
        area.SelectionBorder = null;
        area.SelectionForeground = null;
        area.SelectionCornerRadius = 2;
        area.Caret.CaretBrush = new SolidColorBrush(CodeTheme.Subtext);

        TextEditorControl.Options.HighlightCurrentLine = true;
        area.TextView.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(0x40, 0x31, 0x32, 0x44));
        area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);

        TextEditorControl.Options.EnableHyperlinks = false;
        TextEditorControl.Options.EnableEmailHyperlinks = false;

        // Breathing room around the line numbers; drop the dotted separator line
        for (int i = area.LeftMargins.Count - 1; i >= 0; i--)
        {
            if (area.LeftMargins[i] is ICSharpCode.AvalonEdit.Editing.LineNumberMargin numbers)
                numbers.Margin = new Thickness(14, 0, 16, 0);
            else if (area.LeftMargins[i] is System.Windows.Shapes.Line)
                area.LeftMargins.RemoveAt(i);
        }
    }

    // Bigger files are cut off: loading tens of MB into the editor freezes the UI
    private const int MaxPreviewChars = 1024 * 1024;
    private int _loadToken;

    /// <summary>
    /// Reads the file on a background thread. Returns false if a newer load superseded this one.
    /// </summary>
    public async Task<bool> LoadFileAsync(string filePath)
    {
        int token = ++_loadToken;
        try
        {
            var (content, size, truncated) = await Task.Run(() =>
            {
                // FileShare.ReadWrite so locked files can still be previewed
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var buffer = new char[MaxPreviewChars];
                int read = reader.ReadBlock(buffer, 0, buffer.Length);
                bool more = reader.Peek() >= 0;
                return (new string(buffer, 0, read), stream.Length, more);
            });

            if (token != _loadToken) return false;

            _currentFilePath = filePath;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            SetHighlightingForExtension(ext);

            TextEditorControl.Text = content;
            TextEditorControl.ScrollToHome();
            _codeBlocks.Analyze(TextEditorControl.Document, ext is ".md" or ".markdown");
            TextEditorControl.TextArea.TextView.Redraw();

            FileSizeText.Text = FormatFileSize(size);
            LinesCountText.Text = truncated
                ? $"pokazano pierwsze {TextEditorControl.Document.LineCount} linii (plik jest za duży na cały podgląd)"
                : $"{TextEditorControl.Document.LineCount} linii";
            return true;
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return false;
            TextEditorControl.SyntaxHighlighting = null;
            TextEditorControl.Text = $"Błąd podczas odczytu pliku:\n{ex.Message}";
            _codeBlocks.Analyze(TextEditorControl.Document, false);
            LinesCountText.Text = "Błąd";
            LanguageText.Text = "!";
            FileSizeText.Text = "—";
            return true;
        }
    }

    private void SetHighlightingForExtension(string ext)
    {
        string langName = ext.TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrEmpty(langName)) langName = "TXT";

        IHighlightingDefinition? definition = HighlightingManager.Instance.GetDefinitionByExtension(ext);

        // Fallbacks for common extensions without exact matching extension
        if (definition == null)
        {
            definition = ext switch
            {
                ".ts" or ".jsx" or ".tsx" or ".mjs" or ".cjs" => HighlightingManager.Instance.GetDefinitionByExtension(".js"),
                ".json" or ".jsonc" => HighlightingManager.Instance.GetDefinitionByExtension(".js"),
                ".rs" or ".go" or ".kt" => HighlightingManager.Instance.GetDefinitionByExtension(".cs"),
                ".py" => HighlightingManager.Instance.GetDefinitionByExtension(".py") ?? HighlightingManager.Instance.GetDefinitionByExtension(".js"),
                ".ps1" or ".psm1" or ".cmd" or ".bat" => HighlightingManager.Instance.GetDefinitionByExtension(".bat"),
                ".sql" => HighlightingManager.Instance.GetDefinitionByExtension(".sql"),
                _ => null
            };
        }

        CodeTheme.Apply(definition);
        TextEditorControl.SyntaxHighlighting = definition;
        LanguageText.Text = langName;
    }

    /// <summary>Drops the loaded text (called when QuickPeek goes idle).</summary>
    public void Release()
    {
        _loadToken++;
        TextEditorControl.Text = "";
        _codeBlocks.Analyze(TextEditorControl.Document, false);
    }

    public void ScrollLines(int direction)
    {
        if (direction < 0) TextEditorControl.LineUp();
        else TextEditorControl.LineDown();
    }

    private void OnWordWrapChanged(object sender, RoutedEventArgs e)
    {
        TextEditorControl.WordWrap = WordWrapToggle.IsChecked == true;
    }

    private async void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TextEditorControl.Text);
            CopyIcon.Kind = "check";
            CopyText.Text = "Skopiowano";
            CopyButton.Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1));

            await Task.Delay(1800);

            CopyIcon.Kind = "copy";
            CopyText.Text = "Kopiuj";
            CopyButton.ClearValue(ForegroundProperty);
        }
        catch
        {
            // Clipboard access issue
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
