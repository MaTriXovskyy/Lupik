using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Lupik.Core;

internal enum ScreenshotMode { Region, Window, Monitor, Desktop, Freeform, Scroll }
internal static class ScreenshotModes
{
    internal static bool Remembered(ScreenshotMode mode) => mode is ScreenshotMode.Region or ScreenshotMode.Window;
    internal static ScreenshotMode Restore(string? saved) =>
        Enum.TryParse<ScreenshotMode>(saved,ignoreCase:true,out var mode) && Remembered(mode) ? mode : ScreenshotMode.Region;
}
internal sealed record ScreenshotSelection(ScreenshotMode Mode, Rectangle Area, ScreenshotWindow? Window = null, IReadOnlyList<Point>? Outline = null, bool TextOnly = false);
internal sealed record ScreenshotWindow(IntPtr Handle, uint Process, Rectangle Bounds, Rectangle NativeBounds);

/// <summary>A frozen top-to-bottom window list, taken before the selection overlays exist.</summary>
internal static class ScreenshotWindows
{
    internal static ScreenshotWindow[] Snapshot()
    {
        var result = new List<ScreenshotWindow>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || GetWindowTextLength(hwnd) == 0) return true;
            var name = new StringBuilder(100); NativeMethods.GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if ((GetWindowLong(hwnd, -20) & 0x80) != 0) return true; // tooltips and tool palettes
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(hwnd, out var native)) return true;
            var bounds = native;
            if (DwmGetWindowAttribute(hwnd, 9, out NativeRect frame, Marshal.SizeOf<NativeRect>()) == 0) bounds = frame;
            var area = bounds.Rectangle;
            if (area.Width < 2 || area.Height < 2) return true;
            GetWindowThreadProcessId(hwnd, out uint process);
            result.Add(new(hwnd, process, area, native.Rectangle));
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }

    internal static ScreenshotWindow? At(IEnumerable<ScreenshotWindow> windows, Point point) => windows.FirstOrDefault(w => w.Bounds.Contains(point));
    internal static bool StillExists(ScreenshotWindow window) =>
        GetWindowThreadProcessId(window.Handle, out uint process) != 0 && process == window.Process;
    internal static bool InSamePosition(ScreenshotWindow window) =>
        GetWindowRect(window.Handle,out var bounds) && bounds.Rectangle==window.NativeBounds;

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        internal readonly Rectangle Rectangle => Rectangle.FromLTRB(Left,Top,Right,Bottom);
    }
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
