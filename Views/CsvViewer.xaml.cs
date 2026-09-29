using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Lupik.Views;

/// <summary>Shows .csv / .tsv files as a table (delimiter detected automatically).</summary>
public partial class CsvViewer : UserControl
{
    public static readonly string[] Extensions = { ".csv", ".tsv" };

    private const int MaxRows = 20_000; // enough to skim; bigger files are cut off with a note
    private int _loadToken;

    public CsvViewer()
    {
        InitializeComponent();
        Table.SelectionChanged += UpdateSummary;
    }

    private string _summary = "";

    private void UpdateSummary()
    {
        int cells = Table.SelectedCellCount;
        SummaryText.Text = cells > 1 ? $"{_summary}  •  zaznaczono {cells} komórek (Ctrl+C kopiuje)" : _summary;
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
            SummaryText.Text = $"{_summary}  •  skopiowano {cells} {(cells == 1 ? "komórkę" : "komórek")}";
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
        try
        {
            var (rows, delimiter, truncated) = await Task.Run(() => Parse(filePath));
            if (token != _loadToken) return false;

            int columns = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
            string[] firstRow = rows.Count > 0 ? rows[0] : Array.Empty<string>();
            var header = Enumerable.Range(0, columns)
                .Select(i => i < firstRow.Length && firstRow[i].Length > 0 ? firstRow[i] : $"Kolumna {i + 1}")
                .ToArray();
            var body = rows.Skip(1).ToList();
            Table.SetData(header, body);

            string delimiterName = delimiter switch { '\t' => "tabulator", ';' => "średnik", _ => "przecinek" };
            _summary = $"{body.Count} wierszy  •  {columns} kolumn  •  separator: {delimiterName}" +
                               (truncated ? $"  •  pokazano pierwsze {MaxRows} wierszy" : "");
            UpdateSummary();
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return false;
            Table.SetData(Array.Empty<string>(), Array.Empty<string[]>());
            _summary = $"Nie udało się odczytać pliku: {ex.Message}";
            UpdateSummary();
        }
        return true;
    }

    private static (List<string[]>, char, bool) Parse(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, DetectEncoding(stream), detectEncodingFromByteOrderMarks: true);

        string? first = reader.ReadLine();
        if (first == null) return (new List<string[]>(), ',', false);
        char delimiter = DetectDelimiter(first, Path.GetExtension(filePath));

        var rows = new List<string[]>();
        string? line = first;
        while (line != null)
        {
            if (rows.Count >= MaxRows + 1) return (rows, delimiter, true);

            // A quoted value may span several lines: keep reading until quotes are balanced
            while (line.Count(c => c == '"') % 2 != 0 && reader.ReadLine() is { } next)
                line += "\n" + next;

            rows.Add(SplitLine(line, delimiter));
            line = reader.ReadLine();
        }
        return (rows, delimiter, false);
    }

    /// <summary>
    /// UTF-8 if the start of the file is valid UTF-8; otherwise Windows-1250,
    /// which is what Polish Excel uses for "CSV (rozdzielany przecinkami)".
    /// </summary>
    private static Encoding DetectEncoding(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        int read = stream.Read(buffer, 0, buffer.Length);
        stream.Position = 0;

        // Don't judge a multi-byte character cut off at the end of the sample
        int end = read;
        while (end > 0 && end > read - 4 && (buffer[end - 1] & 0xC0) == 0x80) end--;
        if (end > 0 && buffer[end - 1] >= 0xC0) end--;

        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, end);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1250);
        }
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
