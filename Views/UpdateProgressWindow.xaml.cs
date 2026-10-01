using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>
/// Lupik's own progress card while an update downloads, instead of Velopack's plain Windows dialog (that one is
/// turned off: the update is applied silently). It stays until Lupik closes for the update to be put in place.
/// </summary>
public partial class UpdateProgressWindow : Window
{
    private double _fraction;

    public UpdateProgressWindow(string version)
    {
        InitializeComponent();
        HeadingText.Text = Loc.T("update.progressTitle", version);
        SetProgress(0);
        Loaded += (_, _) => Animate();
    }

    /// <summary>Download progress, 0–100.</summary>
    public void SetProgress(int percent)
    {
        _fraction = Math.Clamp(percent, 0, 100) / 100.0;
        StatusText.Text = Loc.T("update.progressDownloading");
        PercentText.Text = $"{Math.Clamp(percent, 0, 100)}%";
        UpdateFill(animated: true);
    }

    /// <summary>Downloaded: Lupik is about to close and come back in the new version.</summary>
    public void SetRestarting()
    {
        _fraction = 1;
        StatusText.Text = Loc.T("update.progressRestarting");
        PercentText.Text = "100%";
        UpdateFill(animated: true);
    }

    private void UpdateFill(bool animated)
    {
        double width = Track.ActualWidth * _fraction;
        if (!animated || Track.ActualWidth <= 0) { Fill.Width = width; return; }
        Fill.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase() });
    }

    private void OnTrackSized(object sender, SizeChangedEventArgs e) => UpdateFill(animated: false);

    private void Animate()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
