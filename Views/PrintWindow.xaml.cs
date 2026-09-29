using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lupik.Core;

using Lupik.Localization;
namespace Lupik.Views;

/// <summary>Chrome-style print dialog: settings on the right, live preview of the sheet on the left.</summary>
public partial class PrintWindow : Window
{
    private readonly IPrintSource _source;
    private readonly Dictionary<int, BitmapSource> _previewCache = new();
    private readonly Dictionary<int, System.Drawing.SizeF> _pageSizes = new();

    private List<int> _pages = new();      // zero-based pages that will print
    private int _previewPosition;          // index into _pages
    private int _printerToken;             // ignores stale printer lookups
    private int _previewToken;
    private bool _loaded;

    private sealed record PrinterCapabilities(bool Valid, bool Color, bool Duplex, List<string> Papers, string? DefaultPaper,
        Dictionary<string, System.Drawing.SizeF> PaperInches);
    private PrinterCapabilities? _printer;

    public PrintWindow(IPrintSource source)
    {
        InitializeComponent();
        _source = source;

        Loaded += (_, _) =>
        {
            var printers = PrintService.InstalledPrinters();
            string preferred = Settings.Current.LastPrinter is { } last && printers.Contains(last)
                ? last
                : PrintService.DefaultPrinter();
            foreach (var name in printers)
                PrinterBox.Items.Add(new ComboBoxItem { Content = name == PrintService.DefaultPrinter() ? $"{name}  " + Loc.T("print.defaultPrinter") : name, Tag = name });
            PrinterBox.SelectedItem = PrinterBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == preferred)
                                      ?? PrinterBox.Items.Cast<ComboBoxItem>().FirstOrDefault();

            _loaded = true;
            Refresh();
        };
        Closed += (_, _) => _source.Dispose();
    }

    private string? SelectedPrinter => (PrinterBox.SelectedItem as ComboBoxItem)?.Tag as string;

    // ---------- Printer capabilities ----------

    private async void OnPrinterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedPrinter is not { } name) return;
        int token = ++_printerToken;
        PrinterInfo.Text = Loc.T("print.checking");
        PrintButton.IsEnabled = false;

        // Network printers can take a moment to answer; don't freeze the dialog
        var info = await Task.Run(() =>
        {
            var s = new PrinterSettings { PrinterName = name };
            if (!s.IsValid) return new PrinterCapabilities(false, false, false, new(), null, new());
            var papers = s.PaperSizes.Cast<PaperSize>().Where(p => p.Width > 0 && p.Height > 0).ToList();
            return new PrinterCapabilities(true, s.SupportsColor, s.CanDuplex,
                papers.Select(p => p.PaperName).Distinct().ToList(),
                s.DefaultPageSettings.PaperSize?.PaperName,
                papers.GroupBy(p => p.PaperName).ToDictionary(g => g.Key, g => new System.Drawing.SizeF(g.First().Width / 100f, g.First().Height / 100f)));
        });
        if (token != _printerToken) return;

        _printer = info;
        PrintButton.IsEnabled = info.Valid;
        PrinterInfo.Text = info.Valid
            ? string.Join("  •  ", new[] { Loc.T(info.Color ? "print.infoColor" : "print.infoMono"), info.Duplex ? Loc.T("print.infoDuplex") : null }.Where(s => s != null))
            : Loc.T("print.printerUnavailable");

        // Color / duplex only where the printer supports them
        ColorBox.IsEnabled = info.Color;
        if (!info.Color) ColorBox.SelectedIndex = 1;
        DuplexPanel.Visibility = info.Duplex ? Visibility.Visible : Visibility.Collapsed;
        if (!info.Duplex) DuplexBox.SelectedIndex = 0;

        // Keep the chosen paper if the new printer has it, else its default (usually A4)
        string? keep = (PaperBox.SelectedItem as ComboBoxItem)?.Tag as string;
        PaperBox.Items.Clear();
        foreach (var paper in info.Papers) PaperBox.Items.Add(new ComboBoxItem { Content = paper, Tag = paper });
        string? choose = keep != null && info.Papers.Contains(keep) ? keep : info.DefaultPaper;
        PaperBox.SelectedItem = PaperBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == choose)
                                ?? PaperBox.Items.Cast<ComboBoxItem>().FirstOrDefault();

        Refresh();
    }

    // ---------- Settings ----------

    private void OnPagesModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageRangeBox == null) return;
        PageRangeBox.Visibility = PagesModeBox.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (PagesModeBox.SelectedIndex == 3 && string.IsNullOrWhiteSpace(PageRangeBox.Text))
            PageRangeBox.Text = _source.PageCount > 1 ? $"1-{_source.PageCount}" : "1";
        if (PagesModeBox.SelectedIndex == 3) PageRangeBox.Focus();
        Refresh();
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e) => Refresh();

    private void OnCopiesTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

    private void OnCopiesMinus(object sender, RoutedEventArgs e) => CopiesBox.Text = Math.Max(1, Copies - 1).ToString();
    private void OnCopiesPlus(object sender, RoutedEventArgs e) => CopiesBox.Text = Math.Min(999, Copies + 1).ToString();

    private int Copies => int.TryParse(CopiesBox.Text, out int c) ? Math.Clamp(c, 1, 999) : 1;

    /// <summary>Pages to print, or null if the custom range is invalid.</summary>
    private List<int>? ComputePages()
    {
        var all = Enumerable.Range(0, _source.PageCount);
        return PagesModeBox.SelectedIndex switch
        {
            1 => all.Where(i => i % 2 == 0).ToList(), // pages 1, 3, 5…
            2 => all.Where(i => i % 2 == 1).ToList(),
            3 => PrintService.ParsePageRange(PageRangeBox.Text, _source.PageCount),
            _ => all.ToList(),
        };
    }

    private PrintOptions BuildOptions() => new()
    {
        PrinterName = SelectedPrinter ?? "",
        Copies = Copies,
        Collate = CollateBox.IsChecked == true,
        Pages = _pages,
        Orientation = OrientationBox.SelectedIndex switch { 1 => PrintOrientation.Portrait, 2 => PrintOrientation.Landscape, _ => PrintOrientation.Auto },
        Color = ColorBox.SelectedIndex == 0,
        Duplex = DuplexBox.SelectedIndex switch { 1 => Duplex.Vertical, 2 => Duplex.Horizontal, _ => Duplex.Simplex },
        PaperSizeName = (PaperBox.SelectedItem as ComboBoxItem)?.Tag as string,
        Scale = ScaleBox.SelectedIndex == 1 ? PrintScale.ActualSize : PrintScale.FitToPage,
    };

    /// <summary>Re-validates the settings and updates the sheet count and preview.</summary>
    private void Refresh()
    {
        if (!_loaded) return;

        var pages = ComputePages();
        bool rangeValid = pages != null;
        PageRangeBox.Tag = rangeValid ? null : "invalid";
        CollateBox.Visibility = Copies > 1 && (pages?.Count ?? 0) > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (!rangeValid)
        {
            SheetsText.Text = "";
            StatusText.Text = Loc.T("print.badRange", _source.PageCount);
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8));
            PrintButton.IsEnabled = false;
            return;
        }

        _pages = pages!;
        _previewPosition = Math.Clamp(_previewPosition, 0, _pages.Count - 1);
        StatusText.Text = "";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xB5, 0xAB, 0x9D));
        PrintButton.IsEnabled = _printer?.Valid == true;

        int sidesPerSheet = DuplexBox.SelectedIndex > 0 ? 2 : 1;
        int sheets = (int)Math.Ceiling(_pages.Count / (double)sidesPerSheet) * Copies;
        SheetsText.Text = Loc.Plural("count.sheets", sheets);

        _ = UpdatePreviewAsync();
    }

    // ---------- Preview ----------

    private void OnPrevPage(object sender, RoutedEventArgs e) { _previewPosition = Math.Max(0, _previewPosition - 1); _ = UpdatePreviewAsync(); }
    private void OnNextPage(object sender, RoutedEventArgs e) { _previewPosition = Math.Min(_pages.Count - 1, _previewPosition + 1); _ = UpdatePreviewAsync(); }

    private void OnPreviewAreaSizeChanged(object sender, SizeChangedEventArgs e) => LayoutPaper();

    private async Task UpdatePreviewAsync()
    {
        if (_pages.Count == 0) return;
        int token = ++_previewToken;
        int page = _pages[_previewPosition];

        PageIndicator.Text = Loc.T("pdf.page", _previewPosition + 1, _pages.Count) +
                             (_pages.Count != _source.PageCount ? "  " + Loc.T("print.documentPage", page + 1) : "");
        PrevPageButton.IsEnabled = _previewPosition > 0;
        NextPageButton.IsEnabled = _previewPosition < _pages.Count - 1;

        if (!_pageSizes.ContainsKey(page))
            _pageSizes[page] = await Task.Run(() => _source.PageSizeInches(page));
        if (token != _previewToken) return;

        // Paper shape/orientation/scale update right away; the page image follows when rendered
        LayoutPaper();

        if (!_previewCache.TryGetValue(page, out var bitmap))
        {
            // Only show the "preparing" text when there's nothing on the sheet yet
            PreviewStatus.Visibility = PageImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
            bitmap = await Task.Run(() =>
            {
                using var rendered = _source.RenderPage(page, 1000);
                return ToBitmapSource(rendered);
            });
            _previewCache[page] = bitmap;
            if (token != _previewToken) return;
        }

        PreviewStatus.Visibility = Visibility.Collapsed;
        PageImage.Source = ColorBox.SelectedIndex == 1
            ? new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0) // what black & white will look like
            : bitmap;
        LayoutPaper();
    }

    /// <summary>Sizes the white sheet like the chosen paper and places the page on it (fit or actual size).</summary>
    private void LayoutPaper()
    {
        if (_pages.Count == 0 || !_pageSizes.TryGetValue(_pages[_previewPosition], out var pageInches)) return;

        var options = BuildOptions();
        bool landscape = PrintService.IsLandscape(options, pageInches);

        var paper = _printer?.PaperInches.GetValueOrDefault(options.PaperSizeName ?? "") ?? default;
        if (paper.Width <= 0) paper = new System.Drawing.SizeF(8.27f, 11.69f); // A4 until the printer answers
        double paperW = landscape ? Math.Max(paper.Width, paper.Height) : Math.Min(paper.Width, paper.Height);
        double paperH = landscape ? Math.Min(paper.Width, paper.Height) : Math.Max(paper.Width, paper.Height);

        double availW = PreviewArea.ActualWidth, availH = PreviewArea.ActualHeight;
        if (availW <= 0 || availH <= 0) return;
        double pxPerInch = Math.Min(availW / paperW, availH / paperH);
        Paper.Width = paperW * pxPerInch;
        Paper.Height = paperH * pxPerInch;

        // Approximate the printer's unprintable margin (~0.17") so the preview matches the printout
        double margin = 0.17 * pxPerInch;
        Printable.Margin = new Thickness(margin);
        double areaW = Paper.Width - 2 * margin, areaH = Paper.Height - 2 * margin;

        double pageW = pageInches.Width * pxPerInch, pageH = pageInches.Height * pxPerInch;
        if (options.Scale == PrintScale.FitToPage)
        {
            double s = Math.Min(areaW / pageW, areaH / pageH);
            pageW *= s;
            pageH *= s;
        }
        PageImage.Width = pageW;
        PageImage.Height = pageH;
        PageImage.Stretch = Stretch.Fill;
    }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // ---------- Printing ----------

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        if (!PrintButton.IsEnabled) return;
        var options = BuildOptions();

        PrintButton.IsEnabled = false;
        IsEnabledSettings(false);
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xB5, 0xAB, 0x9D));
        StatusText.Text = Loc.T("print.sending");

        try
        {
            await PrintService.PrintAsync(_source, options);
            Settings.Update(s => s.LastPrinter = options.PrinterName);
            DialogResult = true; // closes the dialog
        }
        catch (Exception ex)
        {
            App.Log($"[PrintWindow] Print failed: {ex}");
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8));
            StatusText.Text = Loc.T("print.error", ex.Message);
            PrintButton.IsEnabled = true;
            IsEnabledSettings(true);
        }
    }

    private void IsEnabledSettings(bool enabled)
    {
        foreach (var control in new Control[] { PrinterBox, PagesModeBox, PageRangeBox, CopiesBox, OrientationBox, ColorBox, DuplexBox, PaperBox, ScaleBox })
            control.IsEnabled = enabled && (control != ColorBox || _printer?.Color == true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static string Plural(int n, string one, string few, string many)
    {
        if (n == 1) return one;
        int lastTwo = n % 100, last = n % 10;
        return last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? few : many;
    }
}
