using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

using Lupik.Localization;
namespace Lupik.Views;

/// <summary>
/// Shows .csv / .tsv files as a table (delimiter detected automatically), and Excel workbooks (.xlsx), one sheet
/// at a time with a tab per sheet. No Excel needed.
/// </summary>
public partial class CsvViewer : UserControl, ISearchable, IEditable
{
    public static readonly string[] Extensions = { ".csv", ".tsv" };

    public static bool IsWorkbook(string ext) => Array.IndexOf(Lupik.Core.Office.XlsxReader.Extensions, ext) >= 0;

    private const int MaxRows = 20_000; // enough to skim; bigger files are cut off with a note
    private int _loadToken;

    public CsvViewer()
    {
        InitializeComponent();
        Table.SelectionChanged += UpdateSummary;
        Table.EditCellRequested += StartCellEdit;
    }

    private string _summary = "";

    private void UpdateSummary()
    {
        int cells = Table.SelectedCellCount;
        SummaryText.Text = cells > 1 ? $"{_summary}  •  " + Loc.T("csv.selected", Loc.Plural("count.cells", cells)) : _summary;
    }

    /// <summary>Drops the loaded rows (called when Lupik goes idle).</summary>
    public void Release()
    {
        _loadToken++;
        Table.SetData(Array.Empty<string>(), Array.Empty<string[]>());
    }

    public bool HasSelection => Table.HasSelection;

    public void SelectAll() => Table.SelectAll();

    public void ClearSelection() => Table.ClearSelection();

    /// <summary>Copies the selected cells (tab-separated, pastes as cells in Excel).</summary>
    public async void CopySelection()
    {
        string? text = Table.GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            int cells = Table.SelectedCellCount;
            SummaryText.Text = $"{_summary}  •  " + Loc.T("csv.copied", Loc.Plural("count.cells", cells));
            await Task.Delay(1800);
            UpdateSummary();
        }
        catch (Exception ex)
        {
            App.Log($"[CsvViewer] Copy failed: {ex.Message}");
        }
    }

    public async Task<bool> LoadFileAsync(string filePath)
    {
        int token = ++_loadToken;
        BadgeText.Text = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        SheetTabs.Children.Clear();
        try
        {
            var file = await Task.Run(() => Parse(filePath));
            if (token != _loadToken) return false;
            var (rows, delimiter, truncated) = (file.Rows, file.Delimiter, file.Truncated);
            _file = file;
            _isWorkbook = false;

            int columns = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
            string[] firstRow = rows.Count > 0 ? rows[0] : Array.Empty<string>();
            _firstRow = Enumerable.Range(0, columns).Select(i => i < firstRow.Length ? firstRow[i] : "").ToArray();
            var header = Enumerable.Range(0, columns)
                .Select(i => i < firstRow.Length && firstRow[i].Length > 0 ? firstRow[i] : Loc.T("csv.column", i + 1))
                .ToArray();
            var body = rows.Skip(1).ToList();
            Table.SetData(header, body);

            string delimiterName = Loc.T(delimiter switch { '\t' => "csv.tab", ';' => "csv.semicolon", _ => "csv.comma" });
            _summary = $"{Loc.Plural("count.rows", body.Count)}  •  {Loc.Plural("count.columns", columns)}  •  {Loc.T("csv.separator", delimiterName)}" +
                               (truncated ? "  •  " + Loc.T("csv.truncated", MaxRows) : "");
            UpdateSummary();
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return false;
            _file = null;
            Table.SetData(Array.Empty<string>(), Array.Empty<string[]>());
            _summary = Loc.T("common.readError", ex.Message);
            UpdateSummary();
        }
        return true;
    }

    // ---------- Excel workbooks ----------

    private string _workbookPath = "";

    /// <summary>
    /// Opens a workbook on its first sheet. False only if it couldn't be read (the caller falls back to Windows'
    /// previewer); a load superseded by a newer one returns true.
    /// </summary>
    public async Task<bool> LoadWorkbookAsync(string filePath)
    {
        int token = ++_loadToken;
        try
        {
            var names = await Task.Run(() => Lupik.Core.Office.XlsxReader.SheetNames(filePath));
            if (token != _loadToken) return true;
            _workbookPath = filePath;
            _isWorkbook = true;
            BadgeText.Text = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
            BuildSheetTabs(names);
            await ShowSheetAsync(0, token);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[CsvViewer] Could not read workbook '{filePath}': {ex.Message}");
            return false;
        }
    }

    private void BuildSheetTabs(List<string> names)
    {
        SheetTabs.Children.Clear();
        if (names.Count < 2) return; // one sheet: nothing to switch between
        for (int i = 0; i < names.Count; i++)
        {
            int index = i;
            var tab = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = names[i],
                Style = (Style)FindResource("SheetTab"),
                IsChecked = i == 0,
            };
            tab.Click += async (_, _) =>
            {
                foreach (var t in SheetTabs.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>()) t.IsChecked = t == tab;
                await ShowSheetAsync(index, ++_loadToken);
                SheetChanged?.Invoke();
            };
            SheetTabs.Children.Add(tab);
        }
    }

    /// <summary>Another sheet is shown (a running search is redone on it).</summary>
    public event Action? SheetChanged;

    private async Task<bool> ShowSheetAsync(int index, int token)
    {
        string path = _workbookPath;
        var culture = Loc.Instance.Culture;
        var sheet = await Task.Run(() => Lupik.Core.Office.XlsxReader.ReadSheet(path, index, MaxRows, culture));
        if (token != _loadToken) return false;

        // Excel-style column letters: the first row is data like any other
        var header = Enumerable.Range(0, sheet.Columns).Select(Lupik.Core.Office.XlsxReader.ColumnName).ToArray();
        Table.SetData(header, sheet.Rows.Select(r => r.Length > sheet.Columns ? r[..sheet.Columns] : r).ToList());
        _summary = (sheet.Name.Length > 0 && SheetTabs.Children.Count == 0 ? sheet.Name + "  •  " : "") +
                   $"{Loc.Plural("count.rows", sheet.Rows.Count)}  •  {Loc.Plural("count.columns", sheet.Columns)}" +
                   (sheet.Truncated ? "  •  " + Loc.T("csv.truncated", MaxRows) : "");
        UpdateSummary();
        return true;
    }

    // ---------- Edit mode (E): cells, CSV only ----------

    private CsvFile? _file;
    private bool _isWorkbook, _dirty, _editing;
    private string[] _firstRow = Array.Empty<string>(); // the header as in the file (the table shows "Column N" for empty ones)
    private TextBox? _cellEditor;
    private (int Row, int Col) _cellEditing;

    public event Action? DirtyChanged;
    public bool IsDirty => _dirty;

    public string? WhyNotEditable() =>
        _isWorkbook ? Loc.T("edit.noExcel")
        : _file == null ? Loc.T("edit.cantRead")
        : _file.Truncated ? Loc.T("edit.tooBig")
        : null;

    public void BeginEdit()
    {
        _editing = true;
        _dirty = false;
        Table.Editable = true;
        if (Table.ActiveCell == null && Table.RowCount > 0) Table.SelectCell(0, 0);
        EditHint.Visibility = Visibility.Visible;
    }

    public void EndEdit()
    {
        CommitCellEdit(cancel: true);
        _editing = false;
        Table.Editable = false;
        EditHint.Visibility = Visibility.Collapsed;
        _dirty = false;
    }

    private void SetDirty()
    {
        if (_dirty) return;
        _dirty = true;
        DirtyChanged?.Invoke();
    }

    private void StartCellEdit(int row, int col, string? typed)
    {
        if (!_editing) return;
        CommitCellEdit(cancel: false);
        _cellEditing = (row, col);
        var rect = Table.CellRect(row, col);
        var topLeft = Table.TranslatePoint(rect.TopLeft, EditLayer);
        _cellEditor = new TextBox
        {
            Text = typed ?? (row < 0 ? _firstRow[col] : Table.GetCell(row, col)),
            Width = Math.Max(rect.Width, 120),
            MinHeight = rect.Height,
            FontSize = 12.5,
            Padding = new Thickness(6, 3, 6, 3),
            Background = new System.Windows.Media.SolidColorBrush(Core.Palette.Color(0x1F1C19)),
            Foreground = new System.Windows.Media.SolidColorBrush(Core.Palette.Color(0xFBF8F2)),
            CaretBrush = (System.Windows.Media.Brush)FindResource("Gold"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Gold"),
            BorderThickness = new Thickness(2),
            AcceptsReturn = false,
        };
        Canvas.SetLeft(_cellEditor, topLeft.X);
        Canvas.SetTop(_cellEditor, topLeft.Y);
        _cellEditor.LostKeyboardFocus += (_, _) => CommitCellEdit(cancel: false);
        EditLayer.Children.Add(_cellEditor);
        _cellEditor.Focus();
        _cellEditor.CaretIndex = _cellEditor.Text.Length;
        if (typed == null) _cellEditor.SelectAll();
    }

    private void CommitCellEdit(bool cancel)
    {
        var editor = _cellEditor;
        if (editor == null) return;
        _cellEditor = null;
        EditLayer.Children.Remove(editor);
        if (cancel) return;

        var (row, col) = _cellEditing;
        string old = row < 0 ? _firstRow[col] : Table.GetCell(row, col);
        if (editor.Text == old) return;
        if (row < 0)
        {
            _firstRow[col] = editor.Text;
            Table.SetCell(-1, col, editor.Text.Length > 0 ? editor.Text : Loc.T("csv.column", col + 1));
        }
        else Table.SetCell(row, col, editor.Text);
        SetDirty();
    }

    public bool HandleEditKey(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods)
    {
        if (_cellEditor != null)
        {
            // Typing in a cell: Enter/Tab commit and move on, Esc cancels; the rest is the text box's
            if (key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Tab)
            {
                var (row, col) = _cellEditing;
                CommitCellEdit(cancel: false);
                bool back = (mods & System.Windows.Input.ModifierKeys.Shift) != 0;
                if (key == System.Windows.Input.Key.Enter) Table.SelectCell(row < 0 ? 0 : row + (back ? -1 : 1), col);
                else Table.SelectCell(Math.Max(0, row), col + (back ? -1 : 1));
                Table.Focus();
                return true;
            }
            if (key == System.Windows.Input.Key.Escape) { CommitCellEdit(cancel: true); Table.Focus(); return true; }
            return false;
        }

        if (Table.ActiveCell is not var (r, c)) return false;
        switch (key)
        {
            case System.Windows.Input.Key.Up: Table.SelectCell(r - 1, c); return true;
            case System.Windows.Input.Key.Down: Table.SelectCell(r + 1, c); return true;
            case System.Windows.Input.Key.Left: Table.SelectCell(r, c - 1); return true;
            case System.Windows.Input.Key.Right: Table.SelectCell(r, c + 1); return true;
            case System.Windows.Input.Key.Tab: Table.SelectCell(r, c + ((mods & System.Windows.Input.ModifierKeys.Shift) != 0 ? -1 : 1)); return true;
            case System.Windows.Input.Key.Enter:
            case System.Windows.Input.Key.F2:
                StartCellEdit(r, c, null);
                return true;
            case System.Windows.Input.Key.Delete:
                foreach (var (row, col) in Table.SelectedCells().ToList())
                    if (Table.GetCell(row, col).Length > 0) { Table.SetCell(row, col, ""); SetDirty(); }
                return true;
        }
        return false;
    }

    public Task SaveToAsync(string path)
    {
        CommitCellEdit(cancel: false);
        var file = _file!;
        var lines = new List<string[]> { _firstRow };
        for (int r = 0; r < Table.RowCount; r++)
        {
            var row = new string[Table.ColumnCount];
            for (int c = 0; c < row.Length; c++) row[c] = Table.GetCell(r, c);
            // Rows keep their own length (no trailing separators the file didn't have)
            int original = r + 1 < file.Rows.Count ? file.Rows[r + 1].Length : row.Length;
            int last = row.Length - 1;
            while (last >= original && row[last].Length == 0) last--;
            lines.Add(row[..(last + 1)]);
        }
        return Task.Run(() =>
        {
            var sb = new StringBuilder();
            foreach (var cells in lines)
            {
                for (int c = 0; c < cells.Length; c++)
                {
                    if (c > 0) sb.Append(file.Delimiter);
                    sb.Append(Quote(cells[c], file.Delimiter));
                }
                sb.Append(file.NewLine);
            }
            Lupik.Core.TextEncoding.Write(path, sb.ToString(), file.Encoding, file.Bom);
        });
    }

    /// <summary>Quotes a value only when it has to be (it holds the separator, a quote or a line break).</summary>
    private static string Quote(string value, char delimiter) =>
        value.IndexOf(delimiter) >= 0 || value.IndexOfAny(new[] { '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    public void ReleaseFile() { }

    public async Task ReloadAsync(string path)
    {
        var (row, col) = Table.ActiveCell ?? (0, 0);
        double offset = TableScroller.VerticalOffset, hOffset = TableScroller.HorizontalOffset;
        await LoadFileAsync(path);
        TableScroller.ScrollToVerticalOffset(offset);
        TableScroller.ScrollToHorizontalOffset(hOffset);
        _dirty = false;
        if (_editing) { Table.Editable = true; Table.SelectCell(row, col); }
        DirtyChanged?.Invoke();
    }

    // ---------- Search (Ctrl+F) ----------

    private List<(int Row, int Col)> _found = new();

    public Task<int> SearchAsync(string query, System.Threading.CancellationToken token)
    {
        _found = Table.FindCells(query);
        return Task.FromResult(_found.Count);
    }

    public void ShowMatch(int index)
    {
        if (index >= 0 && index < _found.Count) Table.ShowCell(_found[index].Row, _found[index].Col);
    }

    public void ClearSearch()
    {
        Table.FindCells("");
        _found.Clear();
    }

    /// <summary>A parsed CSV, with what's needed to write it back the same way.</summary>
    private sealed record CsvFile(List<string[]> Rows, char Delimiter, bool Truncated, Encoding Encoding, bool Bom, string NewLine);

    /// <summary>
    /// UTF-8 if the file is valid UTF-8; otherwise the Windows code page (Windows-1250 on Polish Windows,
    /// what Polish Excel uses for "CSV (rozdzielany przecinkami)").
    /// </summary>
    private static CsvFile Parse(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var (encoding, bom) = Lupik.Core.TextEncoding.Detect(stream);
        string newLine = DetectNewLine(stream);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);

        string? first = reader.ReadLine();
        if (first == null) return new CsvFile(new List<string[]>(), ',', false, encoding, bom, newLine);
        char delimiter = DetectDelimiter(first, Path.GetExtension(filePath));

        var rows = new List<string[]>();
        string? line = first;
        while (line != null)
        {
            if (rows.Count >= MaxRows + 1) return new CsvFile(rows, delimiter, true, encoding, bom, newLine);

            // A quoted value may span several lines: keep reading until quotes are balanced
            while (line.Count(c => c == '"') % 2 != 0 && reader.ReadLine() is { } next)
                line += "\n" + next;

            rows.Add(SplitLine(line, delimiter));
            line = reader.ReadLine();
        }
        return new CsvFile(rows, delimiter, false, encoding, bom, newLine);
    }

    /// <summary>"\r\n" (Windows, Excel) unless the file uses bare "\n". Leaves the stream at the start.</summary>
    private static string DetectNewLine(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        int read = stream.Read(buffer, 0, buffer.Length);
        stream.Position = 0;
        int lf = Array.IndexOf(buffer, (byte)'\n', 0, read);
        return lf > 0 && buffer[lf - 1] != '\r' ? "\n" : "\r\n";
    }

    private static char DetectDelimiter(string headerLine, string ext)
    {
        if (ext == ".tsv") return '\t';
        // Polish Excel exports use ';', most other tools use ','
        return new[] { ';', ',', '\t', '|' }.OrderByDescending(d => headerLine.Count(c => c == d)).First();
    }

    private static string[] SplitLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == delimiter) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
