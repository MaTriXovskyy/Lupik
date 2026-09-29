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
        Loc.Instance.LanguageChanged += UpdateWelcomeText;
        CompareViewerControl.SingleRequested += path => _ = ShowFile(path);
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

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

    private void InitNativeHandle()
    {
        var helper = new WindowInteropHelper(this);
        IntPtr hwnd = helper.EnsureHandle();
        Hwnd = hwnd;

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(HwndHook);

        ApplyHotkey();
        Settings.Changed += () => ApplyHotkey();

        // Borderless windows lack the minimize style, so clicking the taskbar button wouldn't minimize/restore
        const int GWL_STYLE = -16, WS_MINIMIZEBOX = 0x20000;
        SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) | WS_MINIMIZEBOX);

        // Never activated: Explorer keeps the focus (see BringToFront)
        const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        ShowActivated = false;
        WatchForeground();

        ApplyModernStyling(hwnd);
    }

    /// <summary>(Re)registers the global hotkey chosen in Settings. False if another app already uses it.</summary>
    public bool ApplyHotkey()
    {
        NativeMethods.UnregisterHotKey(Hwnd, 9001);

        var hotkey = Settings.Current.GlobalHotkey;
        if (hotkey == null)
        {
            App.Log("[MainWindow] Global hotkey disabled");
            return true;
        }

        bool ok = NativeMethods.RegisterHotKey(Hwnd, 9001, hotkey.HotkeyModifiers | NativeMethods.MOD_NOREPEAT, (uint)hotkey.Vk);
        App.Log($"[MainWindow] Registered HotKey {hotkey}: {ok}");
        return ok;
    }

    /// <summary>While Settings records a new shortcut, the current one mustn't fire.</summary>
    public void SuspendHotkey() => NativeMethods.UnregisterHotKey(Hwnd, 9001);

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        ApplyModernStyling(helper.Handle);
    }

    private void ApplyModernStyling(IntPtr hwnd)
    {
        try
        {
            int darkMode = 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int cornerPreference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
        }
        catch { }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (NativeMethods.RefuseAutomation(msg, ref handled)) return IntPtr.Zero;
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == 9001)
        {
            App.Log("[MainWindow] WM_HOTKEY triggered");
            ToggleWindow();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Welcome text names the key that opens a preview.</summary>
    private void UpdateWelcomeText() => WelcomeSubtitle.Text = Loc.T("welcome.subtitle", Loc.T("key.preview"));

    public void ShowWelcome()
    {
        App.Log("[MainWindow] ShowWelcome called.");
        UpdateWelcomeText();
        _currentFilePath = "";
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

        CancelIdleRelease();
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
                SetBounds(ComputeImageBounds(ImageViewerControl.NaturalWidth, ImageViewerControl.NaturalHeight));
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
        SaveAsButton.Visibility = OpenWithButton.Visibility = isFolder ? Visibility.Collapsed : Visibility.Visible;
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
        foreach (var viewer in new UIElement[] { WelcomeView, ImageViewerControl, CompareViewerControl, CodeViewerControl, PdfViewerControl, GenericViewerControl, ArchiveViewerControl, CsvViewerControl, FolderViewerControl, SystemPreviewControl, MediaViewerControl })
        {
            var visibility = viewer == target ? Visibility.Visible : Visibility.Collapsed;
            // Release the system previewer / player (they hold the file open) as soon as they're not shown
            if (viewer == SystemPreviewControl && visibility == Visibility.Collapsed) SystemPreviewControl.Close();
            if (viewer == MediaViewerControl && visibility == Visibility.Collapsed && viewer.Visibility == Visibility.Visible) MediaViewerControl.Stop();
            if (viewer.Visibility != visibility) viewer.Visibility = visibility;
        }
        UpdateFooter();
        CopyInPreview = target == ImageViewerControl || target == CsvViewerControl;
    }

    /// <summary>Whether Ctrl+C / Ctrl+A mean something in the current preview (read by the keyboard hook thread).</summary>
    public volatile bool CopyInPreview;

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

    private void BringToFront()
    {
        if (SuppressActivationForTests)
        {
            ShowActivated = false;
            if (!IsVisible) Show();
            return;
        }

        // Restored from the taskbar: a minimized preview comes back as it was
        if (WindowState == WindowState.Minimized)
            WindowState = _isFullScreen ? WindowState.Maximized : WindowState.Normal;

        // The preview never takes the focus: activating it makes Windows wait until the previous foreground window
        // (Explorer) acknowledges losing it, up to 5 s whenever Explorer is busy. Like Quick Look on the Mac, Explorer
        // keeps the focus; the preview floats above it and gets its keys through the keyboard hook.
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground != Hwnd && foreground != IntPtr.Zero)
        {
            SourceWindow = GetAncestor(foreground, GA_ROOT);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Topmost = true; // stays on top while its Explorer window is in front (see OnForegroundChanged)
        if (!IsVisible) Show();
        // WPF skips Topmost when the property didn't change, and a window shown without activation keeps its old
        // place in the z-order (seen: behind Explorer). Put it on top explicitly, every time, without activating.
        SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        App.Log($"[MainWindow] BringToFront: shown in {sw.ElapsedMilliseconds} ms, source={DescribeWindow(SourceWindow)}");

        // First frame on screen: log how long it took and whether anything covers the preview
        Dispatcher.InvokeAsync(() =>
            App.Log($"[MainWindow] BringToFront: first frame after {sw.ElapsedMilliseconds} ms, covered by: {WindowsAbove()}"),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;

    /// <summary>The Explorer (or other) window the preview was opened from; its keys are routed to the preview.</summary>
    public volatile IntPtr SourceWindowField;
    public IntPtr SourceWindow { get => SourceWindowField; private set => SourceWindowField = value; }

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    private const uint GA_ROOT = 2;

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    private WinEventProc? _foregroundProc; // kept alive: the hook calls into it

    /// <summary>
    /// Foreground changes (out-of-context WinEvent: delivered asynchronously, never waits on anyone). The preview stays
    /// on top while its source window or Lupik itself is in front, and lets other apps cover it otherwise.
    /// </summary>
    private void WatchForeground()
    {
        const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
        _foregroundProc = (_, _, hwnd, _, _, _, _) => OnForegroundChanged(hwnd);
        SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        if (!IsVisible) return;
        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        GetWindowThreadProcessId(root, out uint pid);
        bool ours = pid == (uint)Environment.ProcessId;
        bool keepOnTop = ours || root == SourceWindow;
        if (!keepOnTop && Settings.Current.CloseOnFocusLoss && hwnd != IntPtr.Zero && !IsShellWindow(root))
        {
            App.Log($"[MainWindow] Another app in front ({DescribeWindow(root)}): closing (CloseOnFocusLoss)");
            HideWindow();
            return;
        }
        if (Topmost != keepOnTop) Topmost = keepOnTop;
        // Back to its Explorer window: make sure the preview is really above it again
        if (keepOnTop) SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>Taskbar, Start, notifications: clicking them isn't "switching to another app".</summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow" or "NotifyIconOverflowWindow"
            or "Progman" or "WorkerW" or "XamlExplorerHostIslandWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    // --- Diagnostics for z-order problems ---

    private const uint GW_HWNDPREV = 3;
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    private string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        if (hwnd == Hwnd) return "Lupik";
        var sb = new System.Text.StringBuilder(64);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return $"0x{hwnd:X} ({sb})";
    }

    /// <summary>Visible windows higher in the z-order that overlap the preview.</summary>
    private string WindowsAbove()
    {
        if (!GetWindowRect(Hwnd, out var me)) return "?";
        var above = new List<string>();
        for (IntPtr h = GetWindow(Hwnd, GW_HWNDPREV); h != IntPtr.Zero && above.Count < 5; h = GetWindow(h, GW_HWNDPREV))
        {
            if (!IsWindowVisible(h) || !GetWindowRect(h, out var r)) continue;
            bool overlaps = r.Left < me.Right && r.Right > me.Left && r.Top < me.Bottom && r.Bottom > me.Top;
            if (overlaps && r.Right - r.Left > 50 && r.Bottom - r.Top > 50) above.Add(DescribeWindow(h));
        }
        return above.Count == 0 ? "nothing" : string.Join(", ", above);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    // --- Idle cleanup ---
    // After the preview has been closed for a while, drop everything it holds (images, preloads, PDF pages,
    // CSV rows, text) and hand the memory back to Windows. Not immediately, so reopening right away stays instant.

    private DispatcherTimer? _idleTimer;

    private void ScheduleIdleRelease()
    {
        _idleTimer?.Stop();
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
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
        ArchiveViewerControl.Release();
        CompareViewerControl.Release();
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

        // Nothing to hand back: the preview never took the focus from Explorer
        Hide();
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    /// <summary>Work area (in DIPs) of the monitor under the cursor, plus its DPI scale.</summary>
    private bool TryGetWorkArea(out Rect work, out double scaleX, out double scaleY)
    {
        NativeMethods.GetCursorPos(out var pt);
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO();
        mi.cbSize = Marshal.SizeOf(mi);

        var dpi = VisualTreeHelper.GetDpi(this);
        scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
        {
            work = Rect.Empty;
            return false;
        }

        work = new Rect(mi.rcWork.Left / scaleX, mi.rcWork.Top / scaleY,
                        mi.rcWork.Width / scaleX, mi.rcWork.Height / scaleY);
        return true;
    }

    private Rect ComputeDefaultBounds(string ext)
    {
        if (!TryGetWorkArea(out var work, out _, out _)) return new Rect(Left, Top, Width, Height);

        double w = Math.Min(1104, work.Width * 0.92);
        double h = Math.Min(810, work.Height * 0.92);

        if (ext == ".pdf")
        {
            w = Math.Min(1012, work.Width * 0.90);
            h = Math.Min(1035, work.Height * 0.95);
        }

        double scale = Settings.Current.WindowScale; // "Window size" in Settings
        return Centered(work, Math.Max(w * scale, 552), Math.Max(h * scale, 414));
    }

    private Rect ComputeImageBounds(double imgWidth, double imgHeight)
    {
        if (imgWidth <= 0 || imgHeight <= 0) return ComputeDefaultBounds("");
        if (!TryGetWorkArea(out var work, out _, out _)) return new Rect(Left, Top, Width, Height);

        double maxW = work.Width * 0.95 * Settings.Current.WindowScale;
        double maxH = work.Height * 0.95 * Settings.Current.WindowScale;

        const double chrome = 38 + 36; // title bar + image footer (the key hints live in it)
        double w = imgWidth;
        double h = imgHeight + chrome;

        if (w > maxW || h > maxH)
        {
            double scaleFactor = Math.Min(maxW / w, (maxH - chrome) / imgHeight);
            w = imgWidth * scaleFactor;
            h = (imgHeight * scaleFactor) + chrome;
        }

        return Centered(work, Math.Max(w, 552), Math.Max(h, 414));
    }

    private static Rect Centered(Rect work, double w, double h) =>
        new(work.Left + (work.Width - w) / 2, work.Top + (work.Height - h) / 2, w, h);

    /// <summary>
    /// Moves and resizes the window in a single native call. Setting Left/Top/Width/Height one by one
    /// makes the window visibly jump through intermediate states.
    /// </summary>
    private void SetBounds(Rect bounds)
    {
        if (_isFullScreen) return; // full screen keeps covering the monitor while switching files

        if (Math.Abs(Left - bounds.Left) < 0.5 && Math.Abs(Top - bounds.Top) < 0.5 &&
            Math.Abs(Width - bounds.Width) < 0.5 && Math.Abs(Height - bounds.Height) < 0.5)
            return;

        if (!IsVisible || Hwnd == IntPtr.Zero)
        {
            Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        SetWindowPos(Hwnd, IntPtr.Zero,
            (int)Math.Round(bounds.Left * dpi.DpiScaleX), (int)Math.Round(bounds.Top * dpi.DpiScaleY),
            (int)Math.Round(bounds.Width * dpi.DpiScaleX), (int)Math.Round(bounds.Height * dpi.DpiScaleY),
            SWP_NOZORDER | SWP_NOACTIVATE);

        // Keep WPF's own properties in sync with the native size
        Width = bounds.Width;
        Height = bounds.Height;
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

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key == Key.System ? e.SystemKey : e.Key, KeyState.Modifiers)) e.Handled = true;
    }

    /// <summary>
    /// A key from the global keyboard hook (virtual-key code). The preview is never the focused window (taking the
    /// focus from Explorer makes Windows wait on it, up to 5 s when it's busy), so its keys arrive this way.
    /// </summary>
    public void HandleHookKey(int vk, ModifierKeys mods)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        // The delete confirmation doesn't take the focus either: Enter / Esc answer it
        if (Views.ConfirmDeleteWindow.Current is { } confirm)
        {
            if (key == Key.Enter) confirm.Answer(true);
            else if (key == Key.Escape) confirm.Answer(false);
            return;
        }
        if (key is Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.System)
        {
            ImageViewerControl.CropModifiersChanged();
            return;
        }
        HandleKey(key, mods);
    }

    /// <summary>All preview shortcuts. Returns true if the key did something.</summary>
    private bool HandleKey(Key key, ModifierKeys mods)
    {
        bool handled = false;
        bool imageShown = ImageViewerControl.Visibility == Visibility.Visible;
        bool csvShown = CsvViewerControl.Visibility == Visibility.Visible;
        bool mediaShown = MediaViewerControl.Visibility == Visibility.Visible;

        if (imageShown && ImageViewerControl.IsCropping)
        {
            // Crop mode owns the keyboard: Enter saves the crop, Esc leaves; nothing else (no file switching)
            if (key is Key.Enter or Key.Return) SaveCrop();
            else if (key == Key.Escape) ImageViewerControl.CancelCrop();
            else ImageViewerControl.CropModifiersChanged(); // Shift / Alt pressed mid-drag
            return true;
        }

        if (imageShown && key == Key.K && mods == 0)
        {
            ImageViewerControl.BeginCrop();
            handled = true;
        }
        else if (key == Key.Delete && mods == 0 && !string.IsNullOrEmpty(_currentFilePath))
        {
            DeleteCurrent();
            handled = true;
        }
        else if (mediaShown && mods == 0 && key is Key.K or Key.J or Key.L or Key.M)
        {
            // YouTube-style: K play/pause, J/L back/forward, M mute (Space and arrows keep closing/navigating)
            if (key == Key.K) MediaViewerControl.TogglePlay();
            else if (key == Key.J) MediaViewerControl.SeekBy(-5);
            else if (key == Key.L) MediaViewerControl.SeekBy(5);
            else MediaViewerControl.ToggleMute();
            handled = true;
        }
        else if (key == Key.S && mods == ModifierKeys.Control)
        {
            SaveAs();
            handled = true;
        }
        else if (key == Key.P && mods == ModifierKeys.Control)
        {
            OpenPrintDialog();
            handled = true;
        }
        else if (imageShown && key == Key.C && mods == ModifierKeys.Control)
        {
            ImageViewerControl.CopyImageToClipboard();
            handled = true;
        }
        else if (imageShown && key == Key.R && (mods & ~ModifierKeys.Shift) == 0)
        {
            ImageViewerControl.Rotate(mods == ModifierKeys.Shift ? -90 : 90);
            handled = true;
        }
        else if (imageShown && mods == 0 && key is Key.OemPlus or Key.Add)
        {
            ImageViewerControl.ZoomBy(1.25);
            handled = true;
        }
        else if (imageShown && mods == 0 && key is Key.OemMinus or Key.Subtract)
        {
            ImageViewerControl.ZoomBy(1 / 1.25);
            handled = true;
        }
        else if (imageShown && key == Key.I && mods == 0)
        {
            ImageViewerControl.ToggleInfo();
            handled = true;
        }
        else if (mods == 0 && key == Key.C && (CompareViewerControl.Visibility == Visibility.Visible || (imageShown && ComparePair() != null)))
        {
            ToggleCompare(); // 2 images selected: side by side ⇄ single
            handled = true;
        }
        else if (CompareViewerControl.Visibility == Visibility.Visible && CompareViewerControl.HandleKey(key, mods))
        {
            handled = true; // +/−/0, S (mode), X (swap); arrows do nothing here
        }
        else if (imageShown && mods == 0 && key is Key.D0 or Key.NumPad0)
        {
            ImageViewerControl.ResetZoom();
            handled = true;
        }
        else if (csvShown && key == Key.C && mods == ModifierKeys.Control)
        {
            CsvViewerControl.CopySelection();
            handled = true;
        }
        else if (csvShown && key == Key.A && mods == ModifierKeys.Control)
        {
            CsvViewerControl.SelectAll();
            handled = true;
        }
        else if (csvShown && key == Key.Escape && CsvViewerControl.HasSelection)
        {
            CsvViewerControl.ClearSelection(); // first Esc clears the selection, the next one closes
            handled = true;
        }
        else if (mods == ModifierKeys.Alt && (key == Key.Left || key == Key.Right))
        {
            if (key == Key.Left) GoBackToFolder(); else GoForward(); // Alt+←/→ like in Explorer
            handled = true;
        }
        else if ((key == Key.Back || key == Key.Escape) && mods == 0 && _folderHistory.Count > 0 && !_isFullScreen)
        {
            GoBackToFolder(); // opened from a folder preview: back to the folder instead of closing
            handled = true;
        }
        else if (key == Key.Escape && _isFullScreen)
        {
            ToggleFullScreen(); // first Esc leaves full screen, the next one closes
            handled = true;
        }
        else if (key == Key.F && mods == 0 && !string.IsNullOrEmpty(_currentFilePath))
        {
            ToggleFullScreen();
            handled = true;
        }
        else if (key == Key.Enter && mods == 0)
        {
            OpenInDefaultApp();
            handled = true;
        }
        else if (key == Key.Space || key == Key.Escape)
        {
            HideWindow();
            handled = true;
        }
        else if ((key == Key.Up || key == Key.Down) && CodeViewerControl.Visibility == Visibility.Visible)
        {
            // In the code preview ↑/↓ scroll the text; ←/→ still switch files
            CodeViewerControl.ScrollLines(key == Key.Up ? -1 : 1);
            handled = true;
        }
        else if (key == Key.Up || key == Key.Left)
        {
            NavigateAdjacent(-1);
            handled = true;
        }
        else if (key == Key.Down || key == Key.Right)
        {
            NavigateAdjacent(1);
            handled = true;
        }
        return handled;
    }

    /// <summary>Releasing Shift / Alt while dragging the crop frame applies at once; Alt alone must not open a menu.</summary>
    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (!ImageViewerControl.IsCropping) return;
        ImageViewerControl.CropModifiersChanged();
        e.Handled = true;
    }

    private bool _isFullScreen;

    /// <summary>The key-hint footer; the image preview shows the hints in its own footer instead.</summary>
    private void UpdateFooter()
    {
        bool show = !_isFullScreen && ImageViewerControl.Visibility != Visibility.Visible;
        FooterRow.Height = new GridLength(show ? 30 : 0);
        FooterBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Full screen for a slideshow feel: no title bar, window covers the whole monitor (incl. taskbar).
    /// Arrow-key navigation keeps working and stays in full screen.
    /// </summary>
    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        TitleRow.Height = new GridLength(_isFullScreen ? 0 : 38);
        TitleBar.Visibility = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        UpdateFooter();
        Background = _isFullScreen ? System.Windows.Media.Brushes.Black : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x13, 0x12, 0x11));
        // Full screen needs a frameless window (a captioned one maximizes to the work area, leaving the taskbar)
        if (_isFullScreen) WindowStyle = WindowStyle.None;
        WindowState = _isFullScreen ? WindowState.Maximized : WindowState.Normal;
        if (!_isFullScreen) WindowStyle = WindowStyle.SingleBorderWindow;

        if (!_isFullScreen && !string.IsNullOrEmpty(_currentFilePath))
        {
            SetBounds(ImageViewerControl.Visibility == Visibility.Visible
                ? ComputeImageBounds(ImageViewerControl.NaturalWidth, ImageViewerControl.NaturalHeight)
                : ComputeDefaultBounds(Path.GetExtension(_currentFilePath).ToLowerInvariant()));
        }
    }

    private void OnSaveAsClicked(object sender, RoutedEventArgs e) => SaveAs();

    private void OnCropClicked(object sender, RoutedEventArgs e)
    {
        if (ImageViewerControl.Visibility != Visibility.Visible) return;
        if (ImageViewerControl.IsCropping) SaveCrop(); else ImageViewerControl.BeginCrop();
        Keyboard.Focus(this);
    }

    /// <summary>Saves the selected part of the image as a new file (the original is never changed).</summary>
    private async void SaveCrop()
    {
        string source = _currentFilePath;
        if (!File.Exists(source)) return;
        var f = ImageViewerControl.CropFraction;
        int rotation = ImageViewerControl.Rotation;
        ImageViewerControl.CancelCrop();

        string ext = Path.GetExtension(source).ToLowerInvariant();
        // Vector and exotic formats are saved as PNG; common raster formats keep their own
        string targetExt = ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".tif" or ".tiff" ? ext : ".png";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("crop.saveTitle"),
            InitialDirectory = Path.GetDirectoryName(source),
            FileName = Path.GetFileNameWithoutExtension(source) + Loc.T("crop.fileSuffix") + targetExt,
            Filter = $"{targetExt.TrimStart('.').ToUpperInvariant()}|*{targetExt}|PNG|*.png|JPEG|*.jpg",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        string target = dialog.FileName;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, Loc.T("save.sameName"), "Lupik");
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                using var image = Views.ImageViewer.OpenForExport(source);
                image.AutoOrient();
                if (rotation != 0) image.Rotate(rotation);
                // The selection is relative to the image as shown (rotated), in the full-resolution pixels
                int x = (int)Math.Round(f.X * image.Width), y = (int)Math.Round(f.Y * image.Height);
                int w = (int)Math.Round(f.Width * image.Width), h = (int)Math.Round(f.Height * image.Height);
                w = Math.Clamp(w, 1, (int)image.Width - x);
                h = Math.Clamp(h, 1, (int)image.Height - y);
                image.Crop(new ImageMagick.MagickGeometry(x, y, (uint)w, (uint)h));
                image.ResetPage();
                if (Path.GetExtension(target).ToLowerInvariant() is ".jpg" or ".jpeg") image.Quality = 95;
                image.Write(target);
            });
            App.Log($"[MainWindow] Cropped '{source}' to '{target}'");
            ShowToast(Loc.T("crop.saved", Path.GetFileName(target)));
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Crop failed: {ex}");
            MessageBox.Show(this, Loc.T("crop.saveError", ex.Message), "Lupik");
        }
    }

    private void OnPrintClicked(object sender, RoutedEventArgs e) => OpenPrintDialog();

    private bool CanPrintCurrent()
    {
        string ext = Path.GetExtension(_currentFilePath).ToLowerInvariant();
        return File.Exists(_currentFilePath) && (ext == ".pdf" || Array.IndexOf(ImageExtensions, ext) >= 0);
    }

    /// <summary>Opens the print dialog for the current PDF or image.</summary>
    private async void OpenPrintDialog()
    {
        if (!CanPrintCurrent()) return;
        string path = _currentFilePath;
        int rotation = ImageViewerControl.Rotation; // read on the UI thread: WPF controls can't be touched from Task.Run
        try
        {
            Core.IPrintSource source = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? await Core.PdfPrintSource.OpenAsync(path)
                : await Task.Run(() => new Core.ImagePrintSource(path, rotation)); // prints as rotated in the preview

            var dialog = new Views.PrintWindow(source) { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Could not open print dialog: {ex}");
            MessageBox.Show(this, Loc.T("print.prepareError", ex.Message), "Lupik");
        }
    }

    private void OnShowInFolderClicked(object sender, RoutedEventArgs e) => RunShell("explorer.exe", $"/select,\"{_currentFilePath}\"");
    private void OnOpenDefaultClicked(object sender, RoutedEventArgs e) => OpenInDefaultApp();
    // Windows' own "Open with…" picker, so any installed app (Photoshop, etc.) can be chosen
    private void OnOpenWithClicked(object sender, RoutedEventArgs e) => RunShell("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {_currentFilePath}");

    private void OpenInDefaultApp()
    {
        if (!PathExists(_currentFilePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_currentFilePath) { UseShellExecute = true });
            HideWindow();
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Open in default app failed: {ex.Message}");
        }
    }

    private void RunShell(string exe, string args)
    {
        if (!PathExists(_currentFilePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = true });
            HideWindow();
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] {exe} failed: {ex.Message}");
        }
    }

    private static readonly (string Label, string Ext, ImageMagick.MagickFormat Format)[] ExportFormats =
    {
        ("PNG", ".png", ImageMagick.MagickFormat.Png),
        ("JPEG", ".jpg", ImageMagick.MagickFormat.Jpeg),
        ("TIFF", ".tif", ImageMagick.MagickFormat.Tiff),
        ("WebP", ".webp", ImageMagick.MagickFormat.WebP),
        ("BMP", ".bmp", ImageMagick.MagickFormat.Bmp),
    };

    /// <summary>
    /// Like "Export…" in macOS Preview: images can be re-encoded to another format
    /// (handy when an app like Photoshop chokes on the original); other files are saved as a copy.
    /// </summary>
    private async void SaveAs()
    {
        string source = _currentFilePath;
        if (string.IsNullOrEmpty(source) || !File.Exists(source)) return;

        string ext = Path.GetExtension(source).ToLowerInvariant();
        bool isImage = Array.IndexOf(ImageExtensions, ext) >= 0;
        string baseName = Path.GetFileNameWithoutExtension(source);

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("save.title"),
            InitialDirectory = Path.GetDirectoryName(source),
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (isImage)
        {
            string original = Loc.T("save.original", ext.TrimStart('.').ToUpperInvariant()) + $"|*{ext}";
            dialog.Filter = string.Join("|", ExportFormats.Select(f => $"{f.Label} (*{f.Ext})|*{f.Ext}").Prepend(original));
            // HEIC/HEIF default to JPEG, since most apps can't open them
            bool preferJpeg = ext is ".heic" or ".heif";
            dialog.FilterIndex = preferJpeg ? 3 : 1;
            dialog.FileName = baseName + Loc.T("save.copySuffix") + (preferJpeg ? ".jpg" : ext);
        }
        else
        {
            dialog.Filter = $"{(ext.Length > 0 ? ext.TrimStart('.').ToUpperInvariant() : Loc.T("save.file"))}|*{(ext.Length > 0 ? ext : ".*")}|{Loc.T("save.allFiles")}|*.*";
            dialog.FileName = baseName + Loc.T("save.copySuffix") + ext;
        }

        if (dialog.ShowDialog(this) != true) return;
        string target = dialog.FileName;

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, Loc.T("save.sameNameOrFolder"), "Lupik");
            return;
        }

        try
        {
            string targetExt = Path.GetExtension(target).ToLowerInvariant();
            var format = ExportFormats.FirstOrDefault(f => f.Ext == targetExt || (targetExt == ".jpeg" && f.Ext == ".jpg") || (targetExt == ".tiff" && f.Ext == ".tif"));

            // Rotation done in the preview is baked into the saved file, so it needs a re-encode too
            int rotation = isImage ? ImageViewerControl.Rotation : 0;
            bool convert = isImage && ((targetExt != ext && format.Label != null) || rotation != 0);

            if (convert)
            {
                await Task.Run(() =>
                {
                    using var image = Views.ImageViewer.OpenForExport(source);
                    image.AutoOrient();
                    if (rotation != 0) image.Rotate(rotation);
                    if (format.Label != null)
                    {
                        if (format.Format == ImageMagick.MagickFormat.Jpeg) image.Quality = 95;
                        image.Write(target, format.Format);
                    }
                    else
                    {
                        image.Write(target); // same, non-listed format (e.g. JFIF): inferred from the extension
                    }
                });
            }
            else
            {
                await Task.Run(() => File.Copy(source, target, overwrite: true));
            }

            App.Log($"[MainWindow] Saved '{source}' as '{target}'");
            ShowToast(Loc.T("save.saved", Path.GetFileName(target)));
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Save As failed: {ex}");
            MessageBox.Show(this, Loc.T("save.error", ex.Message), "Lupik");
        }
    }

    private async void ShowToast(string message)
    {
        // Icon-only button: flash a green check, with the details in the tooltip
        SaveAsIcon.Kind = "check";
        SaveAsButton.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA6, 0xE3, 0xA1));
        SaveAsButton.ToolTip = message;
        await Task.Delay(2200);
        SaveAsIcon.Kind = "save";
        SaveAsButton.ClearValue(ForegroundProperty);
        SaveAsButton.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("[main.saveAsTip]") { Source = Loc.Instance });
    }

    // Files selected in Explorer when the preview was opened; with 2+ files, arrows cycle only through them
    private IReadOnlyList<string> _selectionSet = Array.Empty<string>();

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

    /// <summary>Audio: just the cover and the transport bar, no need for a big window.</summary>
    private Rect ComputeAudioBounds()
    {
        if (!TryGetWorkArea(out var work, out _, out _)) return new Rect(Left, Top, Width, Height);
        return Centered(work, Math.Min(644, work.Width * 0.9), Math.Min(483, work.Height * 0.9));
    }

    // --- Delete: to the Recycle Bin, then on to the next file (like Peek) ---

    private void OnDeleteClicked(object sender, RoutedEventArgs e) => DeleteCurrent();

    private void DeleteCurrent()
    {
        string path = _currentFilePath;
        if (!PathExists(path) || CompareViewerControl.Visibility == Visibility.Visible) return;

        bool isFolder = Directory.Exists(path);
        if (!Views.ConfirmDeleteWindow.Confirm(this, path)) return;

        // Where to go afterwards: decided before the file disappears from Explorer's view
        string? next = GetNeighbor(path, 1);
        if (next == null || string.Equals(next, path, StringComparison.OrdinalIgnoreCase)) next = GetNeighbor(path, -1);
        if (next != null && string.Equals(next, path, StringComparison.OrdinalIgnoreCase)) next = null;

        // Viewers that keep the file open must let go of it first
        MediaViewerControl.Stop();
        SystemPreviewControl.Close();
        if (PdfViewerControl.Visibility == Visibility.Visible) PdfViewerControl.Release();

        try
        {
            if (isFolder)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            App.Log($"[MainWindow] Moved to Recycle Bin: '{path}'");
        }
        catch (OperationCanceledException)
        {
            _ = ShowFile(path); // cancelled in the Windows error dialog: show it again
            return;
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Delete failed: {ex}");
            MessageBox.Show(this, Loc.T("delete.error", ex.Message), "Lupik");
            _ = ShowFile(path);
            return;
        }

        _selectionSet = _selectionSet.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (next != null && PathExists(next)) _ = ShowFile(next);
        else HideWindow();
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
