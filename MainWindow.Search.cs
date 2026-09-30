using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lupik.Localization;

namespace Lupik;

/// <summary>
/// Search in the preview (Ctrl+F): code and text, CSV / Excel tables, Word documents and PDFs.
/// The preview never has the keyboard focus, so while the bar is open the keyboard hook sends it every key as text.
/// </summary>
public partial class MainWindow
{
    /// <summary>The search bar is open: the hook routes all typing here (read by the hook thread).</summary>
    public volatile bool SearchActive;

    /// <summary>The current preview can be searched: Ctrl+F / F3 are routed (read by the hook thread).</summary>
    public volatile bool SearchableInPreview;

    private string _query = "";
    private int _matchCount, _matchIndex = -1;
    private CancellationTokenSource? _searchCts;
    private DispatcherTimer? _searchDebounce;
    private Storyboard? _caretBlink;

    /// <summary>The shown viewer, if it supports search.</summary>
    private Views.ISearchable? CurrentSearchable()
    {
        foreach (UIElement viewer in new UIElement[] { CodeViewerControl, CsvViewerControl, DocxViewerControl, PdfViewerControl, DiffViewerControl })
            if (viewer.Visibility == Visibility.Visible && viewer is Views.ISearchable s) return s;
        return null;
    }

    private void UpdateSearchable() => SearchableInPreview = CurrentSearchable() != null;

    private void OpenSearch()
    {
        if (CurrentSearchable() == null) return;
        SearchBar.Visibility = Visibility.Visible;
        SearchActive = true;
        _caretBlink ??= CreateCaretBlink();
        _caretBlink.Begin(this, true);
        RenderQuery();
        if (_query.Length > 0) RunSearch(); // reopened: the last query again, on whatever is shown now
    }

    /// <summary>Hides the bar and the highlights (Esc, another file, closing the preview).</summary>
    private void CloseSearch()
    {
        if (!SearchActive && SearchBar.Visibility != Visibility.Visible) return;
        SearchActive = false;
        SearchBar.Visibility = Visibility.Collapsed;
        _caretBlink?.Stop(this);
        _searchCts?.Cancel();
        _searchDebounce?.Stop();
        foreach (var viewer in new Views.ISearchable[] { CodeViewerControl, CsvViewerControl, DocxViewerControl, PdfViewerControl, DiffViewerControl })
            viewer.ClearSearch();
        _matchCount = 0;
        _matchIndex = -1;
    }

    /// <summary>A key while the bar is open (from the hook): text, editing, next/previous, Esc.</summary>
    public void HandleSearchKey(int vk, ModifierKeys mods, string? text)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        bool shift = (mods & ModifierKeys.Shift) != 0;
        bool ctrl = (mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Alt) == 0;

        if (key == Key.Escape) { CloseSearch(); return; }
        if (key is Key.Enter or Key.F3) { Step(shift ? -1 : 1); return; }
        if (key == Key.Down) { Step(1); return; }
        if (key == Key.Up) { Step(-1); return; }
        if (key == Key.Back)
        {
            if (_query.Length == 0) return;
            _query = ctrl ? TrimLastWord(_query) : _query[..^1];
            QueryChanged();
            return;
        }
        if (ctrl && key == Key.V)
        {
            try
            {
                string pasted = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                int newline = pasted.IndexOfAny(new[] { '\r', '\n' });
                if (newline >= 0) pasted = pasted[..newline];
                if (pasted.Length > 0) { _query += pasted; QueryChanged(); }
            }
            catch (Exception ex) { App.Log($"[Search] Paste failed: {ex.Message}"); }
            return;
        }
        if (ctrl && key == Key.F) return; // already open
        if (ctrl || string.IsNullOrEmpty(text) || char.IsControl(text[0])) return;
        _query += text;
        QueryChanged();
    }

    private static string TrimLastWord(string s)
    {
        int end = s.Length;
        while (end > 0 && char.IsWhiteSpace(s[end - 1])) end--;
        while (end > 0 && !char.IsWhiteSpace(s[end - 1])) end--;
        return s[..end];
    }

    private void QueryChanged()
    {
        RenderQuery();
        // Search after a short pause in typing: a PDF search reads every page
        _searchDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(160), DispatcherPriority.Input, (_, _) =>
        {
            _searchDebounce!.Stop();
            RunSearch();
        }, Dispatcher);
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void RenderQuery()
    {
        SearchText.Text = _query;
        SearchPlaceholder.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderCount();
    }

    private void RenderCount()
    {
        SearchCount.Text = _query.Length == 0 ? ""
            : _matchCount == 0 ? Loc.T("search.none")
            : Loc.T("search.count", _matchIndex + 1, _matchCount);
    }

    private async void RunSearch()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var target = CurrentSearchable();
        if (target == null) return;

        int count;
        try
        {
            count = await target.SearchAsync(_query, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            App.Log($"[Search] Failed: {ex.Message}");
            count = 0;
        }
        if (cts.IsCancellationRequested) return;

        _matchCount = count;
        _matchIndex = count > 0 ? 0 : -1;
        if (count > 0) target.ShowMatch(0);
        RenderCount();
    }

    private void Step(int direction)
    {
        if (_matchCount == 0) return;
        _matchIndex = ((_matchIndex + direction) % _matchCount + _matchCount) % _matchCount;
        CurrentSearchable()?.ShowMatch(_matchIndex);
        RenderCount();
    }

    private void OnSearchPrevClicked(object sender, RoutedEventArgs e) => Step(-1);
    private void OnSearchNextClicked(object sender, RoutedEventArgs e) => Step(1);
    private void OnSearchCloseClicked(object sender, RoutedEventArgs e) => CloseSearch();

    private Storyboard CreateCaretBlink()
    {
        var blink = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(1.06) };
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.53))));
        Storyboard.SetTargetName(blink, nameof(SearchCaret));
        Storyboard.SetTargetProperty(blink, new PropertyPath(OpacityProperty));
        var sb = new Storyboard();
        sb.Children.Add(blink);
        return sb;
    }
}
