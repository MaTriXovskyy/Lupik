using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Lupik.Views;

/// <summary>
/// Hosts a Windows shell preview handler (the same previewers Explorer's preview pane uses):
/// Word/Excel/PowerPoint with Office installed, Outlook .msg, fonts, SVG, and anything else with a registered previewer.
/// </summary>
public class PreviewHandlerHost : HwndHost
{
    private const string PreviewHandlerKey = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";

    private IntPtr _hwnd;
    private IPreviewHandler? _handler;
    private IStream? _stream;

    /// <summary>Finds the CLSID of the preview handler registered for a file extension, if any.</summary>
    public static Guid? FindHandler(string extension)
    {
        try
        {
            string? Lookup(string path) =>
                Registry.ClassesRoot.OpenSubKey($@"{path}\ShellEx\{PreviewHandlerKey}")?.GetValue(null) as string;

            string? clsid = Lookup(extension) ?? Lookup($@"SystemFileAssociations\{extension}");
            if (clsid == null && Registry.ClassesRoot.OpenSubKey(extension)?.GetValue(null) is string progId)
                clsid = Lookup(progId);

            return clsid != null && Guid.TryParse(clsid, out var guid) ? guid : null;
        }
        catch
        {
            return null;
        }
    }

    // --- Cleanup of previewer processes ---
    // Office previewers run inside a hidden WINWORD/EXCEL/POWERPNT "-Embedding" process that stays alive
    // forever after the preview is closed (~170 MB for Word). We remember the ones we caused to start and
    // end them a while after the preview closes, if they still have no visible window (i.e. nobody is using them).

    private static readonly string[] PreviewerProcesses = { "WINWORD", "EXCEL", "POWERPNT", "prevhost" };
    private static readonly HashSet<int> StartedByUs = new();
    private static System.Windows.Threading.DispatcherTimer? _cleanupTimer;

    private static HashSet<int> RunningPreviewers()
    {
        var ids = new HashSet<int>();
        foreach (var name in PreviewerProcesses)
            foreach (var p in Process.GetProcessesByName(name)) { ids.Add(p.Id); p.Dispose(); }
        return ids;
    }

    private void ScheduleProcessCleanup()
    {
        if (StartedByUs.Count == 0) return;
        _cleanupTimer?.Stop();
        _cleanupTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _cleanupTimer.Tick += (_, _) =>
        {
            _cleanupTimer!.Stop();
            if (_handler != null) return; // a new preview is showing and may be using them
            foreach (int id in StartedByUs.ToArray())
            {
                try
                {
                    using var p = Process.GetProcessById(id);
                    if (HasVisibleWindow(id))
                    {
                        App.Log($"[PreviewHandlerHost] Keeping {p.ProcessName} ({id}): it has a visible window now");
                    }
                    else
                    {
                        App.Log($"[PreviewHandlerHost] Ending idle previewer process {p.ProcessName} ({id})");
                        p.Kill();
                    }
                }
                catch (ArgumentException) { /* already exited */ }
                catch (Exception ex) { App.Log($"[PreviewHandlerHost] Could not end previewer {id}: {ex.Message}"); }
                StartedByUs.Remove(id);
            }
        };
        _cleanupTimer.Start();
    }

    private static bool HasVisibleWindow(int processId)
    {
        bool found = false;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == processId && IsWindowVisible(hwnd)) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Loads the file into its preview handler. Returns false if the handler couldn't be started.</summary>
    public bool Open(string filePath, Guid clsid)
    {
        Close();
        _cleanupTimer?.Stop(); // the previewer process may be reused for this file
        var before = RunningPreviewers();
        try
        {
            var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
            object handler = Activator.CreateInstance(type)!;
            foreach (int id in RunningPreviewers())
                if (!before.Contains(id)) StartedByUs.Add(id);

            if (handler is IInitializeWithFile withFile)
            {
                withFile.Initialize(filePath, 0 /* STGM_READ */);
            }
            else if (handler is IInitializeWithStream withStream)
            {
                int hr = SHCreateStreamOnFileEx(filePath, 0x40 /* STGM_READ | STGM_SHARE_DENY_NONE */, 0, false, null, out var stream);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                _stream = stream;
                withStream.Initialize(stream, 0);
            }
            else
            {
                Marshal.FinalReleaseComObject(handler);
                return false;
            }

            _handler = (IPreviewHandler)handler;
            if (_hwnd != IntPtr.Zero) AttachAndPreview();
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[PreviewHandlerHost] Could not start previewer {clsid} for '{filePath}': {ex.Message}");
            Close();
            return false;
        }
    }

    public void Close()
    {
        if (_handler != null)
        {
            try { _handler.Unload(); } catch { /* handler already gone */ }
            try { Marshal.FinalReleaseComObject(_handler); } catch { }
            _handler = null;
        }
        if (_stream != null)
        {
            try { Marshal.FinalReleaseComObject(_stream); } catch { }
            _stream = null;
        }
        ScheduleProcessCleanup();
    }

    private void AttachAndPreview()
    {
        if (_handler == null) return;
        var rect = ClientRect();
        _handler.SetWindow(_hwnd, ref rect);
        _handler.DoPreview();
    }

    private RECT ClientRect()
    {
        GetClientRect(_hwnd, out var rect);
        return rect;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;
        _hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_handler != null) Dispatcher.InvokeAsync(AttachAndPreview);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Close();
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_handler == null || _hwnd == IntPtr.Zero) return;
        var rect = ClientRect();
        try { _handler.SetRect(ref rect); } catch { /* handler died */ }
    }

    // --- Native interop ---

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(ref MSG pmsg);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize(IStream pstream, uint grfMode);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string pszFile, uint grfMode, uint dwAttributes, bool fCreate,
        IStream? pstmTemplate, out IStream ppstm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
