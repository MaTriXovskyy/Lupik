using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>
/// The card for files Lupik can't show: name, type, size, dates, plus whatever can be told without opening the file
/// (where it was downloaded from, its signature, what program it is, a font's sample, where a shortcut leads).
/// The quick part shows at once; the slower checks (signature, MSI) fill in when ready.
/// </summary>
public partial class GenericViewer : UserControl
{
    private string _currentFilePath = "";
    private string? _shortcutTarget;
    private int _token;

    private static readonly string[] FontExtensions = { ".ttf", ".otf", ".ttc", ".otc" };

    public GenericViewer()
    {
        InitializeComponent();
    }

    public void LoadFile(string filePath)
    {
        int token = ++_token;
        _currentFilePath = filePath;
        _shortcutTarget = null;
        Rows.Children.Clear();
        Badges.Children.Clear();
        FontSampleBox.Visibility = Visibility.Collapsed;
        OpenTargetButton.Visibility = Visibility.Collapsed;
        FileIconImage.Source = null;

        try
        {
            var fileInfo = new FileInfo(filePath);
            FileNameText.Text = Path.GetFileName(filePath);
            string ext = Path.GetExtension(filePath).ToUpperInvariant();
            var (typeName, opensWith) = FileFacts.TypeAndApp(filePath);
            FileTypeAndSizeText.Text = $"{(typeName ?? ext)} • {FormatFileSize(fileInfo.Length)}";

            if (typeName != null) AddRow(Loc.T("generic.type"), $"{typeName} ({ext})");
            if (opensWith != null) AddRow(Loc.T("generic.opensWith"), opensWith);
            AddRow(Loc.T("info.modifiedLabel"), fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
            AddRow(Loc.T("info.createdLabel"), fileInfo.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"));
            AddRow(Loc.T("info.pathLabel"), filePath, small: true);

            using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
            if (sysIcon != null)
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(sysIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                FileIconImage.Source = bs;
            }
        }
        catch (Exception ex)
        {
            FileNameText.Text = Path.GetFileName(filePath);
            FileTypeAndSizeText.Text = ex.Message;
        }

        // The small icon above is only a stand-in: Explorer's big one (256 px, or the file's own thumbnail)
        // replaces it a moment later, sharp at any screen scaling
        ShellThumbnails.Request(filePath, 256, ShellThumbnails.CurrentGeneration, bmp => Dispatcher.BeginInvoke(() =>
        {
            if (token == _token) FileIconImage.Source = bmp;
        }));

        if (FontExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())) ShowFont(filePath);
        _ = LoadFactsAsync(filePath, token);
    }

    /// <summary>The checks that read the file (a big installer's signature takes a second or two).</summary>
    private async Task LoadFactsAsync(string path, int token)
    {
        bool signable = FileFacts.CanBeSigned(path);
        Border? checking = signable ? AddBadge("shield", Loc.T("generic.sigChecking"), "TextMuted", "c2A2622") : null;

        var origin = await Task.Run(() => FileFacts.ReadOrigin(path));
        if (token != _token) return;
        if (origin is { Host: not null })
        {
            AddBadge("globe", Loc.T("generic.downloadedFrom", origin.Host), "GoldHover", "GoldTint");
            AddRow(Loc.T("generic.origin"), origin.Url!, small: true, link: origin.Url);
        }
        else if (origin is { FromInternet: true })
        {
            AddBadge("globe", Loc.T("generic.fromInternet"), "GoldHover", "GoldTint");
        }

        var shortcut = await Task.Run(() => FileFacts.ReadShortcut(path));
        if (token != _token) return;
        if (shortcut != null)
        {
            _shortcutTarget = shortcut.Target;
            AddRow(Loc.T(shortcut.IsUrl ? "generic.address" : "generic.target"), shortcut.Target, small: true, insertAt: 0);
            if (shortcut.Arguments != null) AddRow(Loc.T("generic.arguments"), shortcut.Arguments, small: true, insertAt: 1);
            OpenTargetText.Text = Loc.T(shortcut.IsUrl ? "generic.openAddress" : "generic.openTarget");
            OpenTargetButton.Visibility = Visibility.Visible;
        }

        var program = await Task.Run(() => FileFacts.ReadProgram(path));
        if (token != _token) return;
        if (program != null)
        {
            int at = 0;
            if (program.Product != null) AddRow(Loc.T("generic.product"), program.Product, insertAt: at++, strong: true);
            if (program.Version != null) AddRow(Loc.T("generic.version"), program.Version, insertAt: at++);
            if (program.Company != null) AddRow(Loc.T("generic.company"), program.Company, insertAt: at++);
            if (program.Description != null && program.Description != program.Product) AddRow(Loc.T("generic.description"), program.Description, insertAt: at++);
            if (program.Copyright != null) AddRow(Loc.T("generic.copyright"), program.Copyright, insertAt: at, small: true);
        }

        if (!signable) return;
        var signature = await Task.Run(() => FileFacts.CheckSignature(path));
        if (token != _token) return;
        if (checking != null) Badges.Children.Remove(checking);
        switch (signature.State)
        {
            case FileFacts.SignatureState.Valid:
                AddBadge("shield-check", signature.Signer != null ? Loc.T("generic.sigValid", signature.Signer) : Loc.T("generic.sigValidNoName"),
                    "cA6E3A1", "c1F3A2B", first: true);
                break;
            case FileFacts.SignatureState.Invalid:
                AddBadge("shield-x", Loc.T("generic.sigInvalid"), "cF38BA8", "c3A1F1D", first: true);
                break;
            default:
                AddBadge("shield-alert", Loc.T("generic.sigNone"), "GoldSoft", "GoldTintInfo", first: true);
                break;
        }
    }

    // --- Pieces of the card

    private Border AddBadge(string icon, string text, string foregroundKey, string backgroundKey, bool first = false)
    {
        var lucide = new LucideIcon { Kind = icon, Size = 14, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { lucide, label } };
        content.SetResourceReference(TextElement.ForegroundProperty, foregroundKey);
        var badge = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 5, 12, 5), Margin = new Thickness(4), Child = content, ToolTip = text };
        badge.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        if (first) Badges.Children.Insert(0, badge); else Badges.Children.Add(badge);
        return badge;
    }

    private void AddRow(string label, string value, bool small = false, int insertAt = -1, bool strong = false, string? link = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Label" });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var name = new TextBlock { Text = label.TrimEnd(':') + ":", FontSize = 12, Margin = new Thickness(0, 0, 14, 0) };
        name.SetResourceReference(TextBlock.ForegroundProperty, "c8A8074");
        var text = new TextBlock
        {
            Text = value, FontSize = small ? 11.5 : 12.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, link != null ? "GoldHover" : "cDDD6CB");
        if (link != null)
        {
            text.Cursor = System.Windows.Input.Cursors.Hand;
            text.MouseLeftButtonUp += (_, _) => Launch(link);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
        if (insertAt >= 0 && insertAt <= Rows.Children.Count) Rows.Children.Insert(insertAt, grid);
        else Rows.Children.Add(grid);
    }

    /// <summary>A font file: the family's name and a sample written in it.</summary>
    private void ShowFont(string path)
    {
        try
        {
            var family = Fonts.GetFontFamilies(path).FirstOrDefault();
            if (family == null) return;
            var typeface = family.GetTypefaces().FirstOrDefault();
            string name = family.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path);
            FontSampleBig.FontFamily = FontSampleText.FontFamily = FontSampleDigits.FontFamily = family;
            if (typeface != null)
            {
                foreach (var t in new[] { FontSampleBig, FontSampleText, FontSampleDigits })
                {
                    t.FontStyle = typeface.Style;
                    t.FontWeight = typeface.Weight;
                }
            }
            FontSampleText.Text = Loc.T("generic.fontSample");
            FontSampleBox.Visibility = Visibility.Visible;
            AddRow(Loc.T("generic.fontFamily"), name, insertAt: 0, strong: true);
            if (typeface?.FaceNames.Values.FirstOrDefault() is string face) AddRow(Loc.T("generic.fontFace"), face, insertAt: 1);
        }
        catch (Exception ex)
        {
            App.Log($"[GenericViewer] Font sample failed: {ex.Message}");
        }
    }

    // --- Actions

    /// <summary>"Open with…": the window's menu of apps.</summary>
    public event Action? OpenWithRequested;

    private void OnOpenClicked(object sender, RoutedEventArgs e) => OpenWithRequested?.Invoke();

    /// <summary>A shortcut's target (a folder opens in Explorer, a web address in the browser).</summary>
    private void OnOpenTargetClicked(object sender, RoutedEventArgs e)
    {
        if (_shortcutTarget != null) Launch(_shortcutTarget);
    }

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log($"[GenericViewer] Could not open '{target}': {ex.Message}"); }
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
