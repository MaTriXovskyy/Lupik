using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lupik.Core;

namespace Lupik.Views;

/// <summary>
/// Audio / video player on WPF's MediaElement (Windows Media Foundation), so it plays whatever codecs Windows has.
/// Starts playing right away, like Quick Look; the file is released as soon as the preview moves on.
/// </summary>
public partial class MediaViewer : UserControl
{
    public static readonly string[] VideoExtensions = { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".3gp" };
    public static readonly string[] AudioExtensions = { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".opus" };

    public static bool IsMedia(string ext) => Array.IndexOf(VideoExtensions, ext) >= 0 || Array.IndexOf(AudioExtensions, ext) >= 0;

    private readonly DispatcherTimer _progressTimer;
    private TaskCompletionSource<bool>? _opening;
    private string _currentPath = "";
    private bool _isPlaying;
    private bool _updatingSeek; // timer moving the slider must not seek
    private static double _volume = 0.8; // kept for the whole session

    public bool IsVideo { get; private set; }
    public int NaturalWidth => Player.NaturalVideoWidth;
    public int NaturalHeight => Player.NaturalVideoHeight;

    public MediaViewer()
    {
        InitializeComponent();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        VolumeSlider.Value = _volume;
    }

    /// <summary>Opens and starts playing. False if Windows can't play the file (missing codec, damaged file).</summary>
    public async Task<bool> LoadAsync(string filePath)
    {
        _opening?.TrySetResult(false); // an older load still waiting: superseded
        var opening = _opening = new TaskCompletionSource<bool>();

        Stop();
        _currentPath = filePath;
        IsVideo = Array.IndexOf(VideoExtensions, Path.GetExtension(filePath).ToLowerInvariant()) >= 0;
        AudioPanel.Visibility = IsVideo ? Visibility.Collapsed : Visibility.Visible;
        AudioTitleText.Text = Path.GetFileNameWithoutExtension(filePath);
        CoverImage.Source = null;
        if (!IsVideo)
        {
            // Album art comes from the shell thumbnail (falls back to the file-type icon)
            ShellThumbnails.Request(filePath, 256, ShellThumbnails.CurrentGeneration,
                bmp => Dispatcher.BeginInvoke(() => { if (_currentPath == filePath) CoverImage.Source = bmp; }));
        }

        Player.Volume = _volume;
        Player.Source = new Uri(filePath);
        Player.Play();
        SetPlaying(true);

        // Some broken files never raise either event
        var finished = await Task.WhenAny(opening.Task, Task.Delay(8000));
        bool ok = finished == opening.Task && opening.Task.Result;
        if (!ok && opening == _opening)
        {
            App.Log($"[MediaViewer] Could not open '{filePath}'");
            Stop();
        }
        return ok;
    }

    /// <summary>Stops playback and closes the file (so it isn't locked, e.g. before deleting it).</summary>
    public void Stop()
    {
        _progressTimer.Stop();
        Player.Stop();
        Player.Close();
        Player.Source = null;
        SetPlaying(false);
        _updatingSeek = true;
        SeekSlider.Value = 0;
        _updatingSeek = false;
        TimeText.Text = "0:00 / 0:00";
    }

    public void TogglePlay()
    {
        if (Player.Source == null) return;
        if (_isPlaying) Player.Pause(); else Player.Play();
        SetPlaying(!_isPlaying);
    }

    public void SeekBy(double seconds)
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        var target = Player.Position + TimeSpan.FromSeconds(seconds);
        var length = Player.NaturalDuration.TimeSpan;
        Player.Position = target < TimeSpan.Zero ? TimeSpan.Zero : target > length ? length : target;
        UpdateProgress();
    }

    public void ToggleMute()
    {
        Player.IsMuted = !Player.IsMuted;
        MuteIcon.Kind = Player.IsMuted ? "volume-x" : "volume-2";
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        PlayIcon.Kind = playing ? "pause" : "play";
        if (playing) _progressTimer.Start(); else _progressTimer.Stop();
    }

    private void UpdateProgress()
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        var length = Player.NaturalDuration.TimeSpan;
        _updatingSeek = true;
        SeekSlider.Maximum = Math.Max(1, length.TotalSeconds);
        SeekSlider.Value = Player.Position.TotalSeconds;
        _updatingSeek = false;
        TimeText.Text = $"{Format(Player.Position)} / {Format(length)}";
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        UpdateProgress();
        _opening?.TrySetResult(true);
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        App.Log($"[MediaViewer] Media failed: {e.ErrorException?.Message}");
        _opening?.TrySetResult(false);
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        // Back to the start, paused: pressing play again replays it
        Player.Pause();
        Player.Position = TimeSpan.Zero;
        SetPlaying(false);
        UpdateProgress();
    }

    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeek || Player.Source == null) return;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
        TimeText.Text = Player.NaturalDuration.HasTimeSpan
            ? $"{Format(Player.Position)} / {Format(Player.NaturalDuration.TimeSpan)}"
            : TimeText.Text;
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _volume = e.NewValue;
        if (Player != null) Player.Volume = _volume;
    }

    private void OnPlayClicked(object sender, RoutedEventArgs e) => TogglePlay();
    private void OnMuteClicked(object sender, RoutedEventArgs e) => ToggleMute();
    private void OnSurfaceClicked(object sender, MouseButtonEventArgs e) => TogglePlay();
}
