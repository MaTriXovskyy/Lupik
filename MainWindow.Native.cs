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

/// <summary>Win32 side of the window: handle setup, initial placement and foreground notifications.</summary>
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

        if (_pinned)
        {
            // A pinned window is an ordinary one: it takes the focus when clicked and gets its keys the normal way
            ShowActivated = true;
        }
        else
        {
            // Opening doesn't steal focus, but clicking the preview activates it normally.
            ShowActivated = false;
            WatchForeground();
        }

        ApplyModernStyling(hwnd);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        ApplyModernStyling(helper.Handle);
    }

    private void ApplyModernStyling(IntPtr hwnd)
    {
        ApplyAppearance(); // dark/light hint and backdrop
        try
        {
            int cornerPreference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
        }
        catch { }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        NativeMethods.RefuseAutomation(msg, ref handled);
        if (msg == WM_EXITSIZEMOVE) RememberBounds(); // the user moved or resized the window
        return KeepBackdropActive(hwnd, msg, lParam, ref handled);
    }

    private const int WM_EXITSIZEMOVE = 0x0232;

    private void BringToFront()
    {
        if (_pinned)
        {
            if (!IsVisible) Show();
            return;
        }

        bool opening = !IsVisible || WindowState == WindowState.Minimized;
        // Restored from the taskbar: a minimized preview comes back as it was
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        // Remember the shortcut source only on opening, not when an asynchronous load finishes.
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (opening && !SuppressActivationForTests && foreground != Hwnd && foreground != IntPtr.Zero)
        {
            SourceWindow = GetAncestor(foreground, GA_ROOT);
        }

        Topmost = false;
        if (!IsVisible) Show();
        if (!opening) return;

        // One initial lift, without activation. Immediately return to the normal window band
        // so switching applications can cover the preview, even while loading continues.
        SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetWindowPos(Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    private bool IsTopmostBitSet() => (GetWindowLong(Hwnd, -20) & 0x8) != 0; // WS_EX_TOPMOST

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
    /// Foreground changes only implement the optional close-on-focus-loss setting.
    /// The preview's z-order is independent of Explorer and other foreground windows.
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
        bool fromPreviewContext = ours || root == SourceWindow;
        if (!fromPreviewContext && Settings.Current.CloseOnFocusLoss && hwnd != IntPtr.Zero && !IsShellWindow(root))
        {
            App.Log($"[MainWindow] Another app in front ({DescribeWindow(root)}): closing (CloseOnFocusLoss)");
            HideWindow();
            return;
        }
        // SourceWindow only routes shortcuts and the optional close-on-focus-loss setting.
        // Foreground changes must never reorder the preview.
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
