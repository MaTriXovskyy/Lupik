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

/// <summary>Win32 side of the window: handle setup, staying on top of its Explorer window, z-order diagnostics.</summary>
public partial class MainWindow
{
    private void InitNativeHandle()
    {
        var helper = new WindowInteropHelper(this);
        IntPtr hwnd = helper.EnsureHandle();
        Hwnd = hwnd;

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(HwndHook);

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
        NativeMethods.RefuseAutomation(msg, ref handled);
        return IntPtr.Zero;
    }

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
            WindowState = WindowState.Normal;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
}
