using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Lupik.Core;

namespace Lupik;

/// <summary>Images: the filmstrip of the folder's pictures.</summary>
public partial class MainWindow
{
    private void WireImageExtras()
    {
        ImageViewerControl.FilmstripPicked += path => _ = ShowFile(path);
        ImageViewerControl.FilmstripToggled += () =>
        {
            if (!_isFullScreen && ImageViewerControl.Visibility == Visibility.Visible)
                SetBounds(ComputeImageBounds(ImageViewerControl.NaturalWidth, ImageViewerControl.NaturalHeight));
        };
    }

    /// <summary>
    /// The images to flip through with the shown one: the Explorer multi-selection if it came from one,
    /// otherwise every image in its folder, in Explorer's name order.
    /// </summary>
    private IReadOnlyList<string> ImageSiblings(string path)
    {
        try
        {
            if (_selectionSet.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                return _selectionSet.Where(IsImagePath).ToList();
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new[] { path };
            return ExplorerService.FolderFiles(dir).Where(IsImagePath).ToList();
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Listing the folder's images failed: {ex.Message}");
            return new[] { path };
        }
    }
}
