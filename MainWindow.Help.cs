using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Lupik.Localization;

namespace Lupik;

/// <summary>
/// The shortcuts card: "?" in the footer (or F1) slides it up above the footer. It lists the keys for what's on
/// screen first, then the ones that always work, so the footer can stay free of buttons for every feature.
/// </summary>
public partial class MainWindow
{
    private Popup? _helpPopup;
    private DateTime _helpClosedAt;

    private sealed record HelpRow(string[] Keys, string Text);

    private bool HelpOpen => _helpPopup?.IsOpen == true;

    private void WireHelp() => ImageViewerControl.HelpRequested += ToggleHelp;

    private void OnHelpClicked(object sender, RoutedEventArgs e) => ToggleHelp();

    private void CloseHelp()
    {
        if (_helpPopup != null) _helpPopup.IsOpen = false;
    }

    private void ToggleHelp()
    {
        if (HelpOpen) { CloseHelp(); return; }
        // Clicking "?" while the card is open: the click outside already closed it, don't open it again
        if ((DateTime.UtcNow - _helpClosedAt).TotalMilliseconds < 250) return;

        FrameworkElement target = ImageViewerControl.Visibility == Visibility.Visible ? ImageViewerControl.Footer : FooterBar;
        if (!target.IsVisible) return;
        var card = BuildHelpCard();
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        if (_helpPopup == null)
        {
            _helpPopup = new Popup
            {
                AllowsTransparency = true, StaysOpen = false, PopupAnimation = PopupAnimation.Slide, Placement = PlacementMode.Top,
            };
            _helpPopup.Closed += (_, _) => _helpClosedAt = DateTime.UtcNow;
        }
        _helpPopup.Child = card;
        _helpPopup.PlacementTarget = target;
        _helpPopup.HorizontalOffset = (target.ActualWidth - card.DesiredSize.Width) / 2;
        _helpPopup.VerticalOffset = 6; // the card's own margin holds the shadow
        _helpPopup.IsOpen = true;
    }

    /// <summary>The keys for what's shown now (null when there's nothing special about it).</summary>
    private (string Title, List<HelpRow> Rows)? ContextHelp()
    {
        if (CompareViewerControl.Visibility == Visibility.Visible)
            return (Loc.T("help.compare"), new()
            {
                new(new[] { "S" }, Loc.T("help.slider")),
                new(new[] { "X" }, Loc.T("help.swap")),
                new(new[] { "+", "−" }, Loc.T("help.zoom")),
                new(new[] { "0" }, Loc.T("help.fit")),
            });
        if (ImageViewerControl.Visibility == Visibility.Visible)
            return (Loc.T("help.image"), new()
            {
                new(new[] { "+", "−" }, Loc.T("help.zoom")),
                new(new[] { "0" }, Loc.T("help.fit")),
                new(new[] { "R", "Shift+R" }, Loc.T("help.rotate")),
                new(new[] { "T" }, Loc.T("help.ocr")),
                new(new[] { "S" }, Loc.T("help.filmstrip")),
                new(new[] { "K" }, Loc.T("help.crop")),
                new(new[] { "I" }, Loc.T("help.info")),
                new(new[] { "B" }, Loc.T("help.background")),
                new(new[] { "Ctrl" }, Loc.T("help.pipette")),
                new(new[] { "Ctrl+C" }, Loc.T("help.copyImage")),
            });
        if (PdfViewerControl.Visibility == Visibility.Visible)
            return (Loc.T("help.pdf"), new()
            {
                new(new[] { "↑", "↓" }, Loc.T("help.scroll")),
                new(new[] { "PgUp", "PgDn" }, Loc.T("help.page")),
                new(new[] { "Home", "End" }, Loc.T("help.firstLast")),
                new(new[] { "1–9", "Enter" }, Loc.T("help.goToPage")),
            });
        if (MediaViewerControl.Visibility == Visibility.Visible)
            return (Loc.T("help.media"), new()
            {
                new(new[] { "K" }, Loc.T("help.play")),
                new(new[] { "J", "L" }, Loc.T("help.seek")),
                new(new[] { "M" }, Loc.T("help.mute")),
            });
        if (IsMarkdownShown)
            return (Loc.T("help.markdown"), new()
            {
                new(new[] { "M" }, Loc.T("help.markdownSource")),
                new(new[] { "↑", "↓" }, Loc.T("help.scroll")),
            });
        if (DiffViewerControl.Visibility == Visibility.Visible)
            return (Loc.T("help.diff"), new() { new(new[] { "↑", "↓" }, Loc.T("help.nextChange")) });
        if (CodeViewerControl.Visibility == Visibility.Visible)
            return (Loc.T("help.code"), new() { new(new[] { "↑", "↓" }, Loc.T("help.scroll")) });
        if (CsvViewerControl.Visibility == Visibility.Visible || ArchiveViewerControl.Visibility == Visibility.Visible)
            return (Loc.T(CsvViewerControl.Visibility == Visibility.Visible ? "help.table" : "help.archive"), new()
            {
                new(new[] { "Ctrl+A" }, Loc.T("help.selectAll")),
                new(new[] { "Ctrl+C" }, Loc.T("help.copySelection")),
            });
        return null;
    }

    private List<HelpRow> GeneralHelp()
    {
        var rows = new List<HelpRow>
        {
            new(new[] { "←", "→" }, Loc.T("hint.prevNext")),
            new(new[] { Loc.T("key.preview"), "Esc" }, Loc.T("hint.close")),
            new(new[] { "Enter" }, Loc.T("help.openWith")),
            new(new[] { "F" }, Loc.T("help.fullScreen")),
            new(new[] { "P" }, Loc.T("help.pin")),
        };
        if (CurrentEditable() != null || IsMarkdownShown) rows.Add(new(new[] { "E" }, Loc.T("help.edit")));
        if (CanToggleCompare()) rows.Add(new(new[] { "C" }, Loc.T("help.compareToggle")));
        if (SearchableInPreview) rows.Add(new(new[] { "Ctrl+F" }, Loc.T("help.search")));
        rows.Add(new(new[] { "F2" }, Loc.T("help.rename")));
        rows.Add(new(new[] { "Delete" }, Loc.T("help.delete")));
        rows.Add(new(new[] { "Ctrl+S" }, Loc.T("help.saveAs")));
        rows.Add(new(new[] { "Ctrl+P" }, Loc.T("help.print")));
        if (_folderHistory.Count > 0 || _forwardHistory.Count > 0) rows.Add(new(new[] { "Alt+←", "Alt+→" }, Loc.T("help.folderBack")));
        return rows;
    }

    private FrameworkElement BuildHelpCard()
    {
        var columns = new StackPanel { Orientation = Orientation.Horizontal };
        if (ContextHelp() is { } context)
        {
            columns.Children.Add(BuildHelpSection(context.Title, context.Rows));
            var divider = new Border { Width = 1, Margin = new Thickness(20, 4, 20, 4) };
            divider.SetResourceReference(Border.BackgroundProperty, "c3B3631");
            columns.Children.Add(divider);
        }
        columns.Children.Add(BuildHelpSection(Loc.T("help.general"), GeneralHelp()));

        var title = new TextBlock { Text = Loc.T("help.title"), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "cFBF8F2");
        var content = new StackPanel();
        content.Children.Add(title);
        content.Children.Add(columns);

        var card = new Border
        {
            Child = content, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 18), Margin = new Thickness(16, 16, 16, 10),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = 0.5, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "c1B1917");
        card.SetResourceReference(Border.BorderBrushProperty, "c35302A");
        return card;
    }

    private static FrameworkElement BuildHelpSection(string header, List<HelpRow> rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var head = new TextBlock { Text = header.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Gold");
        grid.RowDefinitions.Add(new RowDefinition());
        Grid.SetColumnSpan(head, 2);
        grid.Children.Add(head);

        foreach (var row in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            int r = grid.RowDefinitions.Count - 1;

            var keys = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 6), VerticalAlignment = VerticalAlignment.Center };
            foreach (string k in row.Keys) keys.Children.Add(HelpCap(k));
            Grid.SetRow(keys, r);
            grid.Children.Add(keys);

            var text = new TextBlock { Text = row.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "cB5AB9D");
            Grid.SetRow(text, r);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
        }
        return grid;
    }

    /// <summary>A key drawn like the footer's key caps.</summary>
    private static Border HelpCap(string key)
    {
        var label = new TextBlock { Text = key, FontSize = 10.5 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "cFBF8F2");
        var cap = new Border
        {
            Child = label, BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 4, 0), MinWidth = 22,
        };
        label.HorizontalAlignment = HorizontalAlignment.Center;
        cap.SetResourceReference(Border.BorderBrushProperty, "c3B3631");
        cap.SetResourceReference(Border.BackgroundProperty, "c24211E");
        return cap;
    }
}
