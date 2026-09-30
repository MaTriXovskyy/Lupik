using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lupik.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SharpCompress.Archives;

using Lupik.Localization;
namespace Lupik.Views;

/// <summary>One visible row of the archive listing. Key = its path inside the archive ("folder/file.txt").</summary>
public sealed record ArchiveEntryRow(string Name, string Key, bool IsFolder, Brush NameBrush, TreePosition Tree, string SizeText, string DateText)
{
    public ImageSource? Icon { get; init; }
}

/// <summary>
/// Lists what's inside ZIP / RAR / 7z / TAR / GZ archives without extracting anything.
/// </summary>
public partial class ArchiveViewer : UserControl
{
    public static readonly string[] Extensions = { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz" };

    private static Brush NameBrush => Core.Palette.Brush(0xECE6DC);
    private static Brush LockedBrush => Core.Palette.Brush(0xF38BA8);

    private int _loadToken;

    private sealed class Node
    {
        public string Name = "";
        public string Key = "";
        public bool IsDirectory;
        public bool IsEncrypted;
        public long Size;
        public DateTime? Modified;
        public readonly Dictionary<string, Node> Children = new(StringComparer.OrdinalIgnoreCase);
    }

    public ArchiveViewer()
    {
        InitializeComponent();
    }

    /// <summary>Drops the listing (called when Lupik goes idle).</summary>
    public void Release()
    {
        _loadToken++;
        _archive = null;
        EntriesList.ItemsSource = null;
    }

    /// <summary>Reads the archive index in the background. Returns false if superseded by a newer load.</summary>
    public async Task<bool> LoadArchiveAsync(string filePath)
    {
        // Same archive, unchanged, still listed: coming back from a peek, keep the list (and its scroll position)
        var stamp = File.GetLastWriteTimeUtc(filePath);
        if (filePath == _archive && stamp == _loadedStamp && EntriesList.ItemsSource != null) return true;
        int token = ++_loadToken;
        _archive = filePath;
        _loadedStamp = stamp;
        if (_extractCts == null) ResetExtractUi(); // a running extraction keeps going and reports when done
        FormatText.Text = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

        try
        {
            var (rows, files, folders, unpacked, encrypted) = await Task.Run(() => ReadArchive(filePath));
            if (token != _loadToken) return false;

            // Windows' icons are looked up here, on the UI thread (cached per extension)
            EntriesList.ItemsSource = rows.Select(r => r with { Icon = ShellIcons.For(r.Name, r.IsFolder) }).ToList();
            SummaryText.Text = $"{Loc.Plural("count.files", files)}  •  {Loc.Plural("count.folders", folders)}" +
                               "  •  " + Loc.T("archive.unpackedSize", FormatFileSize(unpacked)) +
                               (encrypted ? "  •  " + Loc.T("archive.encrypted") : "");
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return false;
            App.Log($"[ArchiveViewer] Failed to read '{filePath}': {ex}");
            EntriesList.ItemsSource = null;
            SummaryText.Text = Loc.T("archive.readError", ex.Message);
        }
        return true;
    }

    private static (List<ArchiveEntryRow>, int, int, long, bool) ReadArchive(string filePath)
    {
        var root = new Node { IsDirectory = true };
        int files = 0;
        long unpacked = 0;
        bool anyEncrypted = false;

        foreach (var entry in EnumerateEntries(filePath))
        {
            {
                string key = ArchiveExtractor.NormalizeKey(filePath, entry.Key);
                if (key.Length == 0) continue;

                // Walk/create the folder chain; archives often omit explicit folder entries
                string[] parts = key.Split('/');
                var node = root;
                for (int i = 0; i < parts.Length; i++)
                {
                    bool last = i == parts.Length - 1;
                    if (!node.Children.TryGetValue(parts[i], out var child))
                    {
                        child = new Node { Name = parts[i], Key = string.Join('/', parts, 0, i + 1), IsDirectory = !last || entry.IsDirectory };
                        node.Children[parts[i]] = child;
                    }
                    node = child;
                }

                if (!entry.IsDirectory)
                {
                    node.Size = entry.Size;
                    node.Modified = entry.LastModifiedTime;
                    node.IsEncrypted = entry.IsEncrypted;
                    anyEncrypted |= entry.IsEncrypted;
                    unpacked += entry.Size;
                    files++;
                }
            }
        }

        var rows = new List<ArchiveEntryRow>();
        int folders = 0;
        Flatten(root, 0, rows, ref folders, new List<bool>());
        return (rows, files, folders, unpacked, anyEncrypted);
    }

    /// <summary>
    /// Entries via the random-access archive API; compressed tarballs (.tar.gz/.tgz/.tar.xz) aren't supported
    /// there, so fall back to one streaming pass that skips over the file data.
    /// </summary>
    private static IEnumerable<SharpCompress.Common.IEntry> EnumerateEntries(string filePath)
    {
        IArchive? archive = null;
        try { archive = ArchiveFactory.OpenArchive(filePath, ArchiveExtractor.ReaderOptions()); }
        catch (SharpCompress.Common.ArchiveOperationException) { }

        if (archive != null)
        {
            using (archive)
                foreach (var entry in archive.Entries) yield return entry;
            yield break;
        }

        using var stream = File.OpenRead(filePath);
        using var reader = SharpCompress.Readers.ReaderFactory.OpenReader(stream, ArchiveExtractor.ReaderOptions());
        while (reader.MoveToNextEntry()) yield return reader.Entry;
    }

    /// <summary>
    /// Folders first, then files, each alphabetically; children under their folder, with tree guide lines.
    /// <paramref name="lines"/>: for each outer level, whether its folder still has items after this branch.
    /// </summary>
    private static long Flatten(Node folder, int depth, List<ArchiveEntryRow> rows, ref int folders, List<bool> lines)
    {
        long total = 0;
        var ordered = folder.Children.Values
            .OrderByDescending(n => n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            var node = ordered[i];
            bool isLast = i == ordered.Count - 1;
            var indent = new TreePosition(depth, lines.ToArray(), isLast);
            if (node.IsDirectory)
            {
                folders++;
                int index = rows.Count;
                rows.Add(null!); // placeholder: size is known only after the children
                // Its children draw a line through this level unless this folder was the last one here
                var childLines = new List<bool>(lines);
                if (depth > 0) childLines.Add(!isLast);
                long size = Flatten(node, depth + 1, rows, ref folders, childLines);
                rows[index] = new ArchiveEntryRow(node.Name, node.Key, true, NameBrush, indent, FormatFileSize(size), "");
                total += size;
            }
            else
            {
                rows.Add(new ArchiveEntryRow(node.Name, node.Key, false, node.IsEncrypted ? LockedBrush : NameBrush, indent,
                    FormatFileSize(node.Size), node.Modified?.ToString("yyyy-MM-dd HH:mm") ?? ""));
                total += node.Size;
            }
        }
        return total;
    }

    // ---------- Extract ----------

    private string? _archive;
    private DateTime _loadedStamp;
    private string? _extractResult;
    private CancellationTokenSource? _extractCts;

    private async void OnExtractClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_archive) || _extractCts != null) return;

        string archive = _archive;
        _extractCts = new CancellationTokenSource();
        var cts = _extractCts;
        var token = cts.Token;
        string? temp = null;

        ExtractButton.Visibility = Visibility.Collapsed;
        ExtractDonePanel.Visibility = Visibility.Collapsed;
        ExtractProgressPanel.Visibility = Visibility.Visible;
        ExtractProgressFill.Width = 0;
        ExtractProgressText.Text = Loc.T("common.preparing");

        var progress = new Progress<(long done, long total, int files)>(p =>
        {
            if (_extractCts == null) return; // late report after finishing
            double fraction = p.total > 0 ? Math.Min(1, (double)p.done / p.total) : 1;
            ExtractProgressFill.Width = 160 * fraction;
            ExtractProgressText.Text = $"{fraction.ToString("P0", Loc.Instance.Culture)}  •  {Loc.Plural("count.files", p.files)}";
        });

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string target = await ArchiveExtractor.ExtractAsync(archive, progress, token,
                t => PendingCleanup.Register(temp = t, cts));
            _extractResult = target;
            App.Log($"[ArchiveViewer] Extracted '{archive}' -> '{target}' in {sw.ElapsedMilliseconds} ms");

            ExtractDoneText.Text = Path.GetFileName(target);
            ExtractProgressPanel.Visibility = Visibility.Collapsed;
            ExtractDonePanel.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            ResetExtractUi();
        }
        catch (Exception ex)
        {
            App.Log($"[ArchiveViewer] Extract failed: {ex}");
            ResetExtractUi();
            if (!MainWindow.SuppressActivationForTests) // tests: log only, a message box would grab focus
                MessageCard.Show(Window.GetWindow(this)!, Loc.T("archive.extractError", ex.Message));
        }
        finally
        {
            if (temp != null) PendingCleanup.Unregister(temp);
            _extractCts = null;
        }
    }

    private void OnExtractCancel(object sender, RoutedEventArgs e) => _extractCts?.Cancel();

    private void OnExtractReveal(object sender, RoutedEventArgs e)
    {
        if (_extractResult != null && (Directory.Exists(_extractResult) || File.Exists(_extractResult)))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_extractResult}\"") { UseShellExecute = true });
    }

    // ---------- Single entries: extract one, drag out, hold to peek ----------

    /// <summary>A file (or folder) from the archive, extracted to a temp folder, to show while the mouse button is held.</summary>
    public event Action<string>? PeekRequested;

    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "Lupik", "archive");

    /// <summary>Leftovers from earlier runs (peeks and drags).</summary>
    public static void CleanTemp()
    {
        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true); }
        catch { /* a file may still be open somewhere: next time */ }
    }

    private async void OnExtractOneClicked(object sender, RoutedEventArgs e)
    {
        if (_archive == null || (sender as FrameworkElement)?.DataContext is not ArchiveEntryRow row) return;
        e.Handled = true;
        try
        {
            string output = await ArchiveExtractor.ExtractPartAsync(_archive, row.Key, row.IsFolder, Path.GetDirectoryName(_archive)!);
            App.Log($"[ArchiveViewer] Extracted '{row.Key}' -> '{output}'");
            _extractResult = output;
            ExtractDoneText.Text = Path.GetFileName(output);
            ExtractButton.Visibility = Visibility.Collapsed;
            ExtractProgressPanel.Visibility = Visibility.Collapsed;
            ExtractDonePanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void ShowError(Exception ex)
    {
        App.Log($"[ArchiveViewer] Single extract failed: {ex}");
        if (!MainWindow.SuppressActivationForTests)
            MessageCard.Show(Window.GetWindow(this)!, Loc.T("archive.extractError", ex.Message));
    }

    /// <summary>Extracted copy in the temp folder (reused while the archive doesn't change).</summary>
    private static async Task<string> ExtractToTempAsync(string archive, ArchiveEntryRow row)
    {
        string id = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{archive}|{File.GetLastWriteTimeUtc(archive).Ticks}|{row.Key}")))[..16];
        string dir = Path.Combine(TempRoot, id);
        string path = Path.Combine(dir, row.Name);
        if (File.Exists(path) || Directory.Exists(path)) return path;
        if (Directory.Exists(dir)) Directory.Delete(dir, true); // half-written earlier
        return await ArchiveExtractor.ExtractPartAsync(archive, row.Key, row.IsFolder, dir);
    }

    private static bool LeftButtonDown => System.Windows.Forms.Control.MouseButtons.HasFlag(System.Windows.Forms.MouseButtons.Left);

    private ArchiveEntryRow? _pressedRow;
    private Point _pressPoint;
    private System.Windows.Threading.DispatcherTimer? _holdTimer;
    private bool _busy;

    private static ArchiveEntryRow? RowAt(object source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is Button) return null; // the row's own button
            if (d is ListBoxItem { DataContext: ArchiveEntryRow row }) return row;
        }
        return null;
    }

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedRow = RowAt(e.OriginalSource);
        if (_pressedRow == null || _archive == null || _busy) return;
        _pressPoint = e.GetPosition(EntriesList);

        // Folders can be dragged out, but not peeked at: their contents are already listed right here
        if (_pressedRow.IsFolder) return;

        // Held still for a moment: peek at it
        _holdTimer?.Stop();
        _holdTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _holdTimer.Tick += async (_, _) =>
        {
            _holdTimer?.Stop();
            var row = _pressedRow;
            if (row == null || _archive == null) return;
            await PeekAsync(_archive, row);
        };
        _holdTimer.Start();
    }

    private async Task PeekAsync(string archive, ArchiveEntryRow row)
    {
        _busy = true;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            string path = await ExtractToTempAsync(archive, row);
            // Only while the button is still held (released during a slow extraction = changed their mind)
            if (LeftButtonDown && archive == _archive)
            {
                App.Log($"[ArchiveViewer] Peek '{row.Key}'");
                PeekRequested?.Invoke(path);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _busy = false;
            _pressedRow = null;
        }
    }

    private async void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedRow == null || _archive == null || _busy || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(EntriesList) - _pressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // Dragged: out of the archive, into Explorer / the desktop (like WinRAR)
        _holdTimer?.Stop();
        var row = _pressedRow;
        _pressedRow = null;
        _busy = true;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            string path = await ExtractToTempAsync(_archive, row);
            Mouse.OverrideCursor = null;
            if (!LeftButtonDown) return;
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            App.Log($"[ArchiveViewer] Dragging '{row.Key}' out");
            DragDrop.DoDragDrop(EntriesList, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _busy = false;
        }
    }

    private void OnRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        _holdTimer?.Stop();
        if (!_busy) _pressedRow = null;
    }

    private void ResetExtractUi()
    {
        ExtractButton.Visibility = Visibility.Visible;
        ExtractProgressPanel.Visibility = Visibility.Collapsed;
        ExtractDonePanel.Visibility = Visibility.Collapsed;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
