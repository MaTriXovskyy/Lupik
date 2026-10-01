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

/// <summary>Moving around: back/forward between a folder (or archive) and what was opened from it, previous/next file.</summary>
public partial class MainWindow
{
    // --- Folder navigation: files/subfolders opened from the folder preview can go back to it ---

    private readonly Stack<string> _folderHistory = new();  // "back": folders we came from
    private readonly Stack<string> _forwardHistory = new(); // "forward": what we went back from

    private void OpenFromFolder(string path)
    {
        _folderHistory.Push(FolderViewerControl.FolderPath);
        _forwardHistory.Clear(); // a new path, like following a link in a browser
        _ = ShowFile(path);
    }

    private void GoBackToFolder()
    {
        if (_folderHistory.Count == 0) return;
        _forwardHistory.Push(_currentFilePath);
        _ = ShowFile(_folderHistory.Pop());
    }

    private void GoForward()
    {
        if (_forwardHistory.Count == 0 || !Directory.Exists(_currentFilePath)) return;
        _folderHistory.Push(_currentFilePath);
        _ = ShowFile(_forwardHistory.Pop());
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => GoBackToFolder();

    /// <summary>Mouse4 (back) / Mouse5 (forward), like in Explorer and browsers.</summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { GoBackToFolder(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { GoForward(); e.Handled = true; }
    }

    /// <summary>Started from Explorer (not from inside a folder preview): no way "back".</summary>
    private void ResetFolderHistory()
    {
        _folderHistory.Clear();
        _forwardHistory.Clear();
    }

    private string? GetNeighbor(string path, int direction)
    {
        int index = -1;
        for (int i = 0; i < _selectionSet.Count; i++)
        {
            if (string.Equals(_selectionSet[i], path, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        }

        if (index >= 0)
        {
            int count = _selectionSet.Count;
            return _selectionSet[((index + direction) % count + count) % count];
        }
        return ExplorerService.GetAdjacentFile(path, direction);
    }

    private void NavigateAdjacent(int direction)
    {
        if (!string.IsNullOrEmpty(_currentFilePath))
        {
            string? adjacent = GetNeighbor(_currentFilePath, direction);
            if (!string.IsNullOrEmpty(adjacent) && adjacent != _currentFilePath)
            {
                _ = ShowFile(adjacent);
            }
        }
    }

    /// <summary>Decodes the previous and next image in the background, so arrow keys show them instantly.</summary>
    private void PreloadNeighbors(string path)
    {
        foreach (int direction in new[] { 1, -1 })
        {
            string? neighbor = GetNeighbor(path, direction);
            if (neighbor != null && neighbor != path &&
                Array.IndexOf(ImageExtensions, Path.GetExtension(neighbor).ToLowerInvariant()) >= 0)
            {
                ImageViewerControl.Preload(neighbor);
            }
        }
    }
}
