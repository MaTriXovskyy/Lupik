using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace Lupik.Core.Office;

// A Word document reduced to what a preview needs. Built on a background thread (plain data, frozen bitmaps);
// DocxViewer turns it into a FlowDocument on the UI thread.

public abstract record DocBlock;

public sealed record DocParagraph(List<DocRun> Runs, int HeadingLevel, System.Windows.TextAlignment Alignment,
    string? ListLabel, int IndentLevel, bool IsTitle) : DocBlock;

public sealed record DocTable(List<List<List<DocBlock>>> Rows) : DocBlock;

public sealed record DocRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, bool Strike = false,
    double? SizePt = null, Color? Color = null, Color? Highlight = null, bool LineBreak = false, bool PageBreak = false,
    BitmapSource? Image = null, double ImageWidth = 0, double ImageHeight = 0);

public sealed record DocxDocument(List<DocBlock> Blocks, double DefaultSizePt, string DefaultFont, int ImageCount);

/// <summary>
/// Reads .docx (Office Open XML) straight from the zip: paragraphs, headings, bold/italic/underline, colors, lists,
/// tables and pictures. Headers, footers, comments and exact page layout are left out: it's a preview, not Word.
/// </summary>
public static class DocxReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static readonly string[] Extensions = { ".docx", ".docm", ".dotx", ".dotm" };

    private const int MaxImages = 60;          // a picture-heavy document shouldn't take ages to open
    private const int MaxImagePixels = 1600;   // decoded no wider than this

    public static DocxDocument Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var context = new Context(zip);

        var document = context.Load("word/document.xml") ?? throw new InvalidDataException("word/document.xml is missing");
        context.ReadStyles();
        context.ReadNumbering();
        context.ReadRelationships();

        var body = document.Root?.Element(W + "body") ?? throw new InvalidDataException("The document has no body");
        var blocks = context.ReadBlocks(body);
        return new DocxDocument(blocks, context.DefaultSizePt, context.DefaultFont, context.ImageCount);
    }

    private sealed class Context(ZipArchive zip)
    {
        public double DefaultSizePt = 11;
        public string DefaultFont = "Calibri";
        public int ImageCount;

        private readonly Dictionary<string, StyleInfo> _styles = new();
        private readonly Dictionary<string, string> _relationships = new();
        // numId -> abstractNum levels (format + text per level)
        private readonly Dictionary<string, List<(string Format, string Text)>> _numbering = new();
        private readonly Dictionary<(string, int), int> _counters = new();

        private sealed record StyleInfo(string Name, string? BasedOn, bool? Bold, bool? Italic, double? SizePt, Color? Color, int OutlineLevel);

        public XDocument? Load(string name)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) return null;
            using var s = entry.Open();
            return XDocument.Load(s);
        }

        public void ReadStyles()
        {
            var styles = Load("word/styles.xml")?.Root;
            if (styles == null) return;

            var defaults = styles.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr");
            if (HalfPoints(defaults?.Element(W + "sz")) is double size) DefaultSizePt = size;
            if (defaults?.Element(W + "rFonts")?.Attribute(W + "ascii")?.Value is string font) DefaultFont = font;

            foreach (var style in styles.Elements(W + "style"))
            {
                string? id = style.Attribute(W + "styleId")?.Value;
                if (id == null) continue;
                var rPr = style.Element(W + "rPr");
                var pPr = style.Element(W + "pPr");
                int outline = int.TryParse(pPr?.Element(W + "outlineLvl")?.Attribute(W + "val")?.Value, out int o) ? o + 1 : 0;
                _styles[id] = new StyleInfo(
                    style.Element(W + "name")?.Attribute(W + "val")?.Value?.ToLowerInvariant() ?? "",
                    style.Element(W + "basedOn")?.Attribute(W + "val")?.Value,
                    Flag(rPr?.Element(W + "b")), Flag(rPr?.Element(W + "i")),
                    HalfPoints(rPr?.Element(W + "sz")), ColorOf(rPr?.Element(W + "color")), outline);
            }
        }

        public void ReadNumbering()
        {
            var root = Load("word/numbering.xml")?.Root;
            if (root == null) return;
            var abstracts = root.Elements(W + "abstractNum").ToDictionary(
                a => a.Attribute(W + "abstractNumId")?.Value ?? "",
                a => a.Elements(W + "lvl")
                    .OrderBy(l => int.TryParse(l.Attribute(W + "ilvl")?.Value, out int i) ? i : 0)
                    .Select(l => (l.Element(W + "numFmt")?.Attribute(W + "val")?.Value ?? "bullet",
                                  l.Element(W + "lvlText")?.Attribute(W + "val")?.Value ?? "•"))
                    .ToList());
            foreach (var num in root.Elements(W + "num"))
            {
                string? id = num.Attribute(W + "numId")?.Value;
                string? abstractId = num.Element(W + "abstractNumId")?.Attribute(W + "val")?.Value;
                if (id != null && abstractId != null && abstracts.TryGetValue(abstractId, out var levels)) _numbering[id] = levels;
            }
        }

        public void ReadRelationships()
        {
            var root = Load("word/_rels/document.xml.rels")?.Root;
            if (root == null) return;
            foreach (var rel in root.Elements(Rel + "Relationship"))
            {
                string? id = rel.Attribute("Id")?.Value, target = rel.Attribute("Target")?.Value;
                if (id != null && target != null) _relationships[id] = target;
            }
        }

        public List<DocBlock> ReadBlocks(XElement container)
        {
            var blocks = new List<DocBlock>();
            foreach (var element in container.Elements())
            {
                if (element.Name == W + "p") blocks.Add(ReadParagraph(element));
                else if (element.Name == W + "tbl") blocks.Add(ReadTable(element));
                else if (element.Name == W + "sdt") blocks.AddRange(ReadBlocks(element.Element(W + "sdtContent") ?? element));
            }
            return blocks;
        }

        private DocTable ReadTable(XElement table)
        {
            var rows = new List<List<List<DocBlock>>>();
            foreach (var tr in table.Elements(W + "tr"))
            {
                var cells = new List<List<DocBlock>>();
                foreach (var tc in tr.Elements(W + "tc")) cells.Add(ReadBlocks(tc));
                rows.Add(cells);
            }
            return new DocTable(rows);
        }

        private DocParagraph ReadParagraph(XElement p)
        {
            var pPr = p.Element(W + "pPr");
            string? styleId = pPr?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
            var style = Resolve(styleId);

            int heading = 0;
            bool title = false;
            if (style.Name.StartsWith("heading ") && int.TryParse(style.Name.AsSpan(8), out int h)) heading = h;
            else if (style.Name == "title") title = true;
            else if (int.TryParse(pPr?.Element(W + "outlineLvl")?.Attribute(W + "val")?.Value, out int ol) && ol < 9) heading = ol + 1;
            else if (style.OutlineLevel is > 0 and < 10) heading = style.OutlineLevel;

            var alignment = (pPr?.Element(W + "jc")?.Attribute(W + "val")?.Value) switch
            {
                "center" => System.Windows.TextAlignment.Center,
                "right" or "end" => System.Windows.TextAlignment.Right,
                "both" or "distribute" => System.Windows.TextAlignment.Justify,
                _ => System.Windows.TextAlignment.Left,
            };

            string? label = null;
            int indent = 0;
            var numPr = pPr?.Element(W + "numPr");
            if (numPr != null)
            {
                string? numId = numPr.Element(W + "numId")?.Attribute(W + "val")?.Value;
                indent = int.TryParse(numPr.Element(W + "ilvl")?.Attribute(W + "val")?.Value, out int lvl) ? lvl : 0;
                if (numId != null && numId != "0") label = ListLabel(numId, indent);
            }

            var runs = new List<DocRun>();
            ReadRuns(p, runs, style, hyperlink: false);
            return new DocParagraph(runs, heading, alignment, label, indent, title);
        }

        private void ReadRuns(XElement parent, List<DocRun> runs, StyleInfo paragraphStyle, bool hyperlink)
        {
            foreach (var node in parent.Elements())
            {
                if (node.Name == W + "r") ReadRun(node, runs, paragraphStyle, hyperlink);
                else if (node.Name == W + "hyperlink") ReadRuns(node, runs, paragraphStyle, hyperlink: true);
                else if (node.Name == W + "ins" || node.Name == W + "smartTag" || node.Name == W + "fldSimple" ||
                         node.Name == W + "customXml") ReadRuns(node, runs, paragraphStyle, hyperlink);
                else if (node.Name == W + "sdt") ReadRuns(node.Element(W + "sdtContent") ?? node, runs, paragraphStyle, hyperlink);
                // w:del (deleted text in tracked changes) is skipped on purpose
            }
        }

        private void ReadRun(XElement r, List<DocRun> runs, StyleInfo paragraphStyle, bool hyperlink)
        {
            var rPr = r.Element(W + "rPr");
            var charStyle = Resolve(rPr?.Element(W + "rStyle")?.Attribute(W + "val")?.Value);
            bool bold = Flag(rPr?.Element(W + "b")) ?? charStyle.Bold ?? paragraphStyle.Bold ?? false;
            bool italic = Flag(rPr?.Element(W + "i")) ?? charStyle.Italic ?? paragraphStyle.Italic ?? false;
            string? u = rPr?.Element(W + "u")?.Attribute(W + "val")?.Value;
            bool underline = hyperlink || (rPr?.Element(W + "u") != null && u != "none");
            bool strike = Flag(rPr?.Element(W + "strike")) ?? false;
            double? size = HalfPoints(rPr?.Element(W + "sz")) ?? charStyle.SizePt ?? paragraphStyle.SizePt;
            Color? color = ColorOf(rPr?.Element(W + "color")) ?? charStyle.Color ?? paragraphStyle.Color;
            if (hyperlink && color == null) color = Color.FromRgb(0x05, 0x63, 0xC1);
            Color? highlight = HighlightOf(rPr);
            bool caps = Flag(rPr?.Element(W + "caps")) ?? false;
            bool hidden = Flag(rPr?.Element(W + "vanish")) ?? false;
            if (hidden) return;

            DocRun Make(string text) => new(caps ? text.ToUpper(CultureInfo.CurrentCulture) : text,
                bold, italic, underline, strike, size, color, highlight);

            foreach (var child in r.Elements())
            {
                if (child.Name == W + "t") runs.Add(Make(child.Value));
                else if (child.Name == W + "tab") runs.Add(Make("\t"));
                else if (child.Name == W + "noBreakHyphen") runs.Add(Make("‑"));
                else if (child.Name == W + "sym" && child.Attribute(W + "char")?.Value is string code &&
                         int.TryParse(code, NumberStyles.HexNumber, null, out int ch)) runs.Add(Make(((char)(ch >= 0xF000 ? ch - 0xF000 : ch)).ToString()));
                else if (child.Name == W + "br")
                    runs.Add(new DocRun("", LineBreak: child.Attribute(W + "type")?.Value != "page", PageBreak: child.Attribute(W + "type")?.Value == "page"));
                else if (child.Name == W + "cr") runs.Add(new DocRun("", LineBreak: true));
                else if (child.Name == W + "drawing" || child.Name == W + "pict" || child.Name == W + "object")
                {
                    if (ReadPicture(child) is DocRun picture) runs.Add(picture);
                }
            }
        }

        private DocRun? ReadPicture(XElement drawing)
        {
            // DrawingML: a:blip r:embed + wp:extent (EMU); VML (old documents): v:imagedata r:id + style width/height
            string? relId = drawing.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed")?.Value
                            ?? drawing.Descendants(V + "imagedata").FirstOrDefault()?.Attribute(R + "id")?.Value;
            if (relId == null || !_relationships.TryGetValue(relId, out var target)) return null;
            if (ImageCount >= MaxImages) return null;

            var extent = drawing.Descendants(Wp + "extent").FirstOrDefault();
            double width = 0, height = 0;
            if (extent != null && long.TryParse(extent.Attribute("cx")?.Value, out long cx) && long.TryParse(extent.Attribute("cy")?.Value, out long cy))
            {
                width = cx / 9525.0; // EMU -> px at 96 DPI
                height = cy / 9525.0;
            }

            string entryName = target.StartsWith('/') ? target.TrimStart('/') : "word/" + target;
            entryName = NormalizePath(entryName);
            var entry = zip.GetEntry(entryName);
            if (entry == null) return null;
            try
            {
                using var raw = entry.Open();
                using var buffer = new MemoryStream();
                raw.CopyTo(buffer);
                buffer.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = buffer;
                bitmap.DecodePixelWidth = (int)Math.Min(MaxImagePixels, Math.Max(1, width > 0 ? width * 1.5 : MaxImagePixels));
                bitmap.EndInit();
                bitmap.Freeze();
                if (width <= 0 || height <= 0) { width = bitmap.PixelWidth; height = bitmap.PixelHeight; }
                ImageCount++;
                return new DocRun("", Image: bitmap, ImageWidth: width, ImageHeight: height);
            }
            catch (Exception ex)
            {
                App.Log($"[DocxReader] Picture '{entryName}' skipped: {ex.Message}");
                return null; // EMF/WMF and other formats WPF can't decode
            }
        }

        private string ListLabel(string numId, int level)
        {
            if (!_numbering.TryGetValue(numId, out var levels) || levels.Count == 0) return "•";
            var (format, text) = levels[Math.Min(level, levels.Count - 1)];
            if (format == "bullet") return BulletOf(text);
            if (format == "none") return "";

            int n = _counters.TryGetValue((numId, level), out int c) ? c + 1 : 1;
            _counters[(numId, level)] = n;
            // A new item on a level restarts the deeper levels
            foreach (var key in _counters.Keys.Where(k => k.Item1 == numId && k.Item2 > level).ToList()) _counters.Remove(key);

            // "%1.%2." style labels: the current level's number, the parents' current numbers
            string label = text;
            for (int l = 0; l <= level; l++)
            {
                int value = l == level ? n : (_counters.TryGetValue((numId, l), out int pv) ? pv : 1);
                string fmt = l < levels.Count ? levels[l].Format : "decimal";
                label = label.Replace("%" + (l + 1), FormatNumber(value, fmt));
            }
            return label;
        }

        private static string BulletOf(string text) => text switch
        {
            "" or "" or "" => "•",
            "" or "" => "▪",
            "o" => "◦",
            _ => text.Length > 0 && text[0] >= 0xF000 ? "•" : text,
        };

        private static string FormatNumber(int n, string format) => format switch
        {
            "lowerLetter" => Letters(n).ToLowerInvariant(),
            "upperLetter" => Letters(n),
            "lowerRoman" => Roman(n).ToLowerInvariant(),
            "upperRoman" => Roman(n),
            _ => n.ToString(CultureInfo.InvariantCulture),
        };

        private static string Letters(int n)
        {
            string s = "";
            while (n > 0) { n--; s = (char)('A' + n % 26) + s; n /= 26; }
            return s;
        }

        private static string Roman(int n)
        {
            var map = new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
            string s = "";
            foreach (var (v, t) in map) while (n >= v) { s += t; n -= v; }
            return s;
        }

        /// <summary>A style with everything it inherits (basedOn chain) filled in.</summary>
        private StyleInfo Resolve(string? id)
        {
            var empty = new StyleInfo("", null, null, null, null, null, 0);
            if (id == null || !_styles.TryGetValue(id, out var style)) return empty;
            var result = style;
            var seen = new HashSet<string> { id };
            while (result.BasedOn is string parent && seen.Add(parent) && _styles.TryGetValue(parent, out var p))
            {
                result = result with
                {
                    BasedOn = p.BasedOn,
                    Bold = result.Bold ?? p.Bold,
                    Italic = result.Italic ?? p.Italic,
                    SizePt = result.SizePt ?? p.SizePt,
                    Color = result.Color ?? p.Color,
                    OutlineLevel = result.OutlineLevel > 0 ? result.OutlineLevel : p.OutlineLevel,
                };
            }
            return result with { Name = style.Name };
        }
    }

    private static string NormalizePath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part != "." && part.Length > 0) parts.Add(part);
        }
        return string.Join('/', parts);
    }

    /// <summary>On/off properties: present = on, unless w:val says 0/false/off.</summary>
    private static bool? Flag(XElement? e)
    {
        if (e == null) return null;
        string? v = e.Attribute(W + "val")?.Value;
        return v is null or "1" or "true" or "on";
    }

    private static double? HalfPoints(XElement? e) =>
        double.TryParse(e?.Attribute(W + "val")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double half) ? half / 2 : null;

    private static Color? ColorOf(XElement? e)
    {
        string? v = e?.Attribute(W + "val")?.Value;
        if (v == null || v == "auto" || v.Length != 6) return null;
        try { return (Color)ColorConverter.ConvertFromString("#" + v); }
        catch { return null; }
    }

    private static Color? HighlightOf(XElement? rPr)
    {
        string? name = rPr?.Element(W + "highlight")?.Attribute(W + "val")?.Value;
        if (name != null && name != "none")
        {
            return name switch
            {
                "yellow" => Colors.Yellow, "green" => Color.FromRgb(0x00, 0xFF, 0x00), "cyan" => Colors.Cyan,
                "magenta" => Colors.Magenta, "blue" => Colors.Blue, "red" => Colors.Red, "darkBlue" => Colors.DarkBlue,
                "darkCyan" => Colors.DarkCyan, "darkGreen" => Colors.DarkGreen, "darkMagenta" => Colors.DarkMagenta,
                "darkRed" => Colors.DarkRed, "darkYellow" => Color.FromRgb(0x80, 0x80, 0x00), "darkGray" => Colors.DarkGray,
                "lightGray" => Colors.LightGray, "black" => Colors.Black, "white" => Colors.White,
                _ => Colors.Yellow,
            };
        }
        string? fill = rPr?.Element(W + "shd")?.Attribute(W + "fill")?.Value;
        if (fill != null && fill.Length == 6 && fill != "auto" && fill != "FFFFFF")
        {
            try { return (Color)ColorConverter.ConvertFromString("#" + fill); } catch { }
        }
        return null;
    }
}
