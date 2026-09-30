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

public partial class MainWindow : Window
{
    private string _currentFilePath = "";
    private int _showToken;
    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".heic", ".heif", ".psd", ".avif", ".bmp", ".gif", ".webp", ".ico", ".tiff", ".tif", ".svg", ".eps", ".epsf", ".epsi", ".ps"
    };

    private static readonly string[] CodeExtensions =
    {
        ".txt", ".cs", ".py", ".js", ".ts", ".jsx", ".tsx", ".json", ".xml", ".html",
        ".htm", ".css", ".scss", ".less", ".sql", ".rs", ".go", ".cpp", ".c", ".h",
        ".hpp", ".java", ".kt", ".php", ".ps1", ".psm1", ".bat", ".cmd", ".sh",
        ".bash", ".yaml", ".yml", ".ini", ".cfg", ".config", ".toml", ".log", ".md",
        ".env", ".gitignore", ".gitattributes", ".csproj", ".props", ".targets"
    };

    /// <summary>Visibility and handle mirrored for the keyboard hook thread, which can't touch WPF properties.</summary>
    public volatile bool IsShown;
    public IntPtr Hwnd { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        InitNativeHandle();
        IsVisibleChanged += (_, _) => IsShown = IsVisible;
        FolderViewerControl.OpenRequested += OpenFromFolder;
        ArchiveViewerControl.PeekRequested += PeekFromArchive;
        Loc.Instance.LanguageChanged += UpdateWelcomeText;
        CompareViewerControl.SingleRequested += path => _ = ShowFile(path);
        WireImageExtras();
        PdfViewerControl.Notice += message => ShowNotice(message);
        WatchAppearance();
        PreviewTextInput += OnWindowTextInput;
        UpdatePinButton();
        if (_pinned) Closed += (_, _) => ReleasePinned();
    }

    /// <summary>A pinned window closed: let go of its file and of the app-wide events.</summary>
    private void ReleasePinned()
    {
        Loc.Instance.LanguageChanged -= UpdateWelcomeText;
        SystemPreviewControl.Close();
        MediaViewerControl.Stop();
        ImageViewerControl.Release();
        PdfViewerControl.Release();
        CodeViewerControl.Release();
        FolderViewerControl.Release();
        CsvViewerControl.Release();
        DocxViewerControl.Release();
        ArchiveViewerControl.Release();
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Welcome text names the key that opens a preview.</summary>
    private void UpdateWelcomeText() => WelcomeSubtitle.Text = Loc.T("welcome.subtitle", Loc.T("key.preview"));

    public void ShowWelcome()
    {
        App.Log("[MainWindow] ShowWelcome called.");
        UpdateWelcomeText();
        _currentFilePath = "";
        _boundsKind = null;
        TitleFileNameText.Text = Loc.T("main.readyTitle");
        TitleIconImage.Source = null;
        Title = "Lupik";
        FileActions.Visibility = SaveAsButton.Visibility = CropButton.Visibility = Visibility.Collapsed; // nothing to act on

        _showToken++; // cancel any image still loading
        CancelIdleRelease();
        ShowOnlyViewer(WelcomeView);
        SetBounds(ComputeDefaultBounds(""));

        BringToFront();
    }

    public async Task ShowFile(string filePath)
    {
        App.Log($"[MainWindow] ShowFile: '{filePath}'");
        if (string.IsNullOrEmpty(filePath) || !PathExists(filePath))
        {
            App.Log($"[MainWindow] File does not exist or empty: '{filePath}', falling back to Welcome screen.");
            ShowWelcome();
            return;
        }

        // Another file while editing: finish editing first (asks about unsaved changes)
        if (EditMode && !string.Equals(filePath, _editingPath, StringComparison.OrdinalIgnoreCase) && !ExitEdit()) return;
        CancelIdleRelease();
        CloseSearch(); // a new file: the old matches mean nothing there
        CloseOpenWithMenu();
        _boundsKind = KindOf(filePath);
        int token = ++_showToken;
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        BeginLoading(filePath, ext, token);

        try
        {
            // Folders first: names like "LTN1400 CORD 205.14 DOBOXA" have a bogus "extension"
            if (Directory.Exists(filePath))
            {
                FolderViewerControl.LoadFolder(filePath); // first: it resets pending thumbnail requests
                ApplyFileHeader(filePath);                 // ...then asks for the title-bar folder icon
                ShowOnlyViewer(FolderViewerControl);
                SetBounds(ComputeDefaultBounds(""));
            }
            else if (Array.IndexOf(ImageExtensions, ext) >= 0)
            {
                // Decode in the background; the previous image stays on screen until the new one is ready
                bool loaded = await ImageViewerControl.LoadImageAsync(filePath);
                if (!loaded || token != _showToken) return; // superseded by a newer request

                ApplyFileHeader(filePath);
                ShowOnlyViewer(ImageViewerControl);
                ImageViewerControl.SetFilmstrip(ImageSiblings(filePath), filePath);
                if (!_isFullScreen) SetBounds(ComputeImageBounds(ImageViewerControl.NaturalWidth, ImageViewerControl.NaturalHeight));
                PreloadNeighbors(filePath);
            }
            else if (Views.MediaViewer.IsMedia(ext))
            {
                ApplyFileHeader(filePath);
                ShowOnlyViewer(MediaViewerControl); // the player has to be in the visual tree to open the file
                // ...and on screen: MediaElement doesn't open anything in a hidden window. Showing it now also keeps
                // the loading overlay from kicking in (it would collapse the player, which stops the file opening).
                if (!IsVisible) SetBounds(ComputeDefaultBounds(ext));
                BringToFront();
                bool opened = await MediaViewerControl.LoadAsync(filePath);
                if (token != _showToken) return;

                if (!opened)
                {
                    // No codec for it: the file info card instead of a black box
                    GenericViewerControl.LoadFile(filePath);
                    ShowOnlyViewer(GenericViewerControl);
                    SetBounds(ComputeDefaultBounds(ext));
                }
                else if (MediaViewerControl.IsVideo && MediaViewerControl.NaturalWidth > 0)
                {
                    const double transportBar = 44 - 36; // taller than the image footer ComputeImageBounds assumes
                    SetBounds(ComputeImageBounds(MediaViewerControl.NaturalWidth, MediaViewerControl.NaturalHeight + transportBar));
                }
                else
                {
                    SetBounds(ComputeAudioBounds());
                }
            }
            else if (ext == ".pdf" || (ext == ".ai" && IsPdfCompatible(filePath)))
            {
                ApplyFileHeader(filePath);
                ShowOnlyViewer(PdfViewerControl);
                SetBounds(ComputeDefaultBounds(ext));
                BringToFront();
                // Returns once the first page is visible; remaining pages keep rendering in the background
                await PdfViewerControl.LoadPdfAsync(filePath);
                if (token != _showToken) return;
            }
            else if (Views.DocxViewer.CanOpen(ext) && await DocxViewerControl.LoadAsync(filePath))
            {
                // Word documents: Lupik's own view (a damaged or odd file falls through to Windows' previewer below)
                if (token != _showToken) return;
                ApplyFileHeader(filePath);
                ShowOnlyViewer(DocxViewerControl);
                SetBounds(ComputeDefaultBounds(ext));
            }
            else if (Views.CsvViewer.IsWorkbook(ext) && await CsvViewerControl.LoadWorkbookAsync(filePath))
            {
                // Excel workbooks: the table view, a tab per sheet
                if (token != _showToken) return;
                ApplyFileHeader(filePath);
                ShowOnlyViewer(CsvViewerControl);
                SetBounds(ComputeDefaultBounds(ext));
            }
            else if (Array.IndexOf(Views.CsvViewer.Extensions, ext) >= 0)
            {
                bool loaded = await CsvViewerControl.LoadFileAsync(filePath);
                if (!loaded || token != _showToken) return;

                ApplyFileHeader(filePath);
                ShowOnlyViewer(CsvViewerControl);
                SetBounds(ComputeDefaultBounds(ext));
            }
            else if (Array.IndexOf(Views.ArchiveViewer.Extensions, ext) >= 0)
            {
                bool loaded = await ArchiveViewerControl.LoadArchiveAsync(filePath);
                if (!loaded || token != _showToken) return;

                ApplyFileHeader(filePath);
                ShowOnlyViewer(ArchiveViewerControl);
                SetBounds(ComputeDefaultBounds(ext));
            }
            else if (Array.IndexOf(CodeExtensions, ext) >= 0)
            {
                bool loaded = await CodeViewerControl.LoadFileAsync(filePath);
                if (!loaded || token != _showToken) return;

                ApplyFileHeader(filePath);
                ShowOnlyViewer(CodeViewerControl);
                SetBounds(ComputeDefaultBounds(ext));
            }
            else
            {
                ApplyFileHeader(filePath);
                SetBounds(ComputeDefaultBounds(ext));

                // Anything Windows can preview (Office documents, .msg, fonts, ...) uses the system previewer;
                // otherwise fall back to the file info card
                var previewer = Views.PreviewHandlerHost.FindHandler(ext);
                ShowOnlyViewer(SystemPreviewControl); // the host window must exist before the previewer attaches
                if (previewer == null || !SystemPreviewControl.Open(filePath, previewer.Value))
                {
                    GenericViewerControl.LoadFile(filePath);
                    ShowOnlyViewer(GenericViewerControl);
                }
            }

            BringToFront();
            App.Log($"[MainWindow] Window shown and activated successfully for: '{filePath}'");
        }
        finally
        {
            if (token == _showToken) EndLoading();
        }
    }

    private DispatcherTimer? _loadingDelayTimer;
    private System.Windows.Media.Animation.Storyboard? _spinner;

    /// <summary>
    /// Arms the loading overlay. It only appears if loading takes longer than a blink, so fast files don't flash.
    /// If the window is still hidden at that point, it's shown right away with the overlay, so a slow file
    /// never looks like Lupik ignored the key press.
    /// </summary>
    private void BeginLoading(string filePath, string ext, int token)
    {
        _loadingDelayTimer?.Stop();
        _loadingDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _loadingDelayTimer.Tick += (_, _) =>
        {
            _loadingDelayTimer?.Stop();
            if (token != _showToken) return;

            App.Log($"[MainWindow] Slow load, showing overlay for: '{filePath}'");
            LoadingFileText.Text = Path.GetFileName(filePath);
            LoadingOverlay.Visibility = Visibility.Visible;
            _spinner ??= CreateSpinner();
            _spinner.Begin(this, true);

            if (!IsVisible)
            {
                TitleFileNameText.Text = Path.GetFileName(filePath);
                // Nothing stale behind the overlay; but a player that's opening its file stays (collapsing it stops it)
                if (MediaViewerControl.Visibility != Visibility.Visible) ShowOnlyViewer(LoadingOverlay);
                SetBounds(ComputeDefaultBounds(ext));
                BringToFront();
            }
        };
        _loadingDelayTimer.Start();
    }

    private void EndLoading()
    {
        _loadingDelayTimer?.Stop();
        _spinner?.Stop(this);
        LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    private System.Windows.Media.Animation.Storyboard CreateSpinner()
    {
        var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };
        System.Windows.Media.Animation.Storyboard.SetTargetName(spin, nameof(SpinnerRotation));
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(spin, new PropertyPath(RotateTransform.AngleProperty));
        var sb = new System.Windows.Media.Animation.Storyboard();
        sb.Children.Add(spin);
        return sb;
    }

    private void ApplyFileHeader(string filePath)
    {
        _currentFilePath = filePath;
        TitleFileNameText.Text = Path.GetFileName(filePath);
        Title = $"{Path.GetFileName(filePath)} — Lupik"; // taskbar / Alt+Tab label
        UpdateTitleIcon(filePath);
        FileActions.Visibility = Visibility.Visible;
        PrintActionButton.Visibility = CanPrintCurrent() ? Visibility.Visible : Visibility.Collapsed;
        CropButton.Visibility = File.Exists(filePath) && IsImagePath(filePath) ? Visibility.Visible : Visibility.Collapsed;
        ImageViewerControl.CancelCrop();

        // Folders: "show in folder" and "open" (in Explorer) make sense; save-as / open-with don't
        bool isFolder = Directory.Exists(filePath);
        OpenWithButton.Visibility = isFolder ? Visibility.Collapsed : Visibility.Visible;
        // Save as = convert to another image format; other files have nothing to convert to
        SaveAsButton.Visibility = !isFolder && IsImagePath(filePath) ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _folderHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (isFolder)
        {
            // Explorer's own folder icon for the title bar
            ShellThumbnails.Request(filePath, 32, ShellThumbnails.CurrentGeneration,
                bmp => Dispatcher.BeginInvoke(() => { if (_currentFilePath == filePath) TitleIconImage.Source = bmp; }));
        }
    }

    /// <summary>Switches viewers without ever collapsing the one that stays, so nothing blinks.</summary>
    private void ShowOnlyViewer(UIElement target)
    {
        foreach (var viewer in new UIElement[] { WelcomeView, ImageViewerControl, CompareViewerControl, DiffViewerControl, CodeViewerControl, PdfViewerControl, GenericViewerControl, ArchiveViewerControl, CsvViewerControl, DocxViewerControl, FolderViewerControl, SystemPreviewControl, MediaViewerControl })
        {
            var visibility = viewer == target ? Visibility.Visible : Visibility.Collapsed;
            // Release the system previewer / player (they hold the file open) as soon as they're not shown
            if (viewer == SystemPreviewControl && visibility == Visibility.Collapsed) SystemPreviewControl.Close();
            if (viewer == MediaViewerControl && visibility == Visibility.Collapsed && viewer.Visibility == Visibility.Visible) MediaViewerControl.Stop();
            if (viewer.Visibility != visibility) viewer.Visibility = visibility;
        }
        UpdateFooter();
        CopyInPreview = target == ImageViewerControl || target == CsvViewerControl;
        PdfInPreview = target == PdfViewerControl;
        UpdateSearchable();
        UpdateEditable();
    }

    /// <summary>Whether Ctrl+C / Ctrl+A mean something in the current preview (read by the keyboard hook thread).</summary>
    public volatile bool CopyInPreview;

    /// <summary>A PDF is shown: PgUp/PgDn/Home/End page through it (read by the keyboard hook thread).</summary>
    public volatile bool PdfInPreview;

    /// <summary>
    /// Shows the window above everything else. Topmost is only held for the moment of opening,
    /// so other windows can cover the preview again once the user switches away.
    /// </summary>
    /// <summary>
    /// For automated tests only: show the window without activating it, so a test never steals
    /// keyboard focus from whatever the user is doing.
    /// </summary>
#pragma warning disable CS0649 // set by automated tests (e.g. via reflection)
    internal static bool SuppressActivationForTests;
#pragma warning restore CS0649

    // --- Idle cleanup ---
    // After the preview has been closed for a while, drop everything it holds (images, preloads, PDF pages,
    // CSV rows, text) and hand the memory back to Windows. Not immediately, so reopening right away stays instant.

    private DispatcherTimer? _idleTimer;

    private void ScheduleIdleRelease()
    {
        _idleTimer?.Stop();
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer!.Stop();
            if (IsVisible) return;
            ReleaseResources();
        };
        _idleTimer.Start();
    }

    private void CancelIdleRelease() => _idleTimer?.Stop();

    private void ReleaseResources()
    {
        long before = Environment.WorkingSet;

        ImageViewerControl.Release();
        PdfViewerControl.Release();
        CodeViewerControl.Release();
        FolderViewerControl.Release();
        CsvViewerControl.Release();
        DocxViewerControl.Release();
        ArchiveViewerControl.Release();
        CompareViewerControl.Release();
        DiffViewerControl.Release();
        MediaViewerControl.Stop();
        TitleIconImage.Source = null;
        _selectionSet = Array.Empty<string>();

        // Large bitmaps live on the large-object heap, which is only compacted on request
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Return the freed pages to Windows (otherwise Task Manager keeps showing the peak)
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
            EmptyWorkingSet(self.Handle);

        App.Log($"[MainWindow] Idle cleanup: working set {before / 1048576} MB -> {Environment.WorkingSet / 1048576} MB");
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);

    public void HideWindow()
    {
        App.Log("[MainWindow] HideWindow called");
        if (EditMode && !ExitEdit()) return; // unsaved changes: the user chose to stay
        CloseOpenWithMenu();
        if (_pinned) { Close(); return; } // a pinned window is done once closed

        // Nothing to hand back: the preview never took the focus from Explorer
        Hide();
        CloseSearch();
        _showToken++; // cancel any image still loading
        EndLoading();
        if (_isFullScreen) ToggleFullScreen(); // next preview opens as a normal window
        ResetFolderHistory();
        SystemPreviewControl.Close();
        SystemPreviewControl.Visibility = Visibility.Collapsed;
        MediaViewerControl.Stop(); // no music playing from a hidden window
        MediaViewerControl.Visibility = Visibility.Collapsed;
        ScheduleIdleRelease();
        _currentFilePath = "";
        WelcomeView.Visibility = Visibility.Collapsed;
        ImageViewerControl.Visibility = Visibility.Collapsed;
        CompareViewerControl.Visibility = Visibility.Collapsed;
        CodeViewerControl.Visibility = Visibility.Collapsed;
        PdfViewerControl.Visibility = Visibility.Collapsed;
        GenericViewerControl.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Space in Explorer/Desktop while the preview is open: swap to the newly selected file,
    /// or close if the selection is still the file being previewed.
    /// </summary>
    public void ShowSelectedOrHide()
    {
        string? selected = ExplorerService.GetSelectedFilePath();
        CaptureSelection();
        ResetFolderHistory(); // opened from Explorer, not from a folder preview
        App.Log($"[MainWindow] ShowSelectedOrHide: selected='{selected}', current='{_currentFilePath}'");

        bool samePairShown = CompareViewerControl.Visibility == Visibility.Visible && ComparePair() is var (a, b) &&
            new[] { a, b }.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
                new[] { CompareViewerControl.PathA, CompareViewerControl.PathB }.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        // Arrow keys change the file in the preview only (Explorer keeps its selection, the preview has no focus).
        // So Space with the same Explorer selection as before means "close", not "go back to that file".
        bool selectionChanged = !string.Equals(selected, _explorerSelection, StringComparison.OrdinalIgnoreCase);
        _explorerSelection = selected;

        if (selectionChanged && !string.IsNullOrEmpty(selected) && PathExists(selected) && !samePairShown &&
            (ComparePair() != null || !string.Equals(selected, _currentFilePath, StringComparison.OrdinalIgnoreCase)))
        {
            ShowSelection(selected);
        }
        else
        {
            HideWindow();
        }
    }

    /// <summary>What Explorer had selected when the preview last looked (to tell "Space again" from "picked another file").</summary>
    private string? _explorerSelection;

    public void ToggleWindow()
    {
        App.Log($"[MainWindow] ToggleWindow called. Current IsVisible: {IsVisible}");
        if (IsVisible)
        {
            HideWindow();
        }
        else
        {
            string? selected = ExplorerService.GetSelectedFilePath();
        CaptureSelection();
        ResetFolderHistory(); // opened from Explorer, not from a folder preview
            App.Log($"[MainWindow] ExplorerService returned: '{selected}'");
            _explorerSelection = selected;
            if (!string.IsNullOrEmpty(selected) && PathExists(selected))
            {
                ShowSelection(selected);
            }
            else
            {
                ShowWelcome();
            }
        }
    }

    private void UpdateTitleIcon(string filePath)
    {
        try
        {
            using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
            if (sysIcon != null)
            {
                using var bmp = sysIcon.ToBitmap();
                IntPtr hBitmap = bmp.GetHbitmap();
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    TitleIconImage.Source = source;
                }
                finally
                {
                    NativeMethods.DeleteObject(hBitmap);
                }
            }
        }
        catch
        {
            TitleIconImage.Source = null;
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        HideWindow();
    }

    /// <summary>Illustrator files saved with "PDF compatible" (the default) are PDFs inside.</summary>
    private static bool IsPdfCompatible(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[5];
            return stream.Read(header) == 5 && header.SequenceEqual("%PDF-"u8);
        }
        catch
        {
            return false;
        }
    }

    private static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "test_samples", fileName);

    private void OnSampleImageClicked(object sender, RoutedEventArgs e)
    {
        string sample = SamplePath("sample.png");
        if (File.Exists(sample)) _ = ShowFile(sample);
    }

    private void OnSampleVectorClicked(object sender, RoutedEventArgs e)
    {
        string sample = SamplePath("sample.eps");
        if (File.Exists(sample)) _ = ShowFile(sample);
    }

    private void OnSampleCodeClicked(object sender, RoutedEventArgs e)
    {
        string sample = SamplePath("sample.cs");
        if (File.Exists(sample)) _ = ShowFile(sample);
    }

    private void OnSamplePdfClicked(object sender, RoutedEventArgs e)
    {
        string sample = SamplePath("sample.pdf");
        if (File.Exists(sample)) _ = ShowFile(sample);
    }
}
