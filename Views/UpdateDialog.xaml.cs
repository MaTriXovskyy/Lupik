using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>"Lupik X is available, update now?" Enter updates, Esc postpones.</summary>
public partial class UpdateDialog : Window
{
    private UpdateDialog(string version, string current)
    {
        InitializeComponent();
        HeadingText.Text = Loc.T("update.dialogHeading", version);
        BodyText.Text = Loc.T("update.dialogBody", current);
        Loaded += (_, _) => Animate();
    }

    /// <summary>True if the user wants to update now.</summary>
    public static bool Ask(Window? owner, string version, string current)
    {
        var dialog = new UpdateDialog(version, current);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        return dialog.ShowDialog() == true;
    }

    private void Animate()
    {
        Activate();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) { DialogResult = true; e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    private void OnUpdate(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnLater(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
