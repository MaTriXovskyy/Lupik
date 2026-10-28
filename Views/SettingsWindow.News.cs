using System.Windows;
using System.Windows.Controls;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik.Views;

public partial class SettingsWindow
{
    private string? _newsLanguage, _newsVersion;

    private void BuildNews()
    {
        string current = Updater.CurrentVersion.Split('-', '+')[0];
        if (_newsLanguage == Loc.Instance.Code && _newsVersion == current) return;
        _newsLanguage = Loc.Instance.Code;
        _newsVersion = current;
        NewsEntries.Children.Clear();
        foreach (var entry in ReleaseHistory.Entries)
        {
            var content = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
            if (entry.Version == current)
            {
                var badgeText = new TextBlock { Text = Loc.T("news.current"), FontSize = 11, FontWeight = FontWeights.SemiBold };
                badgeText.SetResourceReference(TextBlock.ForegroundProperty, "GoldHover");
                var badge = new Border { Child = badgeText, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
                badge.SetResourceReference(Border.BackgroundProperty, "GoldTintAlt");
                DockPanel.SetDock(badge, Dock.Right);
                header.Children.Add(badge);
            }
            var version = new TextBlock { Text = entry.Version, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            version.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            header.Children.Add(version);
            content.Children.Add(header);
            foreach (string key in entry.Items)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var bullet = new TextBlock { Text = "•", FontSize = 13 };
                bullet.SetResourceReference(TextBlock.ForegroundProperty, "Gold");
                var text = new TextBlock { Text = Loc.T(key), TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                Grid.SetColumn(text, 1);
                row.Children.Add(bullet); row.Children.Add(text);
                content.Children.Add(row);
            }
            NewsEntries.Children.Add(new Border { Style = (Style)FindResource("Row"), Child = content, Margin = new Thickness(0, 0, 0, 12) });
        }
    }
}
