using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Lupik.Core;

public static class ExplorerService
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    private const uint GA_ROOT = 2;

    /// <summary>All files selected at the last <see cref="GetSelectedFilePath"/> call (for navigating a multi-selection).</summary>
    public static IReadOnlyList<string> LastSelection { get; private set; } = Array.Empty<string>();

    private static List<string> ReadSelection(dynamic? items)
    {
        var paths = new List<string>();
        if (items == null) return paths;
        int count = items.Count;
        for (int i = 0; i < count; i++)
        {
            string? path = items.Item(i)?.Path;
            // Files and folders (folders get the folder preview)
            if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path))) paths.Add(path);
        }
        return paths;
    }

    public static string? GetSelectedFilePath()
    {
        LastSelection = Array.Empty<string>();
        IntPtr fgWnd = NativeMethods.GetForegroundWindow();
        IntPtr rootWnd = IntPtr.Zero;
        string className = "";
        string rootClass = "";
        bool isDesktop = false;

        if (fgWnd != IntPtr.Zero)
        {
            rootWnd = GetAncestor(fgWnd, GA_ROOT);
            if (rootWnd == IntPtr.Zero) rootWnd = fgWnd;

            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(fgWnd, sb, sb.Capacity);
            className = sb.ToString();

            var sbRoot = new StringBuilder(256);
            NativeMethods.GetClassName(rootWnd, sbRoot, sbRoot.Capacity);
            rootClass = sbRoot.ToString();

            App.Log($"[ExplorerService] fgWnd: 0x{fgWnd:X} ('{className}'), rootWnd: 0x{rootWnd:X} ('{rootClass}')");

            isDesktop = className == "Progman" || className == "WorkerW" ||
                        rootClass == "Progman" || rootClass == "WorkerW" ||
                        className == "SHELLDLL_DefView" || rootClass == "SHELLDLL_DefView" ||
                        className == "SysListView32" || rootClass == "SysListView32";
        }
        else
        {
            App.Log("[ExplorerService] fgWnd is IntPtr.Zero, inspecting Shell windows and Desktop directly.");
        }

        return GetSelectedFromShellWindows(fgWnd, rootWnd, isDesktop);
    }

    private static string? GetSelectedFromShellWindows(IntPtr fgWnd, IntPtr rootWnd, bool isDesktop)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
            {
                App.Log("[ExplorerService] Shell.Application ProgID not found");
                return null;
            }

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;

            dynamic windows = shell.Windows();

            // 1. If Desktop is active, prioritize Desktop
            if (isDesktop)
            {
                string? desktopPath = GetDesktopSelection(windows);
                if (!string.IsNullOrEmpty(desktopPath))
                {
                    return desktopPath;
                }
            }

            // 2. Explorer window: search through open ShellWindows
            int count = windows.Count;
            App.Log($"[ExplorerService] Scanning {count} open shell windows for matching HWND...");

            long fgHwnd32 = fgWnd.ToInt64() & 0xFFFFFFFFL;
            long rootHwnd32 = rootWnd.ToInt64() & 0xFFFFFFFFL;

            List<string>? fallbackCandidate = null;
            // Explorer tabs all share the window's HWND: only the visible tab is the one the user is looking at
            IntPtr activeTab = ActiveTab(rootWnd);

            for (int i = 0; i < count; i++)
            {
                try
                {
                    dynamic? item = windows.Item(i);
                    if (item == null) continue;

                    long itemHwnd = (long)item.HWND & 0xFFFFFFFFL;
                    App.Log($"[ExplorerService] Window {i}: HWND=0x{itemHwnd:X}, Location='{item.LocationName}'");

                    bool sameWindow = fgWnd != IntPtr.Zero && (itemHwnd == fgHwnd32 || itemHwnd == rootHwnd32);
                    if (sameWindow && activeTab != IntPtr.Zero && TabOf((object)item) is IntPtr tab && tab != IntPtr.Zero && tab != activeTab)
                    {
                        App.Log($"[ExplorerService] Window {i} is a background tab, skipped");
                        continue;
                    }
                    if (sameWindow)
                    {
                        dynamic? doc = item.Document;
                        if (doc != null)
                        {
                            var paths = ReadSelection(doc.SelectedItems());
                            if (paths.Count > 0)
                            {
                                App.Log($"[ExplorerService] Exact HWND match! Selected file: {paths[0]} (+{paths.Count - 1} more)");
                                LastSelection = paths;
                                return paths[0];
                            }

                            // The active Explorer window has nothing selected: don't fall back to some
                            // other window's or the Desktop's selection (it opened unrelated files)
                            App.Log("[ExplorerService] Active Explorer window has no selection");
                            return null;
                        }
                    }
                    else
                    {
                        dynamic? doc = item.Document;
                        if (doc != null)
                        {
                            var paths = ReadSelection(doc.SelectedItems());
                            if (paths.Count > 0)
                            {
                                fallbackCandidate = paths;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[ExplorerService] Error inspecting window {i}: {ex.Message}");
                }
            }

            // 3. Fallback to Desktop if not already checked
            if (!isDesktop)
            {
                string? desktopPath = GetDesktopSelection(windows);
                if (!string.IsNullOrEmpty(desktopPath))
                {
                    return desktopPath;
                }
            }

            // 4. Fallback to open Explorer selection candidate
            if (fallbackCandidate != null)
            {
                App.Log($"[ExplorerService] Using fallback candidate: {fallbackCandidate[0]}");
                LastSelection = fallbackCandidate;
                return fallbackCandidate[0];
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ExplorerService] Fatal COM error: {ex}");
        }

        return null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>The visible tab (ShellTabWindowClass) of an Explorer window, or zero (no tabs / not Explorer).</summary>
    private static IntPtr ActiveTab(IntPtr explorerWindow)
    {
        if (explorerWindow == IntPtr.Zero) return IntPtr.Zero;
        for (IntPtr tab = FindWindowEx(explorerWindow, IntPtr.Zero, "ShellTabWindowClass", null);
             tab != IntPtr.Zero;
             tab = FindWindowEx(explorerWindow, tab, "ShellTabWindowClass", null))
        {
            if (IsWindowVisible(tab)) return tab;
        }
        return IntPtr.Zero;
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? obj);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr hwnd); // from IOleWindow; the rest of the interface isn't needed
    }

    private static Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    /// <summary>The tab window a shell view lives in (its IShellBrowser's window).</summary>
    private static IntPtr TabOf(object shellWindow)
    {
        try
        {
            if (shellWindow is IServiceProvider sp &&
                sp.QueryService(ref SID_STopLevelBrowser, ref IID_IShellBrowser, out var obj) == 0 &&
                obj is IShellBrowser browser && browser.GetWindow(out var hwnd) == 0)
                return hwnd;
        }
        catch (Exception ex)
        {
            App.Log($"[ExplorerService] Tab lookup failed: {ex.Message}");
        }
        return IntPtr.Zero;
    }

    private static string? GetDesktopSelection(dynamic windows)
    {
        try
        {
            object vEmpty = Type.Missing;
            int phwnd = 0;
            // SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1
            dynamic? desktopView = windows.FindWindowSW(ref vEmpty, ref vEmpty, 8, out phwnd, 1);
            if (desktopView != null)
            {
                dynamic? doc = desktopView.Document;
                if (doc != null)
                {
                    var paths = ReadSelection(doc.SelectedItems());
                    if (paths.Count > 0)
                    {
                        App.Log($"[ExplorerService] Desktop selected item found: {paths[0]} (+{paths.Count - 1} more)");
                        LastSelection = paths;
                        return paths[0];
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ExplorerService] Error querying Desktop view: {ex.Message}");
        }

        return null;
    }

    private static (string Dir, DateTime Stamp, List<string> Files)? _folderCache;

    /// <summary>
    /// The folder's visible files in Explorer's name order ("2.jpg" before "10.jpg"). Remembered until the folder
    /// changes: flipping through a big folder doesn't list it again on every step.
    /// </summary>
    public static List<string> FolderFiles(string dir)
    {
        var stamp = Directory.GetLastWriteTimeUtc(dir);
        if (_folderCache is var (cachedDir, cachedStamp, cached) && cachedStamp == stamp &&
            string.Equals(cachedDir, dir, StringComparison.OrdinalIgnoreCase))
            return cached;

        var files = new DirectoryInfo(dir).EnumerateFiles()
            .Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden))
            .Select(f => f.FullName)
            .ToList();
        files.Sort((a, b) => StrCmpLogicalW(a, b));
        _folderCache = (dir, stamp, files);
        return files;
    }

    public static string? GetAdjacentFile(string currentPath, int direction)
    {
        try
        {
            if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath))
                return null;

            string? dir = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;

            var files = FolderFiles(dir);

            if (files.Count <= 1)
                return currentPath;

            int currentIndex = files.FindIndex(f => string.Equals(f, currentPath, StringComparison.OrdinalIgnoreCase));
            if (currentIndex == -1)
                return null;

            int newIndex = currentIndex + direction;
            if (newIndex < 0)
                newIndex = files.Count - 1;
            else if (newIndex >= files.Count)
                newIndex = 0;

            return files[newIndex];
        }
        catch
        {
            return null;
        }
    }
}
