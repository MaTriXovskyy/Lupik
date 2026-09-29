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
using QuickPeek.Core;

namespace QuickPeek;

public partial class MainWindow : Window
{
    private string _currentFilePath = "";
    private int _showToken;
    private static readonly string[] ImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".heic", ".heif", ".psd", ".avif", ".bmp", ".gif", ".webp", ".ico", ".tiff", ".tif"
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
        Settings.Changed += ApplyHotkey;

        // Borderless windows lack the minimize style, so clicking the taskbar button wouldn't minimize/restore
        const int GWL_STYLE = -16, WS_MINIMIZEBOX = 0x20000;
        SetWindowLong(hwnd, GWL_STYLE, GetWindowLong(hwnd, GWL_STYLE) | WS_MINIMIZEBOX);

        ApplyModernStyling(hwnd);
    }

    /// <summary>(Re)registers the global hotkey chosen in the tray settings.</summary>
    private void ApplyHotkey()
    {
        NativeMethods.UnregisterHotKey(Hwnd, 9001);

        uint? modifiers = Settings.Current.Hotkey switch
        {
            GlobalHotkey.CtrlSpace => NativeMethods.MOD_CONTROL,
            GlobalHotkey.CtrlAltSpace => NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
            _ => null,
        };
        if (modifiers == null)
        {
            App.Log("[MainWindow] Global hotkey disabled");
            return;
        }

        bool ok = NativeMethods.RegisterHotKey(Hwnd, 9001, modifiers.Value | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_SPACE);
        App.Log($"[MainWindow] Registered HotKey {Settings.Current.Hotkey}: {ok}");
    }

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
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == 9001)
        {
            App.Log("[MainWindow] WM_HOTKEY Ctrl+Space triggered!");
            ToggleWindow();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void ShowWelcome()
    {
        App.Log("[MainWindow] ShowWelcome called.");
        _currentFilePath = "";
        TitleFileNameText.Text = "QuickPeek - Gotowy do działania";
        TitleIconImage.Source = null;
        Title = "QuickPeek";
        FileActions.Visibility = SaveAsButton.Visibility = Visibility.Collapsed; // nothing to act on

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
            else if (ext == ".pdf")
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
    /// never looks like QuickPeek ignored the key press.
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
                ShowOnlyViewer(LoadingOverlay); // nothing stale behind the overlay
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
        Title = $"{Path.GetFileName(filePath)} — QuickPeek"; // taskbar / Alt+Tab label
        UpdateTitleIcon(filePath);
        FileActions.Visibility = Visibility.Visible;
        PrintActionButton.Visibility = CanPrintCurrent() ? Visibility.Visible : Visibility.Collapsed;

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
        foreach (var viewer in new UIElement[] { WelcomeView, ImageViewerControl, CompareViewerControl, CodeViewerControl, PdfViewerControl, GenericViewerControl, ArchiveViewerControl, CsvViewerControl, FolderViewerControl, SystemPreviewControl })
        {
            var visibility = viewer == target ? Visibility.Visible : Visibility.Collapsed;
            // Release the system previewer (it may hold the file open) as soon as it's not shown
            if (viewer == SystemPreviewControl && visibility == Visibility.Collapsed) SystemPreviewControl.Close();
            if (viewer.Visibility != visibility) viewer.Visibility = visibility;
        }
    }

    /// <summary>
    /// Shows the window above everything else. Topmost is only held for the moment of opening,
    /// so other windows can cover the preview again once the user switches away.
    /// </summary>
    /// <summary>
    /// For automated tests only: show the window without activating it, so a test never steals
    /// keyboard focus from whatever the user is doing.
    /// </summary>
    internal static bool SuppressActivationForTests;

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

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        App.Log($"[MainWindow] BringToFront: IsVisible={IsVisible}, IsActive={IsActive}, foreground={DescribeWindow(foreground)}");

        // Already in front (e.g. arrow-key navigation). Ask Windows, not WPF: WPF's IsActive can stay "true"
        // after Explorer took the focus (seen in the log), which left the preview stuck behind Explorer.
        if (IsVisible && foreground == Hwnd) return;

        if (foreground != Hwnd && foreground != IntPtr.Zero) _returnFocusTo = foreground;
        Topmost = true;

        if (!IsVisible)
        {
            Show();
        }

        ForceForeground();
        Activate();
        Keyboard.Focus(this); // arrow keys work immediately, without clicking the preview first

        App.Log($"[MainWindow] BringToFront: after activation foreground={DescribeWindow(NativeMethods.GetForegroundWindow())}");

        // Dropping Topmost keeps the window at the top of the normal z-order
        Dispatcher.InvokeAsync(() =>
        {
            Topmost = false;
            App.Log($"[MainWindow] BringToFront: topmost dropped, foreground={DescribeWindow(NativeMethods.GetForegroundWindow())}, covered by: {WindowsAbove()}");
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // --- Diagnostics for z-order problems ---

    private const uint GW_HWNDPREV = 3;
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

    /// <summary>The window that had the focus before the preview opened (Explorer / Desktop).</summary>
    private IntPtr _returnFocusTo;
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    private string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        if (hwnd == Hwnd) return "QuickPeek";
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

    /// <summary>
    /// Windows refuses SetForegroundWindow when the key press belonged to another app (Explorer),
    /// so the preview would open without focus and arrows would keep going to Explorer.
    /// Briefly joining the foreground thread's input queue lets us take focus.
    /// </summary>
    private void ForceForeground()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == Hwnd) return;

        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint ourThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != ourThread && AttachThreadInput(ourThread, fgThread, true);
        try
        {
            NativeMethods.SetForegroundWindow(Hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, fgThread, false);
        }
    }

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

        // Hand the focus back to the window Space was pressed in (Explorer / Desktop). Otherwise Windows picks
        // "the next window in z-order", which after our Topmost toggle can be any app (e.g. seen: Claude).
        // Only while we're the foreground window: if the user already switched elsewhere, leave it alone.
        IntPtr back = _returnFocusTo;
        _returnFocusTo = IntPtr.Zero;
        if (back != IntPtr.Zero && NativeMethods.GetForegroundWindow() == Hwnd && IsWindow(back) && IsWindowVisible(back))
        {
            // Plain SetForegroundWindow is refused here (the Space went to our hook, not to a window, so Windows
            // doesn't count it as "input to us"); joining the target's input queue lifts that restriction.
            uint targetThread = GetWindowThreadProcessId(back, IntPtr.Zero);
            uint ourThread = GetCurrentThreadId();
            bool attached = targetThread != 0 && targetThread != ourThread && AttachThreadInput(ourThread, targetThread, true);
            bool ok;
            try
            {
                ok = NativeMethods.SetForegroundWindow(back);
            }
            finally
            {
                if (attached) AttachThreadInput(ourThread, targetThread, false);
            }
            App.Log($"[MainWindow] HideWindow: focus returned to {DescribeWindow(back)} (ok={ok})");
        }
        Hide();
        _showToken++; // cancel any image still loading
        EndLoading();
        if (_isFullScreen) ToggleFullScreen(); // next preview opens as a normal window
        ResetFolderHistory();
        SystemPreviewControl.Close();
        SystemPreviewControl.Visibility = Visibility.Collapsed;
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

        if (!string.IsNullOrEmpty(selected) && PathExists(selected) && !samePairShown &&
            (ComparePair() != null || !string.Equals(selected, _currentFilePath, StringComparison.OrdinalIgnoreCase)))
        {
            ShowSelection(selected);
        }
        else
        {
            HideWindow();
        }
    }

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

        double w = Math.Min(960, work.Width * 0.85);
        double h = Math.Min(680, work.Height * 0.85);

        if (ext == ".pdf")
        {
            w = Math.Min(880, work.Width * 0.80);
            h = Math.Min(900, work.Height * 0.90);
        }

        return Centered(work, w, h);
    }

    private Rect ComputeImageBounds(double imgWidth, double imgHeight)
    {
        if (imgWidth <= 0 || imgHeight <= 0) return ComputeDefaultBounds("");
        if (!TryGetWorkArea(out var work, out _, out _)) return new Rect(Left, Top, Width, Height);

        double maxW = work.Width * 0.85;
        double maxH = work.Height * 0.85;

        const double chrome = 38 + 36; // title bar + image footer
        double w = imgWidth;
        double h = imgHeight + chrome;

        if (w > maxW || h > maxH)
        {
            double scaleFactor = Math.Min(maxW / w, (maxH - chrome) / imgHeight);
            w = imgWidth * scaleFactor;
            h = (imgHeight * scaleFactor) + chrome;
        }

        return Centered(work, Math.Max(w, 480), Math.Max(h, 360));
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
        bool imageShown = ImageViewerControl.Visibility == Visibility.Visible;
        bool csvShown = CsvViewerControl.Visibility == Visibility.Visible;

        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveAs();
            e.Handled = true;
        }
        else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenPrintDialog();
            e.Handled = true;
        }
        else if (imageShown && e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ImageViewerControl.CopyImageToClipboard();
            e.Handled = true;
        }
        else if (imageShown && e.Key == Key.R && (Keyboard.Modifiers & ~ModifierKeys.Shift) == 0)
        {
            ImageViewerControl.Rotate(Keyboard.Modifiers == ModifierKeys.Shift ? -90 : 90);
            e.Handled = true;
        }
        else if (imageShown && Keyboard.Modifiers == 0 && e.Key is Key.OemPlus or Key.Add)
        {
            ImageViewerControl.ZoomBy(1.25);
            e.Handled = true;
        }
        else if (imageShown && Keyboard.Modifiers == 0 && e.Key is Key.OemMinus or Key.Subtract)
        {
            ImageViewerControl.ZoomBy(1 / 1.25);
            e.Handled = true;
        }
        else if (imageShown && e.Key == Key.I && Keyboard.Modifiers == 0)
        {
            ImageViewerControl.ToggleInfo();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == 0 && e.Key == Key.C && (CompareViewerControl.Visibility == Visibility.Visible || (imageShown && ComparePair() != null)))
        {
            ToggleCompare(); // 2 images selected: side by side ⇄ single
            e.Handled = true;
        }
        else if (CompareViewerControl.Visibility == Visibility.Visible && CompareViewerControl.HandleKey(e.Key))
        {
            e.Handled = true; // +/−/0, S (mode), X (swap); arrows do nothing here
        }
        else if (imageShown && Keyboard.Modifiers == 0 && e.Key is Key.D0 or Key.NumPad0)
        {
            ImageViewerControl.ResetZoom();
            e.Handled = true;
        }
        else if (csvShown && e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CsvViewerControl.CopySelection();
            e.Handled = true;
        }
        else if (csvShown && e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CsvViewerControl.SelectAll();
            e.Handled = true;
        }
        else if (csvShown && e.Key == Key.Escape && CsvViewerControl.HasSelection)
        {
            CsvViewerControl.ClearSelection(); // first Esc clears the selection, the next one closes
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && (e.SystemKey == Key.Left || e.SystemKey == Key.Right))
        {
            if (e.SystemKey == Key.Left) GoBackToFolder(); else GoForward(); // Alt+←/→ like in Explorer
            e.Handled = true;
        }
        else if ((e.Key == Key.Back || e.Key == Key.Escape) && Keyboard.Modifiers == 0 && _folderHistory.Count > 0 && !_isFullScreen)
        {
            GoBackToFolder(); // opened from a folder preview: back to the folder instead of closing
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullScreen)
        {
            ToggleFullScreen(); // first Esc leaves full screen, the next one closes
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == 0 && !string.IsNullOrEmpty(_currentFilePath))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == 0)
        {
            OpenInDefaultApp();
            e.Handled = true;
        }
        else if (e.Key == Key.Space || e.Key == Key.Escape)
        {
            HideWindow();
            e.Handled = true;
        }
        else if ((e.Key == Key.Up || e.Key == Key.Down) && CodeViewerControl.Visibility == Visibility.Visible)
        {
            // In the code preview ↑/↓ scroll the text; ←/→ still switch files
            CodeViewerControl.ScrollLines(e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up || e.Key == Key.Left)
        {
            NavigateAdjacent(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down || e.Key == Key.Right)
        {
            NavigateAdjacent(1);
            e.Handled = true;
        }
    }

    private bool _isFullScreen;

    /// <summary>
    /// Full screen for a slideshow feel: no title bar, window covers the whole monitor (incl. taskbar).
    /// Arrow-key navigation keeps working and stays in full screen.
    /// </summary>
    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        TitleRow.Height = new GridLength(_isFullScreen ? 0 : 38);
        TitleBar.Visibility = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        Background = _isFullScreen ? System.Windows.Media.Brushes.Black : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x18, 0x1B));
        WindowState = _isFullScreen ? WindowState.Maximized : WindowState.Normal;

        if (!_isFullScreen && !string.IsNullOrEmpty(_currentFilePath))
        {
            SetBounds(ImageViewerControl.Visibility == Visibility.Visible
                ? ComputeImageBounds(ImageViewerControl.NaturalWidth, ImageViewerControl.NaturalHeight)
                : ComputeDefaultBounds(Path.GetExtension(_currentFilePath).ToLowerInvariant()));
        }
    }

    private void OnSaveAsClicked(object sender, RoutedEventArgs e) => SaveAs();

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
            MessageBox.Show(this, $"Nie udało się przygotować wydruku:\n{ex.Message}", "QuickPeek");
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
            Title = "Zapisz jako",
            InitialDirectory = Path.GetDirectoryName(source),
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (isImage)
        {
            string original = $"Oryginał ({ext.TrimStart('.').ToUpperInvariant()})|*{ext}";
            dialog.Filter = string.Join("|", ExportFormats.Select(f => $"{f.Label} (*{f.Ext})|*{f.Ext}").Prepend(original));
            // HEIC/HEIF default to JPEG, since most apps can't open them
            bool preferJpeg = ext is ".heic" or ".heif";
            dialog.FilterIndex = preferJpeg ? 3 : 1;
            dialog.FileName = baseName + (preferJpeg ? " (kopia).jpg" : " (kopia)" + ext);
        }
        else
        {
            dialog.Filter = $"{(ext.Length > 0 ? ext.TrimStart('.').ToUpperInvariant() : "Plik")}|*{(ext.Length > 0 ? ext : ".*")}|Wszystkie pliki|*.*";
            dialog.FileName = baseName + " (kopia)" + ext;
        }

        if (dialog.ShowDialog(this) != true) return;
        string target = dialog.FileName;

        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Wybierz inną nazwę lub folder niż oryginalny plik.", "QuickPeek");
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
                    using var image = new ImageMagick.MagickImage(source);
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
            ShowToast($"Zapisano: {Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            App.Log($"[MainWindow] Save As failed: {ex}");
            MessageBox.Show(this, $"Nie udało się zapisać pliku:\n{ex.Message}", "QuickPeek");
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
        SaveAsButton.ToolTip = "Zapisz jako… (Ctrl+S)";
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
            Title = $"Porównanie — QuickPeek";
            FileActions.Visibility = SaveAsButton.Visibility = Visibility.Collapsed; // which file would they act on?
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
        return Centered(work, Math.Min(1500, work.Width * 0.9), Math.Min(900, work.Height * 0.88));
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

    private static string SamplePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "test_samples", fileName);

    private void OnSampleImageClicked(object sender, RoutedEventArgs e)
    {
        string sample = SamplePath("sample_image.png");
        if (File.Exists(sample)) _ = ShowFile(sample);
    }

    private void OnSampleCodeClicked(object sender, RoutedEventArgs e)
    {
        string sample = SamplePath("sample_code.cs");
        if (File.Exists(sample)) _ = ShowFile(sample);
    }

    private void OnSamplePdfClicked(object sender, RoutedEventArgs e)
    {
        string sample = SamplePath("sample_document.pdf");
        if (File.Exists(sample)) _ = ShowFile(sample);
    }
}
