using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

using Lupik.Localization;
namespace Lupik.Views;

public partial class CodeViewer : UserControl, ISearchable, IEditable
{
    private string _currentFilePath = "";
    private readonly MarkdownCodeBlockColorizer _codeBlocks = new();

    public CodeViewer()
    {
        InitializeComponent();
        TextEditorControl.TextArea.TextView.LineTransformers.Clear();
        TextEditorControl.TextArea.TextView.LineTransformers.Add(_codeBlocks);
        StyleEditor();
        _search = new SearchHighlighter(TextEditorControl);
    }

    // --- Edit mode (E) ---

    private System.Text.Encoding _encoding = System.Text.Encoding.UTF8;
    private bool _bom, _truncated, _readFailed, _editing;

    public event Action? DirtyChanged;

    public bool IsDirty => _editing && !TextEditorControl.Document.UndoStack.IsOriginalFile;

    public string? WhyNotEditable() =>
        _readFailed ? Loc.T("edit.cantRead")
        : _truncated ? Loc.T("edit.tooBig")
        : null;

    public void BeginEdit()
    {
        _editing = true;
        TextEditorControl.IsReadOnly = false;
        TextEditorControl.Document.UndoStack.MarkAsOriginalFile();
        TextEditorControl.Document.UndoStack.PropertyChanged += OnUndoStackChanged;
        TextEditorControl.Document.TextChanged += OnTextEdited;
        TextEditorControl.Focus();
        TextEditorControl.TextArea.Focus();
        TextEditorControl.TextArea.Caret.Show();
    }

    public void EndEdit()
    {
        _editing = false;
        TextEditorControl.IsReadOnly = true;
        TextEditorControl.Document.UndoStack.PropertyChanged -= OnUndoStackChanged;
        TextEditorControl.Document.TextChanged -= OnTextEdited;
    }

    private void OnUndoStackChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ICSharpCode.AvalonEdit.Document.UndoStack.IsOriginalFile)) DirtyChanged?.Invoke();
    }

    /// <summary>Markdown code blocks follow the edits (their coloring spans lines).</summary>
    private void OnTextEdited(object? sender, EventArgs e)
    {
        if (!_codeBlocksPending)
        {
            _codeBlocksPending = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                _codeBlocksPending = false;
                string ext = Path.GetExtension(_currentFilePath).ToLowerInvariant();
                _codeBlocks.Analyze(TextEditorControl.Document, ext is ".md" or ".markdown");
                TextEditorControl.TextArea.TextView.Redraw();
            });
        }
        LinesCountText.Text = Loc.Plural("count.lines", TextEditorControl.Document.LineCount);
    }

    private bool _codeBlocksPending;

    public Task SaveToAsync(string path)
    {
        string text = TextEditorControl.Text;
        var encoding = _encoding;
        bool bom = _bom;
        return Task.Run(() => Lupik.Core.TextEncoding.Write(path, text, encoding, bom));
    }

    public void ReleaseFile() { }

    public Task ReloadAsync(string path)
    {
        TextEditorControl.Document.UndoStack.MarkAsOriginalFile();
        FileSizeText.Text = FormatFileSize(new FileInfo(path).Length);
        DirtyChanged?.Invoke();
        return Task.CompletedTask;
    }

    public bool HandleEditKey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods) => false; // the editor has them

    // --- Search (Ctrl+F) ---

    private readonly SearchHighlighter _search;

    public System.Threading.Tasks.Task<int> SearchAsync(string query, System.Threading.CancellationToken token) =>
        System.Threading.Tasks.Task.FromResult(_search.Find(query));

    public void ShowMatch(int index) => _search.Show(index);

    public void ClearSearch() => _search.Clear();

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
            var (content, size, truncated, encoding, bom) = await Task.Run(() =>
            {
                // FileShare.ReadWrite so locked files can still be previewed
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var (encoding, bom) = Lupik.Core.TextEncoding.Detect(stream);
                using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[MaxPreviewChars];
                int read = reader.ReadBlock(buffer, 0, buffer.Length);
                bool more = reader.Peek() >= 0;
                return (new string(buffer, 0, read), stream.Length, more, encoding, bom);
            });

            if (token != _loadToken) return false;
            _encoding = encoding;
            _bom = bom;
            _truncated = truncated;
            _readFailed = false;

            _currentFilePath = filePath;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            SetHighlightingForExtension(ext);

            _search.Clear();
            TextEditorControl.Text = content;
            TextEditorControl.Document.UndoStack.ClearAll(); // a fresh file: nothing to undo, nothing changed
            TextEditorControl.ScrollToHome();
            _codeBlocks.Analyze(TextEditorControl.Document, ext is ".md" or ".markdown");
            TextEditorControl.TextArea.TextView.Redraw();

            FileSizeText.Text = FormatFileSize(size);
            LinesCountText.Text = truncated
                ? Loc.T("code.truncated", TextEditorControl.Document.LineCount)
                : Loc.Plural("count.lines", TextEditorControl.Document.LineCount);
            return true;
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return false;
            _readFailed = true;
            TextEditorControl.SyntaxHighlighting = null;
            TextEditorControl.Text = Loc.T("code.readError", ex.Message);
            _codeBlocks.Analyze(TextEditorControl.Document, false);
            LinesCountText.Text = Loc.T("common.error");
            LanguageText.Text = "!";
            FileSizeText.Text = "—";
            return true;
        }
    }

    private void SetHighlightingForExtension(string ext)
    {
        string langName = ext.TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrEmpty(langName)) langName = "TXT";

        var definition = HighlightingFor(ext);
        CodeTheme.Apply(definition);
        TextEditorControl.SyntaxHighlighting = definition;
        LanguageText.Text = langName;
    }

    /// <summary>Syntax colors for a file type (also used by the diff view).</summary>
    internal static IHighlightingDefinition? HighlightingFor(string ext)
    {
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
        return definition;
    }

    /// <summary>Drops the loaded text (called when Lupik goes idle).</summary>
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
            CopyText.Text = Loc.T("common.copied");
            CopyButton.Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1));

            await Task.Delay(1800);

            CopyIcon.Kind = "copy";
            CopyText.Text = Loc.T("common.copy");
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
