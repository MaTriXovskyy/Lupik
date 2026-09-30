using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace Lupik.Core.Office;

public sealed record XlsxSheet(string Name, List<string[]> Rows, int Columns, bool Truncated);

/// <summary>
/// Reads .xlsx (Office Open XML) straight from the zip, streaming each sheet with an XmlReader: shared strings,
/// numbers, dates (from the cell's number format), booleans and the cached results of formulas.
/// Values are shown the way Excel shows them in the "General" format; exact custom formats are not reproduced.
/// </summary>
public static class XlsxReader
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static readonly string[] Extensions = { ".xlsx", ".xlsm", ".xltx", ".xltm" };

    /// <summary>The sheet names, in workbook order (cheap: no cell data).</summary>
    public static List<string> SheetNames(string path)
    {
        using var zip = Open(path, out var stream);
        using (stream) return ReadSheetList(zip).Select(s => s.Name).ToList();
    }

    /// <summary>One sheet, up to <paramref name="maxRows"/> rows.</summary>
    public static XlsxSheet ReadSheet(string path, int index, int maxRows, CultureInfo culture)
    {
        using var zip = Open(path, out var stream);
        using (stream)
        {
            var sheets = ReadSheetList(zip);
            if (sheets.Count == 0) return new XlsxSheet("", new List<string[]>(), 0, false);
            var (name, entryName) = sheets[Math.Clamp(index, 0, sheets.Count - 1)];
            var shared = ReadSharedStrings(zip);
            var dateStyles = ReadDateStyles(zip, out var percentStyles);
            var entry = zip.GetEntry(entryName) ?? throw new InvalidDataException($"{entryName} is missing");
            using var sheetStream = entry.Open();
            return ReadCells(name, sheetStream, shared, dateStyles, percentStyles, maxRows, culture);
        }
    }

    private static ZipArchive Open(string path, out Stream stream)
    {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static List<(string Name, string Entry)> ReadSheetList(ZipArchive zip)
    {
        var rels = new Dictionary<string, string>();
        var relEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (relEntry != null)
        {
            using var s = relEntry.Open();
            using var reader = XmlReader.Create(s);
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" &&
                    reader.GetAttribute("Id") is string id && reader.GetAttribute("Target") is string target)
                    rels[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        }

        var list = new List<(string, string)>();
        var workbook = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("xl/workbook.xml is missing");
        using (var s = workbook.Open())
        using (var reader = XmlReader.Create(s))
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet") continue;
                string name = reader.GetAttribute("name") ?? "";
                string? relId = reader.GetAttribute("id", OfficeRel);
                if (relId != null && rels.TryGetValue(relId, out var target) && zip.GetEntry(target) != null)
                    list.Add((name, target));
            }
        }
        return list;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return list;
        using var s = entry.Open();
        using var reader = XmlReader.Create(s);
        var text = new StringBuilder();
        bool inPhonetic = false;
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t" && !inPhonetic)
            {
                text.Append(reader.ReadElementContentAsString()); // leaves the reader on the next node already
                continue;
            }
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "si") text.Clear();
                else if (reader.LocalName == "rPh") inPhonetic = !reader.IsEmptyElement; // furigana: not part of the value
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.LocalName == "si") list.Add(text.ToString());
                else if (reader.LocalName == "rPh") inPhonetic = false;
            }
            reader.Read();
        }
        return list;
    }

    /// <summary>Which cell styles (index into cellXfs) are dates, and which are percentages.</summary>
    private static HashSet<int> ReadDateStyles(ZipArchive zip, out HashSet<int> percentStyles)
    {
        var dates = new HashSet<int>();
        percentStyles = new HashSet<int>();
        var entry = zip.GetEntry("xl/styles.xml");
        if (entry == null) return dates;

        var customDate = new HashSet<int>();
        var customPercent = new HashSet<int>();
        using var s = entry.Open();
        using var reader = XmlReader.Create(s);
        bool inCellXfs = false;
        int xfIndex = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "numFmt" && int.TryParse(reader.GetAttribute("numFmtId"), out int id))
                {
                    string code = reader.GetAttribute("formatCode") ?? "";
                    if (IsDateFormat(code)) customDate.Add(id);
                    else if (code.Contains('%')) customPercent.Add(id);
                }
                else if (reader.LocalName == "cellXfs") inCellXfs = !reader.IsEmptyElement;
                else if (inCellXfs && reader.LocalName == "xf")
                {
                    int fmt = int.TryParse(reader.GetAttribute("numFmtId"), out int f) ? f : 0;
                    if (fmt is (>= 14 and <= 22) or (>= 45 and <= 47) || customDate.Contains(fmt)) dates.Add(xfIndex);
                    else if (fmt is 9 or 10 || customPercent.Contains(fmt)) percentStyles.Add(xfIndex);
                    xfIndex++;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs") inCellXfs = false;
        }
        return dates;
    }

    /// <summary>A custom number format is a date if it has d/m/y/h/s outside quotes and [color] brackets.</summary>
    private static bool IsDateFormat(string code)
    {
        bool quoted = false, bracket = false;
        foreach (char c in code)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c == '[') bracket = true;
            else if (!quoted && c == ']') bracket = false;
            else if (!quoted && !bracket && "dmyhsDMYHS".IndexOf(c) >= 0) return true;
        }
        return false;
    }

    private static XlsxSheet ReadCells(string name, Stream sheet, List<string> shared, HashSet<int> dateStyles,
        HashSet<int> percentStyles, int maxRows, CultureInfo culture)
    {
        var rows = new List<string[]>();
        int columns = 0;
        bool truncated = false;
        var current = new List<string>();
        int currentRow = -1;

        using var reader = XmlReader.Create(sheet, new XmlReaderSettings { IgnoreWhitespace = true });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName == "row")
            {
                int rowNumber = int.TryParse(reader.GetAttribute("r"), out int rn) ? rn - 1 : currentRow + 1;
                if (currentRow >= 0) Flush();
                if (rowNumber >= maxRows) { truncated = true; break; }
                // Empty rows in between (Excel skips them in the file)
                while (rows.Count < rowNumber) rows.Add(Array.Empty<string>());
                currentRow = rowNumber;
            }
            else if (reader.LocalName == "c")
            {
                string? reference = reader.GetAttribute("r");
                string? type = reader.GetAttribute("t");
                int style = int.TryParse(reader.GetAttribute("s"), out int st) ? st : 0;
                int col = reference != null ? ColumnIndex(reference) : current.Count;
                string value = ReadCellValue(reader, type, style, shared, dateStyles, percentStyles, culture);
                while (current.Count <= col) current.Add("");
                current[col] = value;
            }
        }
        if (currentRow >= 0 && !truncated) Flush();

        // Trailing empty rows and columns aren't worth showing
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);
        columns = rows.Count == 0 ? 0 : rows.Max(r => LastFilled(r) + 1);
        return new XlsxSheet(name, rows, columns, truncated);

        void Flush()
        {
            rows.Add(current.ToArray());
            current.Clear();
        }
    }

    private static int LastFilled(string[] row)
    {
        for (int i = row.Length - 1; i >= 0; i--) if (!string.IsNullOrEmpty(row[i])) return i;
        return -1;
    }

    private static string ReadCellValue(XmlReader reader, string? type, int style, List<string> shared,
        HashSet<int> dateStyles, HashSet<int> percentStyles, CultureInfo culture)
    {
        if (reader.IsEmptyElement) return "";
        string? raw = null;
        var inline = new StringBuilder();
        int depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && reader.Depth > depth) // ends on </c>
        {
            // ReadElementContentAsString moves past the element by itself: no extra Read after it
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "v") raw = reader.ReadElementContentAsString();
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t") inline.Append(reader.ReadElementContentAsString());
            else reader.Read();
        }

        switch (type)
        {
            case "s":
                return int.TryParse(raw, out int i) && i >= 0 && i < shared.Count ? shared[i] : "";
            case "inlineStr":
                return inline.ToString();
            case "str":
            case "e":
                return raw ?? "";
            case "b":
                return raw == "1" ? "TRUE" : "FALSE";
            case "d":
                return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso) ? FormatDate(iso, culture) : raw ?? "";
        }
        if (raw == null) return "";
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return raw;

        if (dateStyles.Contains(style) && number > -657435 && number < 2958466)
        {
            try { return FormatDate(DateTime.FromOADate(number), culture); }
            catch { return raw; }
        }
        if (percentStyles.Contains(style)) return (number * 100).ToString("0.##", culture) + " %";
        return FormatNumber(number, culture);
    }

    private static string FormatDate(DateTime value, CultureInfo culture) =>
        value.TimeOfDay == TimeSpan.Zero ? value.ToString("d", culture)
        : value.Date == new DateTime(1899, 12, 30) || value.Date == new DateTime(1899, 12, 31) ? value.ToString("t", culture)
        : value.ToString("g", culture);

    /// <summary>Like Excel's "General": up to 11 significant digits, no trailing zeros, float noise rounded away.</summary>
    private static string FormatNumber(double number, CultureInfo culture)
    {
        if (number == Math.Floor(number) && Math.Abs(number) < 1e15) return number.ToString("0", culture);
        double rounded = double.Parse(number.ToString("G11", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return rounded.ToString(Math.Abs(rounded) >= 1e11 || Math.Abs(rounded) < 1e-9 ? "G11" : "0.##########", culture);
    }

    /// <summary>"BC12" -> 54 (zero-based column).</summary>
    private static int ColumnIndex(string reference)
    {
        int col = 0;
        foreach (char c in reference)
        {
            if (c is >= 'A' and <= 'Z') col = col * 26 + (c - 'A' + 1);
            else if (c is >= 'a' and <= 'z') col = col * 26 + (c - 'a' + 1);
            else break;
        }
        return Math.Max(0, col - 1);
    }

    /// <summary>0 -> "A", 27 -> "AB".</summary>
    public static string ColumnName(int index)
    {
        string s = "";
        index++;
        while (index > 0) { index--; s = (char)('A' + index % 26) + s; index /= 26; }
        return s;
    }
}
