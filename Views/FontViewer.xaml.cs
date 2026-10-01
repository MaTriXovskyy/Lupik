using System;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>
/// Fonts (.ttf, .otf, .ttc, .otc) drawn by Lupik: the alphabet big, a sample sentence at growing sizes and a grid of
/// every character the font has (click one to copy it).
/// </summary>
public partial class FontViewer : UserControl
{
    public static readonly string[] Extensions = { ".ttf", ".otf", ".ttc", ".otc" };
    private static readonly double[] SampleSizes = { 12, 16, 20, 28, 36, 48, 72 };
    private const int MaxGlyphs = 1500;

    /// <summary>A character was copied (for the notice in the title bar).</summary>
    public event Action<string>? CharacterCopied;

    private int _loadToken;

    public FontViewer()
    {
        InitializeComponent();
    }

    public static bool CanOpen(string ext) => Extensions.Contains(ext);

    /// <summary>Shows the font; false if WPF can't read it (Windows' own previewer gets a go then).</summary>
    public async Task<bool> LoadAsync(string path)
    {
        int token = ++_loadToken;
        try
        {
            // WPF keeps a font file open for as long as the process runs: read a copy, so the file can still be
            // renamed or deleted
            string copy = await Task.Run(() => TempCopy(path));
            if (token != _loadToken) return false;
            var family = Fonts.GetFontFamilies(copy).FirstOrDefault();
            var typeface = family?.GetTypefaces().FirstOrDefault();
            if (family == null || typeface == null || !typeface.TryGetGlyphTypeface(out var glyphs)) return false;

            FormatText.Text = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            FamilyText.Text = family.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path);
            FaceText.Text = typeface.FaceNames.Values.FirstOrDefault() ?? "";
            var chars = glyphs.CharacterToGlyphMap.Keys.Where(c => c > 32 && !(c >= 0x7F && c <= 0xA0)).OrderBy(c => c).ToList();
            GlyphCountText.Text = Loc.Plural("count.characters", chars.Count);

            Apply(AlphabetBig, family, typeface);
            Sizes.Children.Clear();
            foreach (double size in SampleSizes)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                var label = new TextBlock { Text = size.ToString(), FontSize = 11, Width = 34, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Math.Max(0, size * 0.25), 0, 0) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
                DockPanel.SetDock(label, Dock.Left);
                row.Children.Add(label);
                var sample = new TextBlock { Text = Loc.T("generic.fontSample"), FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
                sample.SetResourceReference(TextBlock.ForegroundProperty, "cDDD6CB");
                Apply(sample, family, typeface);
                row.Children.Add(sample);
                Sizes.Children.Add(row);
            }

            Glyphs.Children.Clear();
            foreach (int c in chars.Take(MaxGlyphs)) Glyphs.Children.Add(GlyphCell(c, family, typeface));
            GlyphsMore.Visibility = chars.Count > MaxGlyphs ? Visibility.Visible : Visibility.Collapsed;
            GlyphsMore.Text = Loc.T("font.more", chars.Count - MaxGlyphs);
            Scroller.ScrollToTop();
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[FontViewer] {path}: {ex.Message}");
            return false;
        }
    }

    public void ScrollBy(double delta) => Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + delta);

    private static void Apply(TextBlock text, FontFamily family, Typeface typeface)
    {
        text.FontFamily = family;
        text.FontStyle = typeface.Style;
        text.FontWeight = typeface.Weight;
        text.FontStretch = typeface.Stretch;
    }

    private FrameworkElement GlyphCell(int codePoint, FontFamily family, Typeface typeface)
    {
        string ch = char.ConvertFromUtf32(codePoint);
        var glyph = new TextBlock { Text = ch, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "cF6F1E9");
        Apply(glyph, family, typeface);
        var cell = new Border
        {
            Width = 46, Height = 46, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Child = glyph, Cursor = Cursors.Hand, ToolTip = $"U+{codePoint:X4}",
        };
        cell.SetResourceReference(Border.BackgroundProperty, "c1B1917");
        cell.SetResourceReference(Border.BorderBrushProperty, "c2A2621");
        cell.MouseEnter += (_, _) => cell.SetResourceReference(Border.BorderBrushProperty, "Gold");
        cell.MouseLeave += (_, _) => cell.SetResourceReference(Border.BorderBrushProperty, "c2A2621");
        cell.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                Clipboard.SetText(ch);
                CharacterCopied?.Invoke(Loc.T("font.copied", ch, $"U+{codePoint:X4}"));
            }
            catch (Exception ex) { App.Log($"[FontViewer] Clipboard: {ex.Message}"); }
        };
        return cell;
    }

    private static string TempCopy(string path)
    {
        string dir = Path.Combine(Path.GetTempPath(), "Lupik", "fonts");
        Directory.CreateDirectory(dir);
        var info = new FileInfo(path);
        // Same file, same copy: named by path, size and date
        string name = $"{XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(path.ToLowerInvariant())):X8}-{info.Length:X}-{info.LastWriteTimeUtc.Ticks:X}{info.Extension}";
        string copy = Path.Combine(dir, name);
        if (!File.Exists(copy)) File.Copy(path, copy, overwrite: true);
        return copy;
    }
}
