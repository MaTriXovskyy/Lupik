using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Lupik;

public partial class MainWindow
{
    private DispatcherTimer? _titleProgressTimer;
    private bool _fileLoadPending, _titleTracksImage;
    private DateTime _titleProgressStarted;
    private double _titleProgressValue;

    private void BeginTitleProgress(bool image)
    {
        _titleTracksImage = image;
        _titleProgressStarted = DateTime.UtcNow;
        _titleProgressValue = 0;
        TitleProgress.BeginAnimation(OpacityProperty, null);
        TitleProgress.Opacity = 0; // don't flash for a cached or very small file
        TitleProgressFill.BeginAnimation(WidthProperty, null);
        TitleProgressFill.Width = 0;
        TitleProgressFill.Margin = new Thickness(0);
        _titleProgressTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render,
            (_, _) => UpdateTitleProgress(), Dispatcher);
        _titleProgressTimer.Start();
    }

    private void UpdateTitleProgress()
    {
        double? progress = _titleTracksImage ? ImageViewerControl.LoadingProgress : null;
        if (!_fileLoadPending && progress == null)
        {
            FinishTitleProgress();
            return;
        }
        double elapsed = (DateTime.UtcNow - _titleProgressStarted).TotalSeconds;
        if (elapsed < 0.15) return;
        TitleProgress.Opacity = 1;
        if (progress is double value && !double.IsNaN(value))
        {
            _titleProgressValue = Math.Max(_titleProgressValue, value);
            TitleProgressFill.Margin = new Thickness(0);
            // Smooth each actual progress report, rather than inventing progress on a timer.
            double width = TitleProgress.ActualWidth * _titleProgressValue;
            TitleProgressFill.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(120)));
        }
        else
        {
            // A decoder with no measurable progress (e.g. vector rendering): keep a moving accent.
            TitleProgressFill.BeginAnimation(WidthProperty, null);
            TitleProgressFill.Width = TitleProgress.ActualWidth * 0.28;
            double phase = (elapsed % 1.5) / 1.5;
            TitleProgressFill.Margin = new Thickness(TitleProgress.ActualWidth * (phase * 1.28 - 0.28), 0, 0, 0);
        }
    }

    private void FinishTitleProgress()
    {
        _titleProgressTimer?.Stop();
        if (TitleProgress.Opacity == 0) return;
        TitleProgressFill.Margin = new Thickness(0);
        TitleProgressFill.BeginAnimation(WidthProperty, new DoubleAnimation(TitleProgress.ActualWidth, TimeSpan.FromMilliseconds(140)));
        TitleProgress.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260))
        {
            BeginTime = TimeSpan.FromMilliseconds(180),
        });
    }

    private void CancelTitleProgress()
    {
        _titleProgressTimer?.Stop();
        _fileLoadPending = false;
        TitleProgress.BeginAnimation(OpacityProperty, null);
        TitleProgress.Opacity = 0;
        TitleProgressFill.BeginAnimation(WidthProperty, null);
    }
}
