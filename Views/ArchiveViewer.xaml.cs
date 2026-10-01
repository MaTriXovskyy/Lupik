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

    // ---------- Entries: select, open, extract, drag out, copy, peek ----------
    // Like Explorer / WinRAR: click selects (Ctrl / Shift add to it), double-click opens the file in its app,
    // dragging takes the selection out, holding the right button shows a small preview next to the cursor.

    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "Lupik", "archive");

    /// <summary>Leftovers from earlier runs (opened, copied and dragged files).</summary>
    public static void CleanTemp()
    {
        try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, true); }
        catch { /* a file may still be open somewhere: next time */ }
    }

    /// <summary>Something is selected (Esc clears it before closing the preview).</summary>
    public bool HasSelection => EntriesList.SelectedItems.Count > 0;

    public void SelectAll() => EntriesList.SelectAll();

    public void ClearSelection() => EntriesList.UnselectAll();

    private void OnClearSelectionClicked(object sender, RoutedEventArgs e) => ClearSelection();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int files = 0, folders = 0;
        foreach (ArchiveEntryRow row in EntriesList.SelectedItems)
            if (row.IsFolder) folders++; else files++;
        SelectionPanel.Visibility = files + folders > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExtractButton.Visibility = files + folders > 0 || _extractCts != null || ExtractDonePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        var parts = new List<string>();
        if (files > 0) parts.Add(Loc.Plural("count.files", files));
        if (folders > 0) parts.Add(Loc.Plural("count.folders", folders));
        SelectionText.Text = Loc.T("archive.selected", string.Join(", ", parts));
    }

    /// <summary>The selection in list order, without entries already inside a selected folder.</summary>
    private List<ArchiveEntryRow> SelectedRows()
    {
        var rows = EntriesList.Items.Cast<ArchiveEntryRow>().Where(r => EntriesList.SelectedItems.Contains(r)).ToList();
        var folders = rows.Where(r => r.IsFolder).Select(r => r.Key + "/").ToList();
        return rows.Where(r => !folders.Any(f => r.Key.StartsWith(f, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // --- Extract the selection

    private async void OnExtractSelectedClicked(object sender, RoutedEventArgs e) => await ExtractRowsAsync(SelectedRows());

    private async void OnExtractOneClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ArchiveEntryRow row) return;
        e.Handled = true;
        await ExtractRowsAsync(new List<ArchiveEntryRow> { row });
    }

    /// <summary>
    /// One entry goes next to the archive; several go into a new folder named after the archive, so they don't get
    /// scattered among the files that are already there.
    /// </summary>
    private async Task ExtractRowsAsync(List<ArchiveEntryRow> rows)
    {
        if (_archive == null || rows.Count == 0 || _busy) return;
        string archive = _archive;
        _busy = true;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            string dest = Path.GetDirectoryName(archive)!;
            if (rows.Count > 1)
            {
                dest = ArchiveExtractor.UniqueFilePath(Path.Combine(dest, Path.GetFileNameWithoutExtension(archive)));
                Directory.CreateDirectory(dest);
            }
            string output = dest;
            foreach (var row in rows)
            {
                string one = await ArchiveExtractor.ExtractPartAsync(archive, row.Key, row.IsFolder, dest);
                if (rows.Count == 1) output = one;
            }
            App.Log($"[ArchiveViewer] Extracted {rows.Count} entries -> '{output}'");
            _extractResult = output;
            ExtractDoneText.Text = rows.Count == 1 ? Path.GetFileName(output) : Loc.T("archive.extractedCount", Path.GetFileName(output), rows.Count);
            ClearSelection();
            ExtractButton.Visibility = Visibility.Collapsed;
            ExtractProgressPanel.Visibility = Visibility.Collapsed;
            ExtractDonePanel.Visibility = Visibility.Visible;
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

    private void ShowError(Exception ex)
    {
        App.Log($"[ArchiveViewer] Extract failed: {ex}");
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

    private async Task<string[]> ExtractToTempAsync(string archive, IEnumerable<ArchiveEntryRow> rows)
    {
        var paths = new List<string>();
        foreach (var row in rows) paths.Add(await ExtractToTempAsync(archive, row));
        return paths.ToArray();
    }

    // --- Copy (Ctrl+C): the selected files, ready to paste in Explorer

    public async void CopySelection()
    {
        var rows = SelectedRows();
        if (_archive == null || rows.Count == 0 || _busy) return;
        _busy = true;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var paths = await ExtractToTempAsync(_archive, rows);
            var files = new System.Collections.Specialized.StringCollection();
            files.AddRange(paths);
            var data = new DataObject();
            data.SetFileDropList(files);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
            Clipboard.SetDataObject(data, copy: true);
            App.Log($"[ArchiveViewer] Copied {paths.Length} entries to the clipboard");
            Copied?.Invoke(rows.Count);
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

    /// <summary>Entries copied to the clipboard (for a short notice).</summary>
    public event Action<int>? Copied;

    // --- Mouse: select, double-click opens, drag takes the selection out

    private static bool LeftButtonDown => System.Windows.Forms.Control.MouseButtons.HasFlag(System.Windows.Forms.MouseButtons.Left);
    private static bool RightButtonDown => System.Windows.Forms.Control.MouseButtons.HasFlag(System.Windows.Forms.MouseButtons.Right);

    private ArchiveEntryRow? _pressedRow;
    private Point _pressPoint;
    private bool _busy;
    // A plain click on a row that's part of a bigger selection: keep the selection (it may be dragged), and
    // narrow it down to that row on release if it wasn't
    private bool _narrowOnRelease;

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
        _narrowOnRelease = false;
        // A click on the empty space below the rows clears the selection (not on the scrollbar)
        if (_pressedRow == null && e.OriginalSource is DependencyObject source && !IsInside<System.Windows.Controls.Primitives.ScrollBar>(source)
            && !IsInside<Button>(source))
            ClearSelection();
        if (_pressedRow == null || _archive == null || _busy) return;
        _pressPoint = e.GetPosition(EntriesList);

        if (e.ClickCount == 2)
        {
            e.Handled = true;
            if (!_pressedRow.IsFolder) _ = OpenAsync(_archive, _pressedRow);
            _pressedRow = null;
            return;
        }

        // Selection is done here, not by the ListBox: the preview never has the keyboard focus, so WPF doesn't see
        // Ctrl / Shift being held; Windows is asked directly
        e.Handled = true;
        var mods = KeyState.Modifiers;
        var items = EntriesList.Items;
        if ((mods & ModifierKeys.Shift) != 0 && _anchor != null && items.Contains(_anchor))
        {
            int from = items.IndexOf(_anchor), to = items.IndexOf(_pressedRow);
            if ((mods & ModifierKeys.Control) == 0) EntriesList.SelectedItems.Clear();
            for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                if (!EntriesList.SelectedItems.Contains(items[i])) EntriesList.SelectedItems.Add(items[i]);
        }
        else if ((mods & ModifierKeys.Control) != 0)
        {
            if (EntriesList.SelectedItems.Contains(_pressedRow)) EntriesList.SelectedItems.Remove(_pressedRow);
            else EntriesList.SelectedItems.Add(_pressedRow);
            _anchor = _pressedRow;
        }
        else if (EntriesList.SelectedItems.Count > 1 && EntriesList.SelectedItems.Contains(_pressedRow))
        {
            _narrowOnRelease = true; // keep the selection: it may be about to be dragged
            _anchor = _pressedRow;
        }
        else
        {
            EntriesList.SelectedItems.Clear();
            EntriesList.SelectedItems.Add(_pressedRow);
            _anchor = _pressedRow;
        }
    }

    private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
    {
        for (DependencyObject? p = d; p != null; p = p is Visual ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p is T) return true;
        return false;
    }

    /// <summary>Where a Shift+click range starts (the last plain or Ctrl click).</summary>
    private ArchiveEntryRow? _anchor;

    /// <summary>Double-click: the file opens in its own app (from a temp copy), like in WinRAR.</summary>
    private async Task OpenAsync(string archive, ArchiveEntryRow row)
    {
        _busy = true;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            string path = await ExtractToTempAsync(archive, row);
            App.Log($"[ArchiveViewer] Opening '{row.Key}'");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
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

    private async void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedRow == null || _archive == null || _busy || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(EntriesList) - _pressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // Dragged: the selection (or just this row) out of the archive, into Explorer / the desktop
        var pressed = _pressedRow;
        _pressedRow = null;
        _narrowOnRelease = false;
        var rows = EntriesList.SelectedItems.Contains(pressed) ? SelectedRows() : new List<ArchiveEntryRow> { pressed };
        _busy = true;
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            var paths = await ExtractToTempAsync(_archive, rows);
            Mouse.OverrideCursor = null;
            if (!LeftButtonDown) return;
            App.Log($"[ArchiveViewer] Dragging {paths.Length} entries out");
            // What really goes out: files inside a dragged folder travel with it, so they aren't counted on their own
            int files = rows.Count(r => !r.IsFolder), folders = rows.Count - files;
            string label = rows.Count == 1 ? rows[0].Name : string.Join(", ", new[]
            {
                files > 0 ? Loc.Plural("count.files", files) : null,
                folders > 0 ? Loc.Plural("count.folders", folders) : null,
            }.Where(s => s != null));
            using (DragCursor.Attach(EntriesList, ShellIcons.For(rows[0].Name, rows[0].IsFolder), label))
                DragDrop.DoDragDrop(EntriesList, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
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
        if (_narrowOnRelease && _pressedRow != null)
        {
            EntriesList.SelectedItems.Clear();
            EntriesList.SelectedItem = _pressedRow;
        }
        _narrowOnRelease = false;
        if (!_busy) _pressedRow = null;
    }

    // --- Right button held: a small preview next to the cursor

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".log", ".csv", ".json", ".xml", ".html", ".htm", ".css", ".js", ".ts", ".cs", ".py", ".java", ".c", ".cpp",
        ".h", ".ini", ".cfg", ".yaml", ".yml", ".toml", ".sql", ".ps1", ".bat", ".cmd", ".sh", ".php", ".go", ".rs", ".kt", ".svg",
    };

    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".webp", ".ico", ".tif", ".tiff",
    };

    private System.Windows.Threading.DispatcherTimer? _peekWatch;
    private int _peekToken;

    private async void OnRowRightDown(object sender, MouseButtonEventArgs e)
    {
        var row = RowAt(e.OriginalSource);
        if (row == null || row.IsFolder || _archive == null) return;
        e.Handled = true;
        string archive = _archive;
        int token = ++_peekToken;

        PeekName.Text = row.Name;
        PeekSize.Text = row.SizeText;
        PeekIcon.Source = row.Icon;
        PeekImage.Source = null;
        PeekText.Text = "";
        PeekLoading.Visibility = Visibility.Visible;
        PeekFooter.Visibility = Visibility.Collapsed;
        PeekPopup.IsOpen = true;

        // Closes as soon as the button is let go (wherever the cursor is by then)
        _peekWatch?.Stop();
        _peekWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _peekWatch.Tick += (_, _) =>
        {
            if (RightButtonDown) return;
            _peekWatch?.Stop();
            _peekToken++;
            PeekPopup.IsOpen = false;
        };
        _peekWatch.Start();

        try
        {
            string path = await ExtractToTempAsync(archive, row);
            if (token != _peekToken) return;
            string ext = Path.GetExtension(path);
            if (PictureExtensions.Contains(ext))
            {
                var (width, height) = await Task.Run(() =>
                {
                    // Header only: the real size, without decoding the whole picture
                    var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(path),
                        System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation | System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                        System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0];
                    return (frame.PixelWidth, frame.PixelHeight);
                });
                if (token != _peekToken) return;
                PeekFooter.Text = $"{width} × {height} px";
                PeekFooter.Visibility = Visibility.Visible;
                var bmp = await Task.Run(() =>
                {
                    var b = new System.Windows.Media.Imaging.BitmapImage();
                    b.BeginInit();
                    b.UriSource = new Uri(path);
                    b.DecodePixelWidth = 840; // sharp at 420 px on a 200% screen
                    b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    b.EndInit();
                    b.Freeze();
                    return b;
                });
                if (token != _peekToken) return;
                PeekImage.Source = bmp;
                PeekLoading.Visibility = Visibility.Collapsed;
            }
            else if (TextExtensions.Contains(ext))
            {
                string text = await Task.Run(() => ReadStart(path));
                if (token != _peekToken) return;
                PeekText.Text = text;
                PeekLoading.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Anything else: Windows' own thumbnail (PDF pages, video frames, documents), or the big icon
                PeekImage.Source = ShellIcons.For(row.Name, false);
                ShellThumbnails.Request(path, 420, ShellThumbnails.CurrentGeneration, bmp => Dispatcher.BeginInvoke(() =>
                {
                    if (token == _peekToken) PeekImage.Source = bmp;
                }));
                PeekLoading.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ArchiveViewer] Peek failed: {ex.Message}");
            if (token == _peekToken) PeekPopup.IsOpen = false;
        }
    }

    /// <summary>The first lines of a text file, for the peek.</summary>
    private static string ReadStart(string path)
    {
        using var stream = File.OpenRead(path);
        var (encoding, _) = TextEncoding.Detect(stream);
        stream.Position = 0;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        for (int i = 0; i < 24 && reader.ReadLine() is string line; i++)
            lines.Add(line.Length > 90 ? line[..90] + "…" : line.Replace("\t", "    "));
        return string.Join("\n", lines);
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
