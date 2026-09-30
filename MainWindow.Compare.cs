using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lupik.Core;

using Lupik.Localization;

namespace Lupik;

/// <summary>Comparing two selected images, or two text files (a diff).</summary>
public partial class MainWindow
{
    private void CaptureSelection() => _selectionSet = ExplorerService.LastSelection.Count > 1
        ? ExplorerService.LastSelection
        : Array.Empty<string>();

    private static bool IsImagePath(string path) => Array.IndexOf(ImageExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>Exactly two images selected in Explorer → the pair to compare.</summary>
    private (string, string)? ComparePair() =>
        _selectionSet.Count == 2 && IsImagePath(_selectionSet[0]) && IsImagePath(_selectionSet[1]) && File.Exists(_selectionSet[0]) && File.Exists(_selectionSet[1])
            ? (_selectionSet[0], _selectionSet[1])
            : null;

    /// <summary>Exactly two text files (code, text, config...) selected in Explorer → the pair to diff.</summary>
    private (string, string)? DiffPair() =>
        _selectionSet.Count == 2 && Views.DiffViewer.IsText(_selectionSet[0], CodeExtensions) && Views.DiffViewer.IsText(_selectionSet[1], CodeExtensions) &&
        File.Exists(_selectionSet[0]) && File.Exists(_selectionSet[1])
            ? (_selectionSet[0], _selectionSet[1])
            : null;

    /// <summary>Opening from Explorer: two selected images open side by side, two text files as a diff, anything else as usual.</summary>
    private void ShowSelection(string selected)
    {
        if (ComparePair() is var (a, b)) _ = ShowCompare(a, b);
        else if (DiffPair() is var (x, y)) _ = ShowDiff(x, y);
        else _ = ShowFile(selected);
    }

    /// <summary>C: side by side ⇄ one file.</summary>
    private void ToggleCompare()
    {
        if (CompareViewerControl.Visibility == Visibility.Visible)
            _ = ShowFile(CompareViewerControl.PathA);
        else if (DiffViewerControl.Visibility == Visibility.Visible)
            _ = ShowFile(DiffViewerControl.PathA);
        else if (ComparePair() is var (a, b))
            _ = ShowCompare(a, b);
        else if (DiffPair() is var (x, y))
            _ = ShowDiff(x, y);
    }

    /// <summary>Whether C (compare) does something now.</summary>
    private bool CanToggleCompare() =>
        CompareViewerControl.Visibility == Visibility.Visible || DiffViewerControl.Visibility == Visibility.Visible ||
        (ImageViewerControl.Visibility == Visibility.Visible && ComparePair() != null) ||
        (CodeViewerControl.Visibility == Visibility.Visible && DiffPair() != null);

    public async Task ShowDiff(string a, string b)
    {
        App.Log($"[MainWindow] ShowDiff: '{a}' vs '{b}'");
        CancelIdleRelease();
        CloseSearch();
        int token = ++_showToken;
        BeginLoading(a, Path.GetExtension(a).ToLowerInvariant(), token);
        try
        {
            bool loaded;
            try { loaded = await DiffViewerControl.LoadAsync(a, b); }
            catch (Exception ex)
            {
                App.Log($"[MainWindow] Diff failed ({ex.Message}), showing the first file");
                if (token == _showToken) _ = ShowFile(a);
                return;
            }
            if (!loaded || token != _showToken) return;

            ApplyFileHeader(a);
            TitleFileNameText.Text = $"{Path.GetFileName(a)}  ⇄  {Path.GetFileName(b)}";
            Title = Loc.T("diff.windowTitle");
            FileActions.Visibility = SaveAsButton.Visibility = CropButton.Visibility = Visibility.Collapsed;
            ShowOnlyViewer(DiffViewerControl);
            SetBounds(ComputeCompareBounds());
            BringToFront();
        }
        finally
        {
            if (token == _showToken) EndLoading();
        }
    }

    public async Task ShowCompare(string a, string b)
    {
        App.Log($"[MainWindow] ShowCompare: '{a}' vs '{b}'");
        CancelIdleRelease();
        CloseSearch();
        int token = ++_showToken;
        BeginLoading(a, Path.GetExtension(a).ToLowerInvariant(), token);
        try
        {
            bool loaded;
            try { loaded = await CompareViewerControl.LoadAsync(a, b, ImageViewerControl.GetDecodeTask); }
            catch (Exception ex)
            {
                App.Log($"[MainWindow] Compare failed ({ex.Message}), showing the first image");
                if (token == _showToken) _ = ShowFile(a);
                return;
            }
            if (!loaded || token != _showToken) return;

            ApplyFileHeader(a);
            TitleFileNameText.Text = $"{Path.GetFileName(a)}  ⇄  {Path.GetFileName(b)}";
            Title = Loc.T("compare.windowTitle");
            FileActions.Visibility = SaveAsButton.Visibility = CropButton.Visibility = Visibility.Collapsed; // which file would they act on?
            ShowOnlyViewer(CompareViewerControl);
            SetBounds(ComputeCompareBounds());
            BringToFront();
        }
        finally
        {
            if (token == _showToken) EndLoading();
        }
    }

    /// <summary>Compare needs room for two images: a big window.</summary>
    private Rect ComputeCompareBounds()
    {
        if (!TryGetWorkArea(out var work, out _, out _)) return new Rect(Left, Top, Width, Height);
        return Centered(work, Math.Min(1725, work.Width * 0.97), Math.Min(1035, work.Height * 0.97));
    }
}
