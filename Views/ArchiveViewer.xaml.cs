using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lupik.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SharpCompress.Archives;

namespace Lupik.Views;

/// <summary>One visible row of the archive listing.</summary>
public sealed record ArchiveEntryRow(string Name, string Icon, Brush IconBrush, Thickness Indent, string SizeText, string DateText);

/// <summary>
/// Lists what's inside ZIP / RAR / 7z / TAR / GZ archives without extracting anything.
/// </summary>
public partial class ArchiveViewer : UserControl
{
    public static readonly string[] Extensions = { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz" };

    private static readonly Brush FolderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)));
    private static readonly Brush FileBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xD6, 0xCB)));
    private static readonly Brush LockedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)));

    private int _loadToken;

    private sealed class Node
    {
        public string Name = "";
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
        EntriesList.ItemsSource = null;
    }

    /// <summary>Reads the archive index in the background. Returns false if superseded by a newer load.</summary>
    public async Task<bool> LoadArchiveAsync(string filePath)
    {
        int token = ++_loadToken;
        _archive = filePath;
        if (_extractCts == null) ResetExtractUi(); // a running extraction keeps going and reports when done
        FormatText.Text = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

        try
        {
            var (rows, files, folders, unpacked, encrypted) = await Task.Run(() => ReadArchive(filePath));
            if (token != _loadToken) return false;

            EntriesList.ItemsSource = rows;
            SummaryText.Text = $"{files} {Plural(files, "plik", "pliki", "plików")}  •  {folders} {Plural(folders, "folder", "foldery", "folderów")}" +
                               $"  •  po rozpakowaniu {FormatFileSize(unpacked)}" +
                               (encrypted ? "  •  zaszyfrowane" : "");
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return false;
            App.Log($"[ArchiveViewer] Failed to read '{filePath}': {ex}");
            EntriesList.ItemsSource = null;
            SummaryText.Text = $"Nie udało się odczytać archiwum: {ex.Message}";
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
                string key = (entry.Key ?? "").Replace('\\', '/').Trim('/');
                if (key.Length == 0) continue;

                // Walk/create the folder chain; archives often omit explicit folder entries
                string[] parts = key.Split('/');
                var node = root;
                for (int i = 0; i < parts.Length; i++)
                {
                    bool last = i == parts.Length - 1;
                    if (!node.Children.TryGetValue(parts[i], out var child))
                    {
                        child = new Node { Name = parts[i], IsDirectory = !last || entry.IsDirectory };
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
        Flatten(root, 0, rows, ref folders);
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

    /// <summary>Folders first, then files, each alphabetically; children indented under their folder.</summary>
    private static long Flatten(Node folder, int depth, List<ArchiveEntryRow> rows, ref int folders)
    {
        long total = 0;
        var ordered = folder.Children.Values
            .OrderByDescending(n => n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase);

        foreach (var node in ordered)
        {
            var indent = new Thickness(depth * 18, 0, 0, 0);
            if (node.IsDirectory)
            {
                folders++;
                int index = rows.Count;
                rows.Add(null!); // placeholder: size is known only after the children
                long size = Flatten(node, depth + 1, rows, ref folders);
                rows[index] = new ArchiveEntryRow(node.Name, "folder", FolderBrush, indent, FormatFileSize(size), "");
                total += size;
            }
            else
            {
                rows.Add(new ArchiveEntryRow(node.Name, "file", node.IsEncrypted ? LockedBrush : FileBrush, indent,
                    FormatFileSize(node.Size), node.Modified?.ToString("yyyy-MM-dd HH:mm") ?? ""));
                total += node.Size;
            }
        }
        return total;
    }

    // ---------- Extract ----------

    private string? _archive;
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
        ExtractProgressText.Text = "Przygotowywanie…";

        var progress = new Progress<(long done, long total, int files)>(p =>
        {
            if (_extractCts == null) return; // late report after finishing
            double fraction = p.total > 0 ? Math.Min(1, (double)p.done / p.total) : 1;
            ExtractProgressFill.Width = 160 * fraction;
            ExtractProgressText.Text = $"{fraction:P0}  •  {p.files} {Plural(p.files, "plik", "pliki", "plików")}";
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
                MessageBox.Show(Window.GetWindow(this)!, $"Nie udało się rozpakować archiwum:\n{ex.Message}", "Lupik");
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
        if (_extractResult != null && Directory.Exists(_extractResult))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_extractResult}\"") { UseShellExecute = true });
    }

    private void ResetExtractUi()
    {
        ExtractButton.Visibility = Visibility.Visible;
        ExtractProgressPanel.Visibility = Visibility.Collapsed;
        ExtractDonePanel.Visibility = Visibility.Collapsed;
    }

    private static string Plural(int n, string one, string few, string many)
    {
        if (n == 1) return one;
        int lastTwo = n % 100, last = n % 10;
        return last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? few : many;
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
