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

/// <summary>Comparing two selected images.</summary>
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

    /// <summary>Opening from Explorer: two selected images open side by side, anything else as usual.</summary>
    private void ShowSelection(string selected)
    {
        if (ComparePair() is var (a, b)) _ = ShowCompare(a, b);
        else _ = ShowFile(selected);
    }

    private void ToggleCompare()
    {
        if (CompareViewerControl.Visibility == Visibility.Visible)
            _ = ShowFile(CompareViewerControl.PathA);
        else if (ComparePair() is var (a, b))
            _ = ShowCompare(a, b);
    }

    public async Task ShowCompare(string a, string b)
    {
        App.Log($"[MainWindow] ShowCompare: '{a}' vs '{b}'");
        CancelIdleRelease();
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
