using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lupik.Core;

namespace Lupik.Views;

/// <summary>
/// An error or a notice in Lupik's style, instead of Windows' message box. The first line of the message is the
/// heading, the rest (e.g. the system's reason) the detail. Enter / Esc close it.
/// </summary>
public partial class MessageCard : Window
{
    private MessageCard(string message, bool error)
    {
        InitializeComponent();
        int newline = message.IndexOf('\n');
        HeadingText.Text = (newline < 0 ? message : message[..newline]).TrimEnd(':', ' ');
        if (newline >= 0 && message[(newline + 1)..].Trim().Length > 0)
        {
            DetailText.Text = message[(newline + 1)..].Trim();
            DetailText.Visibility = Visibility.Visible;
        }
        if (!error)
        {
            BadgeIcon.Kind = "info";
            BadgeIcon.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "GoldSoft");
            IconBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "GoldTintInfo");
            IconBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "GoldTintInfoLine");
        }
        Loaded += (_, _) =>
        {
            Core.Foreground.Take(new WindowInteropHelper(this).Handle); // Enter / Esc reach it directly
            Activate();
            Animate();
        };
    }

    private bool _confirmed;

    /// <summary>A question with two answers: true if <paramref name="okText"/> was chosen (Enter), false for Cancel (Esc).</summary>
    public static bool Confirm(Window? owner, string message, string okText)
    {
        var card = new MessageCard(message, error: false);
        card.OkText.Text = okText;
        card.CancelText.Text = Lupik.Localization.Loc.T("common.cancel");
        card.CancelButton.Visibility = Visibility.Visible;
        if (owner != null && owner.IsVisible) card.Owner = owner;
        else card.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        card.ShowDialog();
        return card._confirmed;
    }

    /// <summary>Shows the message over <paramref name="owner"/>, then gives the keyboard back to where it was.</summary>
    public static void Show(Window? owner, string message, bool error = true)
    {
        IntPtr before = NativeMethods.GetForegroundWindow();
        var card = new MessageCard(message, error);
        if (owner != null && owner.IsVisible) card.Owner = owner;
        else card.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        card.ShowDialog();
        // The preview never keeps the focus: back to Explorer (or whatever had it)
        if (before != IntPtr.Zero && before != new WindowInteropHelper(card).Handle) NativeMethods.SetForegroundWindow(before);
    }

    private void Animate()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) { _confirmed = true; Close(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void OnOk(object sender, RoutedEventArgs e) { _confirmed = true; Close(); }
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
