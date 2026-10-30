using System.Globalization;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Lupik.Core;
using Lupik.Localization;

namespace Lupik.Views;

/// <summary>
/// Edit mode (E) for audio and video: set where it starts and ends, and cut parts out of the middle (drag along the
/// timeline to select one, then Delete). Playback skips what's cut, so the result can be heard before saving.
/// Saving encodes the kept parts into a new file of the same type with FFmpeg.
/// </summary>
public partial class MediaViewer : IEditable
{
    private sealed record EditState(double In, double Out, List<(double A, double B)> Cuts);

    private string _sourcePath = "";
    private bool _editing;
    private double _duration, _in, _out;
    private readonly List<(double A, double B)> _cuts = new();
    private (double A, double B)? _selection;
    private readonly Stack<EditState> _undo = new();

    public event Action? DirtyChanged;

    public bool IsDirty => _editing && (_in > 0.001 || _out < _duration - 0.001 || _cuts.Count > 0);

    public string? WhyNotEditable()
    {
        string ext = System.IO.Path.GetExtension(_sourcePath);
        if (!MediaCut.Supports(_sourcePath)) return Loc.T("edit.mediaFormat", ext.TrimStart('.').ToUpperInvariant());
        if (!Player.NaturalDuration.HasTimeSpan || Player.NaturalDuration.TimeSpan.TotalSeconds <= 0) return Loc.T("edit.cantRead");
        return null;
    }

    public void BeginEdit()
    {
        _editing = true;
        ResetEdit();
        EditStrip.Visibility = Visibility.Visible;
        Timeline.ToolTip = Loc.T("mediaEdit.hint");
        _progressTimer.Interval = TimeSpan.FromMilliseconds(40); // skipping cut parts while playing needs a quick look
        Ffmpeg.WarmUp(); // the first start of ffmpeg.exe is slow (antivirus scan): done now, not when saving
    }

    public void EndEdit()
    {
        _editing = false;
        _cuts.Clear();
        _undo.Clear();
        _selection = null;
        EditStrip.Visibility = Visibility.Collapsed;
        Timeline.Children.Clear();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(250);
    }

    private void ResetEdit()
    {
        _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        _in = 0;
        _out = _duration;
        _cuts.Clear();
        _undo.Clear();
        _selection = null;
        Changed(notify: false);
    }

    // ---------- Changes and undo ----------

    private void PushUndo() => _undo.Push(new EditState(_in, _out, _cuts.ToList()));

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var state = _undo.Pop();
        _in = state.In;
        _out = state.Out;
        _cuts.Clear();
        _cuts.AddRange(state.Cuts);
        _selection = null;
        Changed();
    }

    private void Changed(bool notify = true)
    {
        DrawTimeline();
        UpdateEditInfo();
        if (notify) DirtyChanged?.Invoke();
    }

    /// <summary>Math.Clamp that can't throw: a range that ended before it started (a very short file) gives its start.</summary>
    private static double Within(double value, double min, double max) => max < min ? min : Math.Clamp(value, min, max);

    private void SetStart(double t)
    {
        t = Within(t, 0, _out - 0.1);
        if (Math.Abs(t - _in) < 0.001) return;
        PushUndo();
        _in = t;
        Changed();
    }

    private void SetEnd(double t)
    {
        t = Within(t, _in + 0.1, _duration);
        if (Math.Abs(t - _out) < 0.001) return;
        PushUndo();
        _out = t;
        Changed();
    }

    private void CutSelection()
    {
        if (_selection is not { } s || s.B - s.A < 0.05) return;
        PushUndo();
        _cuts.Add(s);
        // Overlapping cuts become one
        var merged = new List<(double A, double B)>();
        foreach (var c in _cuts.OrderBy(c => c.A))
        {
            if (merged.Count > 0 && c.A <= merged[^1].B) merged[^1] = (merged[^1].A, Math.Max(merged[^1].B, c.B));
            else merged.Add(c);
        }
        _cuts.Clear();
        _cuts.AddRange(merged);
        _selection = null;
        Changed();
    }

    /// <summary>The parts that stay: between the start and the end, minus the cuts.</summary>
    private List<(double Start, double End)> KeptParts()
    {
        var parts = new List<(double, double)>();
        double from = _in;
        foreach (var (a, b) in _cuts.OrderBy(c => c.A))
        {
            if (b <= from || a >= _out) continue;
            if (a - from >= 0.05) parts.Add((from, Math.Min(a, _out)));
            from = Math.Max(from, b);
        }
        if (_out - from >= 0.05) parts.Add((from, _out));
        return parts;
    }

    private void UpdateEditInfo()
    {
        double kept = KeptParts().Sum(p => p.End - p.Start);
        string text = Loc.T("mediaEdit.length", Precise(kept));
        if (_selection is { } s && s.B - s.A >= 0.05) text += "  ·  " + Loc.T("mediaEdit.selection", Precise(s.B - s.A));
        EditInfoText.Text = text;
        CutButton.IsEnabled = _selection is { } sel && sel.B - sel.A >= 0.05;
        UndoCutButton.IsEnabled = _undo.Count > 0;
    }

    private static string Precise(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        string tenths = ((int)(t.Milliseconds / 100)).ToString(CultureInfo.InvariantCulture);
        return (t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss")) + "." + tenths;
    }

    // ---------- Timeline ----------

    private enum TimelineDrag { None, Start, End, Select }
    private TimelineDrag _drag;
    private double _dragFrom;
    private bool _dragMoved;
    private Line? _playhead;

    private double X(double t) => _duration <= 0 ? 0 : t / _duration * Timeline.ActualWidth;
    private double T(double x) => Timeline.ActualWidth <= 0 ? 0 : Math.Clamp(x / Timeline.ActualWidth, 0, 1) * _duration;

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void DrawTimeline()
    {
        Timeline.Children.Clear();
        _playhead = null;
        if (!_editing || _duration <= 0 || Timeline.ActualWidth <= 0) return;
        double w = Timeline.ActualWidth;
        const double top = 12, height = 22;
        var gold = Res("Gold", Brushes.Goldenrod);

        void Box(double x0, double x1, Brush fill, double y = top, double h = height, double radius = 0)
        {
            var r = new Rectangle { Width = Math.Max(0, x1 - x0), Height = h, Fill = fill, RadiusX = radius, RadiusY = radius, IsHitTestVisible = false };
            Canvas.SetLeft(r, x0);
            Canvas.SetTop(r, y);
            Timeline.Children.Add(r);
        }

        Box(0, w, Res("c24211E", Brushes.DimGray), radius: 4);
        var keptFill = gold.Clone(); keptFill.Opacity = 0.32; keptFill.Freeze();
        foreach (var (a, b) in KeptParts()) Box(X(a), X(b), keptFill);
        // Cut out: struck through in red
        var cutFill = new SolidColorBrush(Color.FromArgb(0x70, 0xD9, 0x43, 0x3A));
        foreach (var (a, b) in _cuts)
        {
            Box(X(Math.Max(a, 0)), X(Math.Min(b, _duration)), cutFill);
            var strike = new Line { X1 = X(a), X2 = X(b), Y1 = top + height / 2, Y2 = top + height / 2, Stroke = new SolidColorBrush(Color.FromRgb(0xF0, 0x6A, 0x62)), StrokeThickness = 1.5, IsHitTestVisible = false };
            Timeline.Children.Add(strike);
        }
        if (_selection is { } s)
        {
            var r = new Rectangle { Width = Math.Max(1, X(s.B) - X(s.A)), Height = height + 8, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Stroke = Brushes.White, StrokeThickness = 1, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false };
            Canvas.SetLeft(r, X(s.A));
            Canvas.SetTop(r, top - 4);
            Timeline.Children.Add(r);
        }

        // Start and end: gold brackets with a grip, easy to see and to grab
        void Bracket(double x, bool start)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                double arm = start ? 6 : -6;
                c.BeginFigure(new Point(x + arm, top - 6), false, false);
                c.LineTo(new Point(x, top - 6), true, true);
                c.LineTo(new Point(x, top + height + 6), true, true);
                c.LineTo(new Point(x + arm, top + height + 6), true, true);
            }
            g.Freeze();
            Timeline.Children.Add(new Path { Data = g, Stroke = Brushes.Black, StrokeThickness = 6, Opacity = 0.5, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false });
            Timeline.Children.Add(new Path { Data = g, Stroke = gold, StrokeThickness = 3.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false });
        }
        Bracket(X(_in), start: true);
        Bracket(X(_out), start: false);

        _playhead = new Line { Y1 = 2, Y2 = top + height + 10, Stroke = Res("cFBF8F2", Brushes.White), StrokeThickness = 2, IsHitTestVisible = false };
        Timeline.Children.Add(_playhead);
        MovePlayhead();
    }

    private void MovePlayhead()
    {
        if (_playhead == null) return;
        double x = X(Player.Position.TotalSeconds);
        _playhead.X1 = _playhead.X2 = x;
    }

    private TimelineDrag HitTimeline(double x)
    {
        if (Math.Abs(x - X(_in)) <= 10) return TimelineDrag.Start;
        if (Math.Abs(x - X(_out)) <= 10) return TimelineDrag.End;
        return TimelineDrag.Select;
    }

    private void OnTimelineDown(object sender, MouseButtonEventArgs e)
    {
        if (_duration <= 0) return;
        double x = e.GetPosition(Timeline).X;
        _drag = HitTimeline(x);
        _dragFrom = T(x);
        _dragMoved = false;
        if (_drag is TimelineDrag.Start or TimelineDrag.End)
        {
            PushUndo();
            if (_isPlaying) { Player.Pause(); SetPlaying(false); } // scrubbing while playing is a burst of noise per move
        }
        Timeline.CaptureMouse();
        e.Handled = true;
    }

    private void OnTimelineMove(object sender, MouseEventArgs e)
    {
        double x = e.GetPosition(Timeline).X;
        if (_drag == TimelineDrag.None)
        {
            Timeline.Cursor = HitTimeline(x) == TimelineDrag.Select ? Cursors.IBeam : Cursors.SizeWE;
            return;
        }
        double t = T(x);
        if (Math.Abs(t - _dragFrom) * Timeline.ActualWidth / Math.Max(_duration, 0.001) > 3) _dragMoved = true;
        switch (_drag)
        {
            case TimelineDrag.Start:
                _in = Within(t, 0, _out - 0.1);
                Scrub(_in); // the frame at the new start
                break;
            case TimelineDrag.End:
                _out = Within(t, _in + 0.1, _duration);
                Scrub(_out);
                break;
            case TimelineDrag.Select:
                if (_dragMoved) _selection = (Math.Min(_dragFrom, t), Math.Max(_dragFrom, t));
                break;
        }
        Changed(notify: false);
    }

    private void OnTimelineUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == TimelineDrag.None) return;
        Timeline.ReleaseMouseCapture();
        var drag = _drag;
        _drag = TimelineDrag.None;
        if (drag is TimelineDrag.Start or TimelineDrag.End)
        {
            Player.Position = TimeSpan.FromSeconds(drag == TimelineDrag.Start ? _in : _out);
            // Nothing moved: no undo step
            if (_undo.TryPeek(out var before) && Math.Abs(before.In - _in) < 0.001 && Math.Abs(before.Out - _out) < 0.001) _undo.Pop();
            Changed();
            return;
        }
        if (!_dragMoved)
        {
            // A click: on a cut part brings it back, anywhere else moves the playhead there
            double t = _dragFrom;
            int cut = _cuts.FindIndex(c => t >= c.A && t <= c.B);
            if (cut >= 0) { PushUndo(); _cuts.RemoveAt(cut); _selection = null; Changed(); return; }
            _selection = null;
            Player.Position = TimeSpan.FromSeconds(t);
            UpdateProgress();
        }
        Changed(notify: false);
    }

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e) => DrawTimeline();

    private long _jumpedAt;
    private long _scrubbedAt;

    /// <summary>
    /// While editing, playback jumps over what's cut and stops at the end, like the saved file will. After a jump the
    /// player needs a moment before it reports the new position (and lands a little before the target in MP3 and
    /// similar formats): until then it isn't checked again, or it would jump over and over, each jump a short beep.
    /// </summary>
    private void FollowEdit()
    {
        if (!_editing || _duration <= 0) return;
        MovePlayhead();
        if (!_isPlaying || _drag != TimelineDrag.None) return;
        if (Environment.TickCount64 - _jumpedAt < 350) return;
        double t = Player.Position.TotalSeconds;
        if (t < _in - 0.25) { Jump(_in); return; }
        if (t >= _out - 0.02) { Player.Pause(); SetPlaying(false); Player.Position = TimeSpan.FromSeconds(_out); UpdateProgress(); return; }
        foreach (var (a, b) in _cuts)
            if (t >= a && t < b - 0.25) { Jump(Math.Min(b + 0.05, _out)); return; }
    }

    private void Jump(double t)
    {
        _jumpedAt = Environment.TickCount64;
        Player.Position = TimeSpan.FromSeconds(t);
    }

    /// <summary>The frame under a dragged handle, a few times a second at most (every seek makes the player work).</summary>
    private void Scrub(double t)
    {
        if (Environment.TickCount64 - _scrubbedAt < 80) return;
        _scrubbedAt = Environment.TickCount64;
        Player.Position = TimeSpan.FromSeconds(t);
    }

    private void OnSetStartClicked(object sender, RoutedEventArgs e) => SetStart(Player.Position.TotalSeconds);
    private void OnSetEndClicked(object sender, RoutedEventArgs e) => SetEnd(Player.Position.TotalSeconds);
    private void OnCutClicked(object sender, RoutedEventArgs e) => CutSelection();
    private void OnUndoCutClicked(object sender, RoutedEventArgs e) => Undo();

    public bool HandleEditKey(Key key, ModifierKeys mods)
    {
        if (key == Key.Z && mods == ModifierKeys.Control) { if (_undo.Count == 0) return false; Undo(); return true; }
        if (mods != ModifierKeys.None && mods != ModifierKeys.Shift) return false;
        double step = mods == ModifierKeys.Shift ? 0.1 : 1;
        switch (key)
        {
            case Key.I: SetStart(Player.Position.TotalSeconds); return true;
            case Key.O: SetEnd(Player.Position.TotalSeconds); return true;
            case Key.Delete: case Key.Back: case Key.X: CutSelection(); return true;
            case Key.Space: case Key.K: TogglePlay(); return true;
            case Key.J: SeekBy(-5); return true;
            case Key.L: SeekBy(5); return true;
            case Key.Left: SeekBy(-step); return true;
            case Key.Right: SeekBy(step); return true;
            case Key.Home: Player.Position = TimeSpan.FromSeconds(_in); UpdateProgress(); return true;
            case Key.End: Player.Position = TimeSpan.FromSeconds(_out); UpdateProgress(); return true;
        }
        return false;
    }

    // ---------- Saving ----------

    private CancellationTokenSource? _saveCts;

    public Task SaveToAsync(string path)
    {
        var keep = KeptParts();
        Player.Pause();
        SetPlaying(false);
        _saveCts = new CancellationTokenSource();
        var progress = new Progress<double>(p => EditInfoText.Text = Loc.T("mediaEdit.saving", p.ToString("P0", Loc.Instance.Culture)));
        return MediaCut.ExportAsync(_sourcePath, path, keep, progress, _saveCts.Token);
    }

    public void ReleaseFile() { } // playback uses a private copy; the original isn't held open

    public async Task ReloadAsync(string path)
    {
        await LoadAsync(path);
        Player.Pause();
        SetPlaying(false);
        if (_editing) { EditStrip.Visibility = Visibility.Visible; ResetEdit(); }
        DirtyChanged?.Invoke();
    }
}
