using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lupik.Core;

using Lupik.Localization;
namespace Lupik.Views;

/// <summary>One tile in the folder grid.</summary>
public sealed class FolderTile : INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string ToolTip { get; init; } = "";
    public bool IsDirectory { get; init; }

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Folder preview: Explorer-like thumbnail grid, a summary (sizes counted in the background)
/// and "zip this folder" with progress.
/// </summary>
public partial class FolderViewer : UserControl
{
    private const int MaxTiles = 400; // plain WrapPanel: keep huge folders responsive

    private string _folder = "";
    private CancellationTokenSource? _statsCts;
    private CancellationTokenSource? _zipCts;
    private string? _zipResult;
    private FolderTile? _selected;

    /// <summary>Double-click on a file (open it in the preview) or a subfolder (show it).</summary>
    public event Action<string>? OpenRequested;

    public FolderViewer()
    {
        InitializeComponent();
    }

    public string FolderPath => _folder;

    public void LoadFolder(string folder)
    {
        Release();
        _folder = folder;
        int generation = ShellThumbnails.NewGeneration();

        var info = new DirectoryInfo(folder);
        List<FileSystemInfo> entries;
        try
        {
            // Folders first, then files; names in Explorer's "natural" order ([2] before [10])
            entries = info.EnumerateFileSystemInfos()
                .Where(e => !e.Attributes.HasFlag(FileAttributes.Hidden) && !e.Attributes.HasFlag(FileAttributes.System))
                .OrderByDescending(e => e is DirectoryInfo)
                .ThenBy(e => e.Name, NaturalComparer.Instance)
                .ToList();
        }
        catch (Exception ex)
        {
            SummaryText.Text = Loc.T("folder.openError", ex.Message);
            Tiles.ItemsSource = null;
            return;
        }

        var tiles = entries.Take(MaxTiles).Select(e => new FolderTile
        {
            Path = e.FullName,
            Name = e.Name,
            IsDirectory = e is DirectoryInfo,
            Detail = e is FileInfo f ? FormatSize(f.Length) : Loc.T("folder.tileFolder"),
            ToolTip = $"{e.Name}\n{Loc.T("info.modifiedLabel")} {e.LastWriteTime:yyyy-MM-dd HH:mm}" + (e is FileInfo fi ? $"\n{Loc.T("info.sizeLabel")} {FormatSize(fi.Length)}" : ""),
        }).ToList();

        Tiles.ItemsSource = tiles;
        EmptyText.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TilesScroller.ScrollToTop();
        ZipButton.IsEnabled = info.Parent != null; // not for a drive root

        foreach (var tile in tiles)
        {
            var t = tile;
            ShellThumbnails.Request(t.Path, 128, generation, bmp => Dispatcher.BeginInvoke(() => t.Thumbnail = bmp));
        }

        int files = entries.Count(e => e is FileInfo), dirs = entries.Count - files;
        string shown = entries.Count > MaxTiles ? "  •  " + Loc.T("folder.truncated", MaxTiles) : "";
        string counts = $"{Loc.Plural("count.files", files)}  •  {Loc.Plural("count.folders", dirs)}";
        SummaryText.Text = $"{counts}  •  {Loc.T("folder.measuring")}{shown}";

        // Total size including subfolders, in the background
        _statsCts = new CancellationTokenSource();
        var token = _statsCts.Token;
        Task.Run(() =>
        {
            long total = 0; int allFiles = 0;
            foreach (var file in SafeEnumerateFiles(folder, token))
            {
                try { total += new FileInfo(file).Length; allFiles++; } catch { /* vanished / no access */ }
            }
            return (total, allFiles);
        }, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
            {
                var (total, allFiles) = t.Result;
                string nested = allFiles != files ? " (" + Loc.T("folder.withSubfolders", Loc.Plural("count.files", allFiles)) + ")" : "";
                SummaryText.Text = $"{counts}  •  {FormatSize(total)}{nested}{shown}";
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Stops background work and drops thumbnails (folder closed or Lupik idle).</summary>
    public void Release()
    {
        ShellThumbnails.NewGeneration();
        _statsCts?.Cancel();
        Tiles.ItemsSource = null;
        _selected = null;
        if (_zipCts == null) ResetZipUi(); // a running zip keeps going and reports when done
    }

    // ---------- Tiles ----------

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FolderTile tile) return;

        if (_selected != null) _selected.IsSelected = false;
        _selected = tile;
        tile.IsSelected = true;

        if (e.ClickCount == 2) OpenRequested?.Invoke(tile.Path);
        e.Handled = true;
    }

    // ---------- Zip ----------

    private async void OnZipClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_folder) || _zipCts != null) return;

        string folder = _folder;
        string target = UniqueZipPath(folder);
        string partial = target + ".part";
        _zipCts = new CancellationTokenSource();
        var token = _zipCts.Token;
        PendingCleanup.Register(partial, _zipCts);

        ZipButton.Visibility = Visibility.Collapsed;
        ZipDonePanel.Visibility = Visibility.Collapsed;
        ZipProgressPanel.Visibility = Visibility.Visible;
        ZipProgressFill.Width = 0;
        ZipProgressText.Text = Loc.T("common.preparing");

        var progress = new Progress<(long done, long total, int files)>(p =>
        {
            double fraction = p.total > 0 ? (double)p.done / p.total : 1;
            ZipProgressFill.Width = 160 * fraction;
            ZipProgressText.Text = $"{fraction.ToString("P0", Loc.Instance.Culture)}  •  {Loc.Plural("count.files", p.files)}";
        });

        try
        {
            await Task.Run(() => CreateZipAsync(folder, partial, progress, token), token);
            File.Move(partial, target);
            _zipResult = target;
            App.Log($"[FolderViewer] Zipped '{folder}' -> '{target}' ({FormatSize(new FileInfo(target).Length)})");

            ZipDoneText.Text = $"{Path.GetFileName(target)}  ({FormatSize(new FileInfo(target).Length)})";
            ZipProgressPanel.Visibility = Visibility.Collapsed;
            ZipDonePanel.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            ResetZipUi();
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            App.Log($"[FolderViewer] Zip failed: {ex}");
            ResetZipUi();
            if (!MainWindow.SuppressActivationForTests) // tests: log only, a message box would grab focus
                MessageBox.Show(Window.GetWindow(this)!, Loc.T("folder.zipError", ex.Message), "Lupik");
        }
        finally
        {
            PendingCleanup.Unregister(partial);
            _zipCts = null;
        }
    }

    /// <summary>
    /// Packs the folder with <see cref="FastZipWriter"/>: every file is compressed on all CPU cores at once
    /// (~5× faster than the built-in ZipArchive on a folder of PSDs), incompressible files are stored as-is.
    /// </summary>
    private static async Task CreateZipAsync(string folder, string zipPath, IProgress<(long, long, int)> progress, CancellationToken token)
    {
        var files = SafeEnumerateFiles(folder, token).ToList();
        long total = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        long doneBefore = 0; int count = 0;
        string root = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)); // unzips into its own folder
        var lastReport = Stopwatch.StartNew();

        await using var output = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        using var zip = new FastZipWriter(output);

        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            long before = doneBefore;
            await zip.AddFileAsync($"{root}/{relative}", file, processed =>
            {
                if (lastReport.ElapsedMilliseconds < 100) return;
                lastReport.Restart();
                progress.Report((before + processed, total, count));
            }, token);
            doneBefore += new FileInfo(file).Length;
            count++;
        }

        // Keep empty subfolders too
        foreach (string dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                zip.AddDirectory($"{root}/{Path.GetRelativePath(folder, dir).Replace('\\', '/')}", Directory.GetLastWriteTime(dir));
        }
        zip.Finish();
        progress.Report((total, total, count));
    }

    private void OnZipCancel(object sender, RoutedEventArgs e) => _zipCts?.Cancel();

    private void OnZipReveal(object sender, RoutedEventArgs e)
    {
        if (_zipResult != null && File.Exists(_zipResult))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_zipResult}\"") { UseShellExecute = true });
    }

    private void ResetZipUi()
    {
        ZipButton.Visibility = Visibility.Visible;
        ZipProgressPanel.Visibility = Visibility.Collapsed;
        ZipDonePanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>"Folder.zip" next to the folder, or "Folder (2).zip" if that exists.</summary>
    private static string UniqueZipPath(string folder)
    {
        string parent = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar))!;
        string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        string path = Path.Combine(parent, name + ".zip");
        for (int i = 2; File.Exists(path) || File.Exists(path + ".part"); i++)
            path = Path.Combine(parent, $"{name} ({i}).zip");
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    // ---------- Helpers ----------

    /// <summary>All files under a folder, skipping ones we can't access instead of failing.</summary>
    private static IEnumerable<string> SafeEnumerateFiles(string folder, CancellationToken token)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            token.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
        return $"{len:0.##} {sizes[order]}";
    }

    private static string Plural(int n, string one, string few, string many)
    {
        if (n == 1) return one;
        int lastTwo = n % 100, last = n % 10;
        return last is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? few : many;
    }

    /// <summary>Explorer's name ordering ("[2]" before "[10]").</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string a, string b);
        public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
    }
}
